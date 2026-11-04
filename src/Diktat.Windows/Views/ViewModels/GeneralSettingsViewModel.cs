using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Diktat.Core.Settings;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

/// The shortcut of one mode in "Allgemein".
public sealed class HotkeyRowViewModel : ObservableObject
{
    private string _label = "";
    private bool _isCapturing;
    private bool _isDefault = true;
    private string _name = "";

    public HotkeyRowViewModel(WorkflowType mode) => Mode = mode;

    public WorkflowType Mode { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Label { get => _label; set => Set(ref _label, value); }
    public bool IsCapturing { get => _isCapturing; set { if (Set(ref _isCapturing, value)) OnPropertyChanged(nameof(IsNotCapturing)); } }
    public bool IsNotCapturing => !_isCapturing;
    public bool IsDefault { get => _isDefault; set { if (Set(ref _isDefault, value)) OnPropertyChanged(nameof(CanReset)); } }
    public bool CanReset => !_isDefault;
    public string ChangeAutomationName => $"Kürzel für {Name} ändern";
    public string ResetAutomationName => $"Kürzel für {Name} auf Standard zurücksetzen";
}

public sealed class GeneralSettingsViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private readonly SettingsSaver _saver;
    private CancellationTokenSource? _captureCts;
    private string? _hotkeyMessage;
    private string? _updateMessage;
    private bool _isCheckingUpdates;
    private string? _importMessage;
    private bool _importFailed;

    public GeneralSettingsViewModel(IDiktatApp app, SettingsSaver saver)
    {
        _app = app;
        _saver = saver;
        Hotkeys = new ObservableCollection<HotkeyRowViewModel>(WorkflowTypeInfo.MainMenuCases.Select(m => new HotkeyRowViewModel(m)));
        RefreshHotkeys();
    }

    private AppSettings A => _app.Services.Settings.App;

