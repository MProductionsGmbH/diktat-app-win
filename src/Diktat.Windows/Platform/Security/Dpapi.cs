using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Diktat.Core.Support;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// DPAPI (CryptProtectData), CurrentUser scope: only this Windows user on this PC can decrypt.
/// Called directly through crypt32, so no extra package is needed.
internal static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Diktat.Windows.v1");

    public static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new Win32.DATA_BLOB { cbData = input.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new Win32.DATA_BLOB { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? Win32.CryptProtectData(ref inBlob, "Diktat", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : Win32.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new CryptographicException(protect ? "DPAPI protect failed" : "DPAPI unprotect failed", error);
            }
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                Win32.LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }
}

/// <see cref="IDataProtector"/> with DPAPI for the current user (history, meetings at rest).
/// Throws <see cref="CryptographicException"/> for data from another user or PC.
public sealed class DpapiDataProtector : IDataProtector
{
    public byte[] Protect(byte[] plain) => Dpapi.Protect(plain);
    public byte[] Unprotect(byte[] protectedData) => Dpapi.Unprotect(protectedData);
}

/// <see cref="ISecretStore"/>: one DPAPI-encrypted file per name in %APPDATA%\Diktat\secrets.
/// Values are trimmed; an empty value deletes. A file that cannot be decrypted (profile copied
/// to another PC) reads as missing. Thread-safe.
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly object _gate = new();

    public DpapiSecretStore(string? directory = null)
    {
        _directory = directory ?? AppPaths.SecretsDirectory;
    }

    public string? Get(string name)
    {
        var path = PathFor(name);
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(path)));
            }
            catch (Exception ex)
            {
                Log.Warn($"Secret '{name}' unreadable", ex);
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete(name);
            return;
        }
        var path = PathFor(name);
        var data = Dpapi.Protect(Encoding.UTF8.GetBytes(value.Trim()));
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            try { File.Delete(PathFor(name)); } catch (DirectoryNotFoundException) { }
        }
    }

    /// File name from the secret name: safe characters kept, everything else hex-encoded, so
    /// names never collide and never escape the directory.
    private string PathFor(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Secret name must not be empty.", nameof(name));
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') builder.Append(c);
            else builder.Append('.').Append(b.ToString("x2"));
        }
        return Path.Combine(_directory, builder + ".secret");
    }
}
