using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Diktat.Transcription.Models;

/// A download or install problem, with a German message for the UI.
public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// The models on disk: one folder per model id below <see cref="RootDirectory"/>
/// (on Windows %LOCALAPPDATA%\Diktat\Models).
///
/// Downloads stream to "<file>.part" and resume with an HTTP Range request after an
/// interruption (network loss, app closed). A file only gets its final name once its size
/// and checksum match, so a model is either complete or not installed. Each model downloads
/// at most once at a time: a second call waits for the first and then finds it installed.
public sealed class ModelStore
{
    public const string PartSuffix = ".part";
    private const int BufferSize = 1 << 20;
    private const int MaxAttemptsPerFile = 4;

    /// A connection that delivers nothing for this long counts as broken and is resumed.
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// Pause before resuming after a broken connection, times the attempt number.
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ModelStore(string rootDirectory, HttpClient? httpClient = null)
    {
        RootDirectory = rootDirectory;
        _http = httpClient ?? CreateDefaultClient();
    }

    public string RootDirectory { get; }

    /// Whether checksums are verified after a download (on by default; a 650 MB file takes
    /// about two seconds).
    public bool VerifyChecksums { get; init; } = true;

    /// Raised after a model was downloaded or deleted, with its id.
    public event Action<string>? Changed;

    /// %LOCALAPPDATA%\Diktat\Models (or the platform's equivalent).
    public static string DefaultRootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diktat", "Models");

    public string ModelDirectory(ModelInfo model) => Path.Combine(RootDirectory, model.Id);

    public string FilePath(ModelInfo model, ModelFile file) => Path.Combine(ModelDirectory(model), file.FileName);

    public string FilePath(ModelInfo model, string fileName) => Path.Combine(ModelDirectory(model), fileName);

    /// All files are on disk with the expected size.
    public bool IsInstalled(ModelInfo model) =>
        model.Files.All(file => FileLength(FilePath(model, file)) == file.SizeBytes);

    public bool IsInstalled(string id) => ModelCatalog.Find(id) is { } model && IsInstalled(model);

    public IReadOnlyList<ModelInfo> InstalledModels() => ModelCatalog.All.Where(IsInstalled).ToList();

    /// Bytes already on disk for a model, including partial downloads.
    public long DownloadedBytes(ModelInfo model) => model.Files.Sum(file =>
    {
        var path = FilePath(model, file);
        var complete = FileLength(path);
        return complete == file.SizeBytes ? complete : Math.Min(file.SizeBytes, Math.Max(0, FileLength(path + PartSuffix)));
    });

    /// Deletes the model folder, partial downloads included. The caller unloads the model first.
    public void Delete(ModelInfo model)
    {
        var gate = Gate(model);
        if (!gate.Wait(0))
        {
            throw new ModelDownloadException(L.T("Das Modell wird gerade geladen. Bitte zuerst den Download abbrechen."));
        }
        try
        {
            var directory = ModelDirectory(model);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(model.Id);
    }

    /// Downloads the files that are missing. Progress goes from 0 to 1 over all files of the
    /// model. Cancelling keeps the partial files, so the next call continues where this one stopped.
    public async Task DownloadAsync(ModelInfo model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var gate = Gate(model);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled(model))
            {
                progress?.Report(1);
                return;
            }

            var directory = ModelDirectory(model);
            Directory.CreateDirectory(directory);
            EnsureFreeSpace(directory, model.SizeBytes - DownloadedBytes(model));

            var total = Math.Max(1, model.SizeBytes);
            long finishedBytes = 0;
            foreach (var file in model.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = FilePath(model, file);
                if (FileLength(path) == file.SizeBytes)
                {
                    finishedBytes += file.SizeBytes;
                    progress?.Report((double)finishedBytes / total);
                    continue;
                }
                var before = finishedBytes;
                await DownloadFileAsync(file, path, bytes => progress?.Report((double)(before + bytes) / total), ct).ConfigureAwait(false);
                finishedBytes += file.SizeBytes;
            }
            progress?.Report(1);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(model.Id);
    }

