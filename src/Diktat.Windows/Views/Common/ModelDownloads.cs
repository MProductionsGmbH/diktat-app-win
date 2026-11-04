using Diktat.Transcription.Models;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views;

/// A model download that the settings and the setup assistant share, so a download started in
/// one shows its progress in the other and never runs twice. UI thread only.
public sealed class ModelDownload
{
    private readonly CancellationTokenSource _cts = new();

    internal ModelDownload(ModelInfo model) => Model = model;

    public ModelInfo Model { get; }
    public double Progress { get; internal set; }
    public bool IsRunning { get; internal set; } = true;
    public string? Error { get; internal set; }
    public bool WasCancelled { get; internal set; }

    /// Raised on the UI thread when progress or state changed.
    public event EventHandler? Changed;

    public string StatusText => IsRunning
        ? $"Wird geladen … {Math.Round(Progress * 100)} % von {Model.SizeLabel}"
        : Error ?? (WasCancelled ? "Abgebrochen." : "Installiert.");

    public void Cancel() => _cts.Cancel();

    internal CancellationToken Token => _cts.Token;
    internal void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public static class ModelDownloads
{
    private static readonly Dictionary<string, ModelDownload> Running = new();

    /// Raised on the UI thread when a download started or ended.
    public static event EventHandler<ModelDownload>? Changed;

    public static ModelDownload? Find(string modelId) => Running.TryGetValue(modelId, out var d) ? d : null;

    public static bool IsAnyRunning => Running.Count > 0;

    /// Starts the download, or returns the one already running.
    public static ModelDownload Start(AppServices services, ModelInfo model)
    {
        if (Running.TryGetValue(model.Id, out var existing)) return existing;
        var download = new ModelDownload(model);
        Running[model.Id] = download;
        Changed?.Invoke(null, download);
        _ = RunAsync(services, download);
        return download;
    }

    private static async Task RunAsync(AppServices services, ModelDownload download)
    {
        double lastReported = -1;
        var progress = new Progress<double>(value =>
        {
            download.Progress = Math.Clamp(value, 0, 1);
            if (download.Progress - lastReported < 0.002 && download.Progress < 1) return;
            lastReported = download.Progress;
            download.Raise();
        });
        try
        {
            await services.Models.DownloadAsync(download.Model, progress, download.Token);
            download.Progress = 1;
        }
        catch (OperationCanceledException)
        {
            download.WasCancelled = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Model download {download.Model.Id} failed", ex);
            download.Error = ex is ModelDownloadException && !string.IsNullOrWhiteSpace(ex.Message)
                ? ex.Message
                : "Der Download hat nicht geklappt. Bitte die Internetverbindung prüfen und erneut versuchen.";
        }
        finally
        {
            download.IsRunning = false;
            Running.Remove(download.Model.Id);
            download.Raise();
            Changed?.Invoke(null, download);
        }
    }
}
