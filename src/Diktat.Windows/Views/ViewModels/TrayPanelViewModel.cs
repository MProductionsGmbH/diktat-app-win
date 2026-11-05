using System.Collections.ObjectModel;
using System.ComponentModel;
using Diktat.Core.Settings;
using Diktat.Transcription.Models;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

/// What the tray panel shows: status, modes, the last text and a few history entries.
/// Subscribes to the app only while attached (the panel is visible).
public sealed class TrayPanelViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private bool _attached;
    private bool _lastCopied;

    public TrayPanelViewModel(IDiktatApp app)
    {
        _app = app;
        Modes = new ObservableCollection<ModeRowViewModel>(
            WorkflowTypeInfo.MainMenuCases.Select(m => new ModeRowViewModel(app, m, () => HideRequested?.Invoke(this, EventArgs.Empty))));
    }

    public IDiktatApp App => _app;

    /// The panel should hide (a mode starts: the window in front gets the text).
    public event EventHandler? HideRequested;
    public ObservableCollection<ModeRowViewModel> Modes { get; }
    public ObservableCollection<HistoryEntryViewModel> RecentHistory { get; } = new();

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _app.PropertyChanged += OnAppChanged;
        _app.Services.SettingsChanged += OnServicesChanged;
        _app.Services.History.Changed += OnServicesChanged;
        _app.Services.AIProviders.Changed += OnServicesChanged;
        _app.Services.Models.Changed += OnModelChanged;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _app.PropertyChanged -= OnAppChanged;
        _app.Services.SettingsChanged -= OnServicesChanged;
        _app.Services.History.Changed -= OnServicesChanged;
        _app.Services.AIProviders.Changed -= OnServicesChanged;
        _app.Services.Models.Changed -= OnModelChanged;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The level changes many times a second; only the bars need it.
        if (e.PropertyName == nameof(IDiktatApp.AudioLevel)) Ui.Post(() => OnPropertyChanged(nameof(AudioLevel)));
        else if (e.PropertyName == nameof(IDiktatApp.NoSignal)) Ui.Post(() =>
        {
            OnPropertyChanged(nameof(NoSignal));
            OnPropertyChanged(nameof(StatusDetail));
        });
        else Ui.Post(Refresh);
    }
    private void OnServicesChanged(object? sender, EventArgs e) => Ui.Post(Refresh);
    private void OnModelChanged(string id) => Ui.Post(Refresh);

    // --- Status ---------------------------------------------------------------------

    public DictationPhase Phase => _app.Phase;
    public bool IsRecording => _app.Phase == DictationPhase.Recording;
    public bool IsProcessing => _app.Phase == DictationPhase.Processing;
    public bool IsBusy => IsRecording || IsProcessing;
    public bool IsError => _app.Phase == DictationPhase.Error;
    public bool IsIdle => !IsBusy;
    public float AudioLevel => _app.AudioLevel;
    public bool NoSignal => IsRecording && _app.NoSignal;

    public bool CanStartDictation => SafeAvailable(WorkflowType.TapTalk);

    public string MicToolTip => IsRecording ? L.T("Aufnahme beenden") : IsProcessing ? L.T("Wird verarbeitet") : L.T("Diktat jetzt starten");

    /// "Strg + Win halten und sprechen" or what is happening right now.
    public string StatusTitle
    {
        get
        {
            switch (_app.Phase)
            {
                case DictationPhase.Recording:
                    var mode = _app.ActiveMode is { } m ? Ui.DisplayName(_app.Services.Settings, m) : "Diktat";
                    return L.F("{0}: Aufnahme läuft", mode);
                case DictationPhase.Processing:
                    return _app.StatusMessage ?? L.T("Wird transkribiert …");
                case DictationPhase.Done:
                    return _app.LastTextWasOnlyCopied ? L.T("In der Zwischenablage") : L.T("Eingefügt");
                case DictationPhase.Error:
                    return L.T("Das hat nicht geklappt");
            }
            var hotkey = Ui.HotkeyLabel(_app.Services.Settings, WorkflowType.TapTalk);
            if (hotkey.Length == 0) return "Diktat";
            return _app.Services.Settings.App.HotkeyMode == HotkeyMode.Hold
                ? L.F("{0} halten und sprechen", hotkey)
                : L.F("{0} drücken und sprechen", hotkey);
        }
    }

    public string StatusDetail
    {
        get
        {
            switch (_app.Phase)
            {
                case DictationPhase.Recording:
                    if (_app.NoSignal) return L.T("Das Mikrofon liefert nur Stille. Ist es stummgeschaltet oder das falsche gewählt?");
                    return _app.Services.Settings.App.HotkeyMode == HotkeyMode.Hold
                        ? L.T("Loslassen beendet die Aufnahme.")
                        : L.T("Nochmal drücken beendet die Aufnahme, Escape bricht ab.");
                case DictationPhase.Processing:
                    return L.T("Läuft auf diesem PC.");
                case DictationPhase.Done:
                    return _app.LastTextWasOnlyCopied
                        ? L.T("Kein Textfeld im Fokus. Mit Strg + V einfügen.")
                        : L.T("Der Text steht dort, wo dein Cursor war.");
                case DictationPhase.Error:
                    return _app.StatusMessage ?? L.T("Bitte nochmal versuchen.");
            }
            var model = ModelCatalog.TranscriptionModelOrDefault(_app.Services.Settings.App.TranscriptionModel);
            bool installed;
            try { installed = _app.Services.Models.IsInstalled(model); }
            catch { installed = false; }
            return installed ? L.T("Der Text landet direkt am Cursor.") : L.F("{0} ist noch nicht installiert.", Ui.ShortModelName(model));
        }
    }

    /// Where speech and the AI modes run right now.
    public string PrivacyLine
    {
        get
        {
            var store = _app.Services.AIProviders;
            if (store.IsConfigured && store.Config is { } config)
            {
                return config.IsOnThisPC
                    ? L.F("Sprache und KI-Modi bleiben auf diesem PC ({0}).", config.Model)
                    : L.F("Sprache bleibt auf diesem PC, KI-Modi über {0}.", Diktat.AI.AIProviderInfo.DisplayName(config.Provider));
            }
            return L.T("Sprache bleibt auf diesem PC. KI-Modi brauchen einen Modell-Anbieter.");
        }
    }

    public bool PrivacyIsLocal => _app.Services.AIProviders.Config is not { } c || !_app.Services.AIProviders.IsConfigured || c.IsOnThisPC;

    // --- Banners --------------------------------------------------------------------

    public bool NeedsSetup => !_app.Services.Settings.App.HasSeenOnboarding;

    public bool HasUpdate => _app.AvailableUpdate is { IsNewer: true };
    public string UpdateTitle => _app.AvailableUpdate is { } u ? L.F("Diktat {0} ist da", u.Version) : "";

    public bool ShowsMeetingBanner => _app.Meeting is { Phase: not MeetingPhase.Idle };
    public string MeetingBannerTitle => _app.Meeting?.Phase == MeetingPhase.Recording ? L.T("Meeting wird aufgenommen") : L.T("Meeting wird ausgewertet");

    public bool ShowsInsertTranscript => _app.LastTranscript is not null && _app.Phase is DictationPhase.Done or DictationPhase.Idle;
    public string InsertTranscriptTitle
    {
        get
        {
            var mode = LastMode is { } m ? Ui.DisplayName(_app.Services.Settings, m) : L.T("KI-Modus");
            return L.F("Wortgenau statt {0}", mode);
        }
    }

    private WorkflowType? LastMode =>
        _app.ActiveMode ?? _app.Services.History.Entries.FirstOrDefault()?.Workflow;

    // --- Last text ------------------------------------------------------------------

    public string? LastText => _app.LastText ?? _app.Services.History.Entries.FirstOrDefault()?.Text;
    public bool HasLastText => !string.IsNullOrWhiteSpace(LastText);

    public string LastTextMeta
    {
        get
        {
            if (_lastCopied) return L.T("Kopiert");
            var entry = _app.Services.History.Entries.FirstOrDefault();
            if (entry is not null && (_app.LastText is null || entry.Text == _app.LastText?.Trim()))
            {
                return $"{Ui.DisplayName(_app.Services.Settings, entry.Workflow)}, {Ui.RelativeTime(entry.CreatedAt)}";
            }
            return L.T("Letzter Text");
        }
    }

    public bool LastCopied => _lastCopied;

    public async void CopyLastText()
    {
        if (LastText is not { } text) return;
        _app.CopyToClipboard(text);
        _lastCopied = true;
        OnPropertyChanged(nameof(LastTextMeta));
        OnPropertyChanged(nameof(LastCopied));
        await Task.Delay(1600);
        _lastCopied = false;
        OnPropertyChanged(nameof(LastTextMeta));
        OnPropertyChanged(nameof(LastCopied));
    }

    public bool HasHistory => RecentHistory.Count > 0;

    public string Version => $"v{AppServices.Version}";

    public void Refresh()
    {
        foreach (var mode in Modes) mode.Refresh();

        var entries = _app.Services.History.Entries;
        // The newest result is the bubble; the list shows the ones before it.
        var skip = entries.Count > 0 && LastText?.Trim() == entries[0].Text ? 1 : 0;
        var recent = entries.Skip(skip).Take(3).ToList();
        if (!recent.Select(e => e.Id).SequenceEqual(RecentHistory.Select(e => e.Id)))
        {
            RecentHistory.Clear();
            foreach (var entry in recent)
            {
                RecentHistory.Add(new HistoryEntryViewModel(entry, Ui.DisplayName(_app.Services.Settings, entry.Workflow)));
            }
        }
        OnAllPropertiesChanged();
    }

    private bool SafeAvailable(WorkflowType mode)
    {
        try { return _app.IsModeAvailable(mode); }
        catch { return false; }
    }
}