    private SemaphoreSlim Gate(ModelInfo model) => _locks.GetOrAdd(model.Id, _ => new SemaphoreSlim(1, 1));

    private async Task DownloadFileAsync(ModelFile file, string path, Action<long> progress, CancellationToken ct)
    {
        var partPath = path + PartSuffix;
        if (File.Exists(path)) File.Delete(path); // wrong size: start over below

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadToPartAsync(file, partPath, progress, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception error) when (IsTransient(error, ct) && attempt < MaxAttemptsPerFile)
            {
                // Resume after a short pause; the .part file keeps what arrived.
                await Task.Delay(RetryDelay * attempt, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (IsTransient(error, ct))
            {
                throw new ModelDownloadException(L.T("Das Modell ließ sich nicht laden. Bitte die Internetverbindung prüfen und erneut versuchen."), error);
            }
        }

        var length = FileLength(partPath);
        if (length != file.SizeBytes)
        {
            File.Delete(partPath);
            throw new ModelDownloadException(L.F("Die geladene Datei {0} hat die falsche Größe ({1} statt {2} Bytes). Bitte erneut laden.", file.FileName, length, file.SizeBytes));
        }
        if (VerifyChecksums && file.Sha256 is { } expected)
        {
            var actual = await Sha256Async(partPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                throw new ModelDownloadException(L.F("Die geladene Datei {0} ist beschädigt. Bitte erneut laden.", file.FileName));
            }
        }
        File.Move(partPath, path, overwrite: true);
    }

    private async Task DownloadToPartAsync(ModelFile file, string partPath, Action<long> progress, CancellationToken ct)
    {
        var existing = FileLength(partPath);
        if (existing > file.SizeBytes)
        {
            File.Delete(partPath);
            existing = 0;
        }
        if (existing == file.SizeBytes && existing > 0)
        {
            progress(existing);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server has nothing after our bytes: the part is complete or broken; the size check decides.
            File.Delete(partPath);
            throw new HttpRequestException("Range not satisfiable", null, response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            if (status is >= 500 or 408 or 429) throw new HttpRequestException($"HTTP {status}", null, response.StatusCode);
            throw new ModelDownloadException(L.F("Das Modell ist nicht verfügbar (HTTP {0}). Bitte später erneut versuchen.", status));
        }

        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && (response.Content.Headers.ContentRange?.From ?? existing) == existing;
        var offset = resumed ? existing : 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buffer = new byte[BufferSize];
        var written = offset;
        progress(written);
        var lastReport = Environment.TickCount64;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("The download stalled");
            }
            if (read == 0) break;
            if (written + read > file.SizeBytes)
            {
                throw new ModelDownloadException(L.F("Die geladene Datei {0} ist größer als erwartet. Bitte erneut laden.", file.FileName));
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            var now = Environment.TickCount64;
            if (now - lastReport >= 100)
            {
                lastReport = now;
                progress(written);
            }
        }
        progress(written);
        if (written < file.SizeBytes)
        {
            // The connection closed early; the retry resumes from here.
            throw new IOException($"Download ended after {written} of {file.SizeBytes} bytes");
        }
    }

    private static bool IsTransient(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested && error is HttpRequestException or IOException or TaskCanceledException
        && error is not ModelDownloadException;

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void EnsureFreeSpace(string directory, long neededBytes)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return; // unknown: let the download try
        }
        // A little room for the rest of the system.
        if (available < neededBytes + 200_000_000)
        {
            var neededMB = Math.Max(1, (neededBytes + 200_000_000) / 1_000_000);
            throw new ModelDownloadException(L.F("Nicht genug freier Speicher. Das Modell braucht rund {0} MB.", neededMB));
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            // Large files: no overall timeout; a stalled connection surfaces as an IOException.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Diktat-Windows/1.0");
        return client;
    }
}
