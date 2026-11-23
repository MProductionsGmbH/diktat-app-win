using System.Windows.Threading;
using Diktat.AI.Meetings;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// Checks the audio apps every three seconds while no meeting records and reports when a call
/// starts (CallDetector.swift). UI thread only; the check itself runs in the background.
public sealed class CallMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly CallStreaks _streaks = new();
    private bool _checking;

    /// The app name ("Zoom", "Browser"), once per call and never twice within the quiet time.
    public event EventHandler<string>? CallStarted;

    public CallMonitor()
    {
        _timer = new DispatcherTimer { Interval = CallStreaks.Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        Log.Info("Call detection on");
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _streaks.Reset();
        Log.Info("Call detection off");
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var calls = await Task.Run(() => CallDetection.Calls(AudioApps.Candidates()));
            // Stopped meanwhile (a meeting started): this check does not count.
            if (!_timer.IsEnabled) return;
            foreach (var app in _streaks.Evaluate(calls, DateTimeOffset.Now))
            {
                Log.Info($"Call detected: {app}");
                CallStarted?.Invoke(this, app);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Call check failed", ex);
        }
        finally
        {
            _checking = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