    private void SetApp<T>(Func<AppSettings, T> get, Action<AppSettings, T> set, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(get(A), value)) return;
        set(A, value);
        _saver.SaveNow();
        OnPropertyChanged(name);
    }

    // --- Start ------------------------------------------------------------------------

    public bool LaunchAtLogin
    {
        get => A.LaunchAtLogin;
        set => SetApp(a => a.LaunchAtLogin, (a, v) => a.LaunchAtLogin = v, value);
    }

    // --- Shortcuts --------------------------------------------------------------------

    public IReadOnlyList<Option<HotkeyMode>> HotkeyModes { get; } =
        [new(HotkeyMode.Hold, "Halten"), new(HotkeyMode.Toggle, "Umschalten")];

    public HotkeyMode HotkeyMode
    {
        get => A.HotkeyMode;
        set
        {
            if (A.HotkeyMode == value) return;
            A.HotkeyMode = value;
            _saver.SaveNow();
            ReloadHotkeys();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyModeDetail));
        }
    }

    public string HotkeyModeDetail => A.HotkeyMode == HotkeyMode.Hold
        ? "Halten: Kürzel gedrückt halten, sprechen, loslassen. Der Text erscheint am Cursor."
        : "Umschalten: Kürzel einmal drücken, sprechen, nochmal drücken. Escape bricht ab.";

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; }

    public string? HotkeyMessage { get => _hotkeyMessage; private set => Set(ref _hotkeyMessage, value); }

    public void RefreshHotkeys()
    {
        var settings = _app.Services.Settings;
        foreach (var row in Hotkeys)
        {
            row.Name = Ui.DisplayName(settings, row.Mode);
            if (!row.IsCapturing) row.Label = Ui.HotkeyLabel(settings, row.Mode);
            row.IsDefault = A.HotkeyFor(row.Mode) == KeyCombo.DefaultFor(row.Mode);
        }
    }

    public async Task CaptureAsync(HotkeyRowViewModel row)
    {
        _captureCts?.Cancel();
        foreach (var other in Hotkeys) other.IsCapturing = false;
        RefreshHotkeys();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _captureCts = cts;
        row.IsCapturing = true;
        row.Label = "Kürzel drücken …";
        HotkeyMessage = "Drücke das neue Kürzel, zum Beispiel Strg + Alt + D oder F9. Escape bricht ab.";
        try
        {
            var combo = await _app.CaptureShortcutAsync(cts.Token);
            if (combo is null)
            {
                HotkeyMessage = null;
                return;
            }
            if (combo.IsModifierOnly && combo.ModifierCount < 2)
            {
                HotkeyMessage = "Ein Kürzel nur aus Sondertasten braucht mindestens zwei, zum Beispiel Strg + Win.";
                return;
            }
            var clash = Hotkeys.FirstOrDefault(r => r.Mode != row.Mode && A.HotkeyFor(r.Mode) == combo);
            if (clash is not null)
            {
                HotkeyMessage = $"{combo.Label(GlobalHotkeyService.KeyName)} nutzt schon {clash.Name}.";
                return;
            }
            A.SetHotkey(row.Mode, combo == KeyCombo.DefaultFor(row.Mode) ? null : combo);
            _saver.SaveNow();
            ReloadHotkeys();
            HotkeyMessage = null;
        }
        catch (OperationCanceledException)
        {
            HotkeyMessage = null;
        }
        catch (Exception ex)
        {
            Log.Warn("Capturing a shortcut failed", ex);
            HotkeyMessage = "Das Kürzel konnte nicht aufgenommen werden.";
        }
        finally
        {
            if (ReferenceEquals(_captureCts, cts)) _captureCts = null;
            cts.Dispose();
            row.IsCapturing = false;
            RefreshHotkeys();
        }
    }

    public void CancelCapture() => _captureCts?.Cancel();

    public void ResetHotkey(HotkeyRowViewModel row)
    {
        var fallback = KeyCombo.DefaultFor(row.Mode);
        var clash = Hotkeys.FirstOrDefault(r => r.Mode != row.Mode && A.HotkeyFor(r.Mode) == fallback);
        A.SetHotkey(row.Mode, null);
        if (clash is not null) A.SetHotkey(clash.Mode, null);
        _saver.SaveNow();
        ReloadHotkeys();
        HotkeyMessage = null;
        RefreshHotkeys();
    }

    private void ReloadHotkeys()
    {
        try { _app.ReloadHotkeys(); }
        catch (Exception ex) { Log.Warn("Reloading the shortcuts failed", ex); }
    }

    // --- Recording and pasting ----------------------------------------------------------

    public bool ShowsRecordingIndicator
    {
        get => A.ShowsRecordingIndicator;
        set => SetApp(a => a.ShowsRecordingIndicator, (a, v) => a.ShowsRecordingIndicator = v, value);
    }

    public IReadOnlyList<Option<PillPosition>> PillPositions { get; } = Option.List(PillPositionInfo.AllCases, p => p.DisplayName());

    public PillPosition PillPosition
    {
        get => A.PillPosition;
        set => SetApp(a => a.PillPosition, (a, v) => a.PillPosition = v, value);
    }

    public bool PlaysStartSound
    {
        get => A.PlaysStartSound;
        set => SetApp(a => a.PlaysStartSound, (a, v) => a.PlaysStartSound = v, value);
    }

    public bool MuteOutputWhileRecording
    {
        get => A.MuteOutputWhileRecording;
        set => SetApp(a => a.MuteOutputWhileRecording, (a, v) => a.MuteOutputWhileRecording = v, value);
    }

    public bool RestoreClipboard
    {
        get => A.RestoreClipboardAfterPaste;
        set => SetApp(a => a.RestoreClipboardAfterPaste, (a, v) => a.RestoreClipboardAfterPaste = v, value);
    }

    public bool AutomaticParagraphs
    {
        get => A.AutomaticParagraphs;
        set => SetApp(a => a.AutomaticParagraphs, (a, v) => a.AutomaticParagraphs = v, value);
    }

    public bool FitToSurroundingText
    {
        get => A.FitToSurroundingText;
        set => SetApp(a => a.FitToSurroundingText, (a, v) => a.FitToSurroundingText = v, value);
    }

    public bool AiSeesFieldContext
    {
        get => A.AiSeesFieldContext;
        set => SetApp(a => a.AiSeesFieldContext, (a, v) => a.AiSeesFieldContext = v, value);
    }

    // --- History ----------------------------------------------------------------------

    public IReadOnlyList<Option<HistoryRetention>> Retentions { get; } = Option.List(HistoryRetentionInfo.AllCases, r => r.DisplayName());

    public HistoryRetention HistoryRetention
    {
        get => A.HistoryRetention;
        set => SetApp(a => a.HistoryRetention, (a, v) => a.HistoryRetention = v, value);
    }

    public bool ShowsHistoryStatistics
    {
        get => A.ShowsHistoryStatistics;
        set => SetApp(a => a.ShowsHistoryStatistics, (a, v) => a.ShowsHistoryStatistics = v, value);
    }

    // --- Updates ----------------------------------------------------------------------

    public bool ChecksForUpdates
    {
        get => A.ChecksForUpdates;
        set => SetApp(a => a.ChecksForUpdates, (a, v) => a.ChecksForUpdates = v, value);
    }

    public bool IsCheckingUpdates { get => _isCheckingUpdates; private set { if (Set(ref _isCheckingUpdates, value)) OnPropertyChanged(nameof(CanCheckUpdates)); } }
    public bool CanCheckUpdates => !_isCheckingUpdates;
    public string? UpdateMessage { get => _updateMessage; private set => Set(ref _updateMessage, value); }
    public string? UpdateUrl { get; private set; }
    public bool HasUpdateUrl => UpdateUrl is not null;

    public async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateMessage = "Suche nach Updates …";
        UpdateUrl = null;
        OnPropertyChanged(nameof(HasUpdateUrl));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await _app.CheckForUpdatesAsync(cts.Token);
            if (update is { IsNewer: true })
            {
                UpdateMessage = $"Diktat {update.Version} ist da.";
                UpdateUrl = string.IsNullOrEmpty(update.ReleasePageUrl) ? update.DownloadUrl : update.ReleasePageUrl;
            }
            else
            {
                UpdateMessage = $"Diktat ist aktuell (Version {AppServices.Version}).";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateMessage = "Keine Antwort von GitHub. Bitte später nochmal versuchen.";
        }
        catch (Exception ex)
        {
            UpdateMessage = string.IsNullOrWhiteSpace(ex.Message) ? "Die Suche nach Updates hat nicht geklappt." : ex.Message;
        }
        finally
        {
            IsCheckingUpdates = false;
            OnPropertyChanged(nameof(HasUpdateUrl));
        }
    }

    // --- About ------------------------------------------------------------------------

    public string VersionText => $"Version {AppServices.Version}";
    public bool CanSendFeedback => Diktat.AI.FeedbackMail.IsAvailable;

    public string? FeedbackUrl()
    {
        var model = Diktat.Transcription.Models.ModelCatalog.TranscriptionModelOrDefault(A.TranscriptionModel);
        var provider = _app.Services.AIProviders.Config is { } c && _app.Services.AIProviders.IsConfigured
            ? Diktat.AI.AIProviderInfo.DisplayName(c.Provider) : "keiner";
        var build = typeof(AppServices).Assembly.GetName().Version?.Revision.ToString() ?? "0";
        return Diktat.AI.FeedbackMail.Url(AppServices.Version, build,
        [
            $"Sprachmodell: {Ui.ShortModelName(model)}",
            $"Kürzel: {(A.HotkeyMode == HotkeyMode.Hold ? "Halten" : "Umschalten")}",
            $"KI-Anbieter: {provider}",
        ]);
    }

    public string? ImportMessage { get => _importMessage; private set => Set(ref _importMessage, value); }
    public bool ImportFailed { get => _importFailed; private set => Set(ref _importFailed, value); }

    public void Import(string json)
    {
        try
        {
            _app.Services.ImportMacSettings(json);
            ReloadHotkeys();
            var s = _app.Services.Settings;
            ImportFailed = false;
            ImportMessage = $"Übernommen: {s.TextImprovement.CustomTerms.Count} Eigennamen, {s.TextImprovement.Replacements.Count} Ersetzungen, Modi und Stil. Kürzel und Apps des Macs gelten auf Windows nicht.";
        }
        catch (Exception ex)
        {
            Log.Warn("Importing the Mac settings failed", ex);
            ImportFailed = true;
            ImportMessage = $"Die Datei konnte nicht übernommen werden: {ex.Message}";
        }
        Reload();
    }

    public void ShowImportError(string message)
    {
        ImportFailed = true;
        ImportMessage = message;
    }

    public void Reload()
    {
        RefreshHotkeys();
        var import = _importMessage;
        var failed = _importFailed;
        OnAllPropertiesChanged();
        _importMessage = import;
        _importFailed = failed;
    }
}
