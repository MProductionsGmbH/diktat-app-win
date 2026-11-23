using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Diktat.Windows.Platform;

/// Asks Microsoft's Foundry Local where it runs: "foundry service status" prints the address with
/// the port the service picked when it started. The tool comes with the Foundry Local install
/// (winget install Microsoft.FoundryLocal) and is found on the PATH.
public static class FoundryLocalCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// The tool's output, or null when Foundry Local is not installed or does not answer in time.
    public static async Task<string?> StatusAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("foundry", "service status")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Win32Exception)
        {
            // Not installed: "foundry" is not on the PATH.
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false) + "\n" + await errors.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warn("Foundry Local did not answer in time");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log.Warn("Reading Foundry Local's status failed", ex);
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
    }
}
