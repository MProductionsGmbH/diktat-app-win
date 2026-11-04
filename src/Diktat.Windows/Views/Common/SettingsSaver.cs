using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views;

/// Saves the settings now (switches, pickers) or a moment after the last keystroke (text
/// fields). <see cref="IsSaving"/> is true while our own save raises SettingsChanged, so a
/// page does not reload the field the user is typing in.
public sealed class SettingsSaver
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    public SettingsSaver(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => SaveNow();
    }

    public bool IsSaving { get; private set; }

    public void SaveNow()
    {
        _timer.Stop();
        IsSaving = true;
        try
        {
            _services.SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Saving the settings failed", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    public void SaveSoon()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// Saves a pending change right away (page hidden, window closed).
    public void Flush()
    {
        if (_timer.IsEnabled) SaveNow();
    }
}
