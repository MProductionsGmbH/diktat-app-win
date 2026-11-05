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
    public string ChangeAutomationName => L.F("Kürzel für {0} ändern", Name);
    public string ResetAutomationName => L.F("Kürzel für {0} auf Standard zurücksetzen", Name);
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

    // --- Language of the app ---------------------------------------------------------

    /// Language names are always shown in their own language.
    public IReadOnlyList<Option<AppLanguage>> AppLanguages { get; } =
        [new(AppLanguage.System, L.T("Automatisch")), new(AppLanguage.German, "Deutsch"), new(AppLanguage.English, "English")];

    public AppLanguage AppLanguage
    {
        get => A.AppLanguage;
        set
        {
            if (A.AppLanguage == value) return;
            A.AppLanguage = value;
            _saver.SaveNow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AppLanguageNeedsRestart));
        }
    }

    /// The chosen language differs from the one the texts are shown in: it applies after a restart.
    public bool AppLanguageNeedsRestart => L.Resolve(A.AppLanguage) != L.Current;

    public void Restart()
    {
        _saver.Flush();
        _app.Restart();
    }

    // --- Start ------------------------------------------------------------------------

    public bool LaunchAtLogin
    {
        get => A.LaunchAtLogin;
        set => SetApp(a => a.LaunchAtLogin, (a, v) => a.LaunchAtLogin = v, value);
    }

    // --- Shortcuts --------------------------------------------------------------------

    public IReadOnlyList<Option<HotkeyMode>> HotkeyModes { get; } =
        [new(HotkeyMode.Hold, L.T("Halten")), new(HotkeyMode.Toggle, L.T("Umschalten"))];

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
        ? L.T("Halten: Kürzel gedrückt halten, sprechen, loslassen. Der Text erscheint am Cursor.")
        : L.T("Umschalten: Kürzel einmal drücken, sprechen, nochmal drücken. Escape bricht ab.");

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
        row.Label = L.T("Kürzel drücken …");
        HotkeyMessage = L.T("Drücke das neue Kürzel, zum Beispiel Strg + Alt + D oder F9. Escape bricht ab.");
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
                HotkeyMessage = L.T("Ein Kürzel nur aus Sondertasten braucht mindestens zwei, zum Beispiel Strg + Win.");
                return;
            }
            var clash = Hotkeys.FirstOrDefault(r => r.Mode != row.Mode && A.HotkeyFor(r.Mode) == combo);
            if (clash is not null)
            {
                HotkeyMessage = L.F("{0} nutzt schon {1}.", combo.Label(GlobalHotkeyService.KeyName), clash.Name);
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
            HotkeyMessage = L.T("Das Kürzel konnte nicht aufgenommen werden.");
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
        UpdateMessage = L.T("Suche nach Updates …");
        UpdateUrl = null;
        OnPropertyChanged(nameof(HasUpdateUrl));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await _app.CheckForUpdatesAsync(cts.Token);
            if (update is { IsNewer: true })
            {
                UpdateMessage = L.F("Diktat {0} ist da.", update.Version);
                UpdateUrl = string.IsNullOrEmpty(update.ReleasePageUrl) ? update.DownloadUrl : update.ReleasePageUrl;
            }
            else
            {
                UpdateMessage = L.F("Diktat ist aktuell (Version {0}).", AppServices.Version);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateMessage = L.T("Keine Antwort von GitHub. Bitte später nochmal versuchen.");
        }
        catch (Exception ex)
        {
            UpdateMessage = string.IsNullOrWhiteSpace(ex.Message) ? L.T("Die Suche nach Updates hat nicht geklappt.") : ex.Message;
        }
        finally
        {
            IsCheckingUpdates = false;
            OnPropertyChanged(nameof(HasUpdateUrl));
        }
    }

    // --- About ------------------------------------------------------------------------

    public string VersionText => L.F("Version {0}", AppServices.Version);
    public bool CanSendFeedback => Diktat.AI.FeedbackMail.IsAvailable;

    public string? FeedbackUrl()
    {
        var model = Diktat.Transcription.Models.ModelCatalog.TranscriptionModelOrDefault(A.TranscriptionModel);
        var provider = _app.Services.AIProviders.Config is { } c && _app.Services.AIProviders.IsConfigured
            ? Diktat.AI.AIProviderInfo.DisplayName(c.Provider) : L.T("keiner");
        var build = typeof(AppServices).Assembly.GetName().Version?.Revision.ToString() ?? "0";
        return Diktat.AI.FeedbackMail.Url(AppServices.Version, build,
        [
            L.F("Sprachmodell: {0}", Ui.ShortModelName(model)),
            L.F("Kürzel: {0}", A.HotkeyMode == HotkeyMode.Hold ? L.T("Halten") : L.T("Umschalten")),
            L.F("KI-Anbieter: {0}", provider),
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
            ImportMessage = L.F("Übernommen: {0} Eigennamen, {1} Ersetzungen, Modi und Stil. Kürzel und Apps des Macs gelten auf Windows nicht.", s.TextImprovement.CustomTerms.Count, s.TextImprovement.Replacements.Count);
        }
        catch (Exception ex)
        {
            Log.Warn("Importing the Mac settings failed", ex);
            ImportFailed = true;
            ImportMessage = L.F("Die Datei konnte nicht übernommen werden: {0}", ex.Message);
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
