using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Mikrofon": the input device, a short level test, the Windows privacy switch.
public partial class MicrophoneSection : UserControl, IMainPage
{
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";
    public const string SoundSettingsUri = "ms-settings:sound";
    /// The "system default" entry of the picker (WPF cannot select a null value).
    private const string DefaultValue = "";

    private readonly IDiktatApp _app;
    private AudioDeviceWatcher? _watcher;
    private AudioRecorder? _testRecorder;
    private DispatcherTimer? _testTimer;
    private DateTime _testEnd;
    private float _testPeak;
    private bool _loading;
    private bool _shown;

    public MicrophoneSection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        LoadDevices();
        try
        {
            _watcher = new AudioDeviceWatcher(SynchronizationContext.Current);
            _watcher.DevicesChanged += OnDevicesChanged;
            _watcher.DefaultInputChanged += OnDefaultChanged;
        }
        catch (Exception ex)
        {
            Log.Warn("Device notifications unavailable", ex);
        }
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        StopTest(showResult: false);
        if (_watcher is not null)
        {
            _watcher.DevicesChanged -= OnDevicesChanged;
            _watcher.DefaultInputChanged -= OnDefaultChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => Ui.Post(LoadDevices);
    private void OnDefaultChanged(object? sender, string? id) => Ui.Post(LoadDevices);

    private void LoadDevices()
    {
        _loading = true;
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices;
            try
            {
                devices = AudioDevices.InputDevices();
                ListError.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Warn("Listing the microphones failed", ex);
                devices = [];
                ListError.Text = "Die Mikrofone konnten nicht abgefragt werden.";
                ListError.Visibility = Visibility.Visible;
            }

            var selected = _app.Services.Settings.App.MicrophoneId;
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            var options = new List<Option<string>>
            {
                new(DefaultValue, defaultDevice is null ? "Systemstandard" : $"Systemstandard ({defaultDevice.Name})"),
            };
            options.AddRange(devices.Select(d => new Option<string>(d.Id, d.Name)));
            var missing = !string.IsNullOrEmpty(selected) && devices.All(d => !string.Equals(d.Id, selected, StringComparison.OrdinalIgnoreCase));
            if (missing) options.Add(new Option<string>(selected!, "Nicht angeschlossen"));

            Devices.ItemsSource = options;
            Devices.SelectedValue = string.IsNullOrEmpty(selected)
                ? DefaultValue
                : options.FirstOrDefault(o => string.Equals(o.Value, selected, StringComparison.OrdinalIgnoreCase))?.Value ?? DefaultValue;
            MissingNotice.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            UpdateVolumeNotice();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateVolumeNotice()
    {
        float? volume = null;
        try { volume = AudioDevices.InputVolume(string.IsNullOrEmpty(_app.Services.Settings.App.MicrophoneId) ? null : _app.Services.Settings.App.MicrophoneId); }
        catch { /* unknown */ }
        if (volume is { } v && v < 0.25f)
        {
            VolumeTitle.Text = $"Mikrofon sehr leise eingestellt ({Math.Round(v * 100)} %)";
            VolumeNotice.Visibility = Visibility.Visible;
        }
        else
        {
            VolumeNotice.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Devices.SelectedValue is not string value) return;
        var id = value.Length == 0 ? null : value;
        if (_app.Services.Settings.App.MicrophoneId == id) return;
        _app.Services.Settings.App.MicrophoneId = id;
        _app.Services.SaveSettings();
        LoadDevices();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadDevices();
    private void OnPrivacySettings(object sender, RoutedEventArgs e) => Ui.Open(PrivacySettingsUri);
    private void OnSoundSettings(object sender, RoutedEventArgs e) => Ui.Open(SoundSettingsUri);

    // --- Level test ---------------------------------------------------------------------

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (_testRecorder is not null)
        {
            StopTest(showResult: true);
            return;
        }
        if (_app.Phase == DictationPhase.Recording || _app.Meeting is { Phase: MeetingPhase.Recording })
        {
            ShowResult("Gerade läuft eine Aufnahme. Bitte danach testen.");
            return;
        }
        try
        {
            _testRecorder = new AudioRecorder(SynchronizationContext.Current);
            _testRecorder.Start(_app.Services.Settings.App.MicrophoneId);
        }
        catch (AudioCaptureException ex)
        {
            DisposeRecorder();
            ShowResult(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("Microphone test failed", ex);
            DisposeRecorder();
            ShowResult("Das Mikrofon konnte nicht geöffnet werden.");
            return;
        }
        _testPeak = 0;
        _testEnd = DateTime.UtcNow.AddSeconds(6);
        TestButton.Content = "Test beenden";
        TestBars.IsActive = true;
        ShowResult("Sprich etwas …");
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _testTimer.Tick += (_, _) =>
        {
            if (_testRecorder is null) return;
            var level = _testRecorder.Level;
            TestBars.Level = level;
            _testPeak = Math.Max(_testPeak, level);
            if (DateTime.UtcNow >= _testEnd) StopTest(showResult: true);
        };
        _testTimer.Start();
    }

    private void StopTest(bool showResult)
    {
        _testTimer?.Stop();
        _testTimer = null;
        bool noSignal = false;
        if (_testRecorder is not null)
        {
            try { noSignal = _testRecorder.NoSignal; }
            catch { }
        }
        var hadRecorder = _testRecorder is not null;
        DisposeRecorder();
        TestBars.IsActive = false;
        TestButton.Content = "Mikrofon testen";
        if (!showResult || !hadRecorder) return;
        if (noSignal || _testPeak < 0.02f)
            ShowResult("Kaum etwas zu hören. Ist das Mikrofon stummgeschaltet, sehr leise eingestellt oder das falsche gewählt?");
        else if (_testPeak < 0.12f)
            ShowResult("Das Mikrofon ist recht leise. Näher sprechen oder in den Sound-Einstellungen lauter stellen.");
        else
            ShowResult("Das Mikrofon funktioniert.");
    }

    private void DisposeRecorder()
    {
        if (_testRecorder is null) return;
        try { _testRecorder.Discard(); } catch { }
        try { _testRecorder.Dispose(); } catch { }
        _testRecorder = null;
    }

    private void ShowResult(string text)
    {
        TestResult.Text = text;
        TestResult.Visibility = Visibility.Visible;
    }
}
