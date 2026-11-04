using System.Windows.Threading;
using Diktat.AI.Updates;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// Looks for a newer Diktat on GitHub: shortly after the start and then once a day, while the
/// setting allows it. There is no automatic install; the window offers the download.
public sealed class UpdateScheduler : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private string? _announcedVersion;

    /// A newer version was found by the automatic check (once per version and run).
    public event EventHandler<UpdateInfo>? UpdateFound;

    public UpdateScheduler(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (!_services.Settings.App.ChecksForUpdates) return;
            try
            {
                var update = await CheckAsync(CancellationToken.None);
                if (update is { IsNewer: true } && update.Version != _announcedVersion)
                {
                    _announcedVersion = update.Version;
                    UpdateFound?.Invoke(this, update);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Automatic update check failed: " + ex.Message);
            }
        };
    }

    /// The newest release when it is newer than this version, else null.
    public UpdateInfo? AvailableUpdate { get; private set; }

    public event EventHandler? AvailableUpdateChanged;

    public void Start() => _timer.Start();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        var latest = await _services.Updates.CheckAsync(AppServices.Version, cancellationToken);
        var update = latest is { IsNewer: true } ? latest : null;
        if (update?.Version != AvailableUpdate?.Version)
        {
            AvailableUpdate = update;
            AvailableUpdateChanged?.Invoke(this, EventArgs.Empty);
        }
        Log.Info(update is null ? "No update available" : $"Update available: {update.Version}");
        return update;
    }

    public void Dispose() => _timer.Stop();
}
