using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Diktat.Core.Settings;
using Diktat.Transcription.Models;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.SettingsSections;
using Diktat.Windows.Views.ViewModels;
using Microsoft.Win32;

namespace Diktat.Windows.Views;

public sealed class SetupStep : ObservableObject
{
    private bool _isDone;
    private bool _isCurrent;

    public SetupStep(string title) => Title = title;

    public string Title { get; }
    public bool IsDone { get => _isDone; set { if (Set(ref _isDone, value)) OnPropertyChanged(nameof(AutomationName)); } }
    public bool IsCurrent { get => _isCurrent; set { if (Set(ref _isCurrent, value)) OnPropertyChanged(nameof(AutomationName)); } }
    public string AutomationName => IsCurrent ? L.F("{0}, aktueller Schritt", Title) : IsDone ? L.F("{0}, erledigt", Title) : Title;
}

/// The first-run assistant: Willkommen, Mikrofon, Sprachmodell, Kurzbefehl, KI-Modell, Fertig.
public partial class SetupAssistantWindow : Window
{
    private readonly IDiktatApp _app;
    private readonly List<SetupStep> _steps =
    [
        new(L.T("Willkommen")), new(L.T("Mikrofon")), new(L.T("Sprachmodell")), new(L.T("Kurzbefehl")), new(L.T("KI-Modell")), new(L.T("Fertig")),
    ];
    private readonly FrameworkElement[] _pages;
    private int _index;
    private ModelDownload? _download;
    private ModelDownload? _lmDownload;
    private AIProviderSection? _aiSection;
    private bool _watchingProviders;
    private string? _textBeforeTry;
    private bool _trySucceeded;
    private bool _loadingMic;

    public SetupAssistantWindow(IDiktatApp app)
    {
        ThemeManager.EnsureInitialized();
        _app = app;
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        _pages = [WelcomeStep, MicrophoneStep, ModelStep, TryStep, AIStep, DoneStep];
        StepList.ItemsSource = _steps;
        _app.PropertyChanged += OnAppChanged;
        ModelDownloads.Changed += OnDownloadsChanged;
        Closed += (_, _) =>
        {
            _app.PropertyChanged -= OnAppChanged;
            ModelDownloads.Changed -= OnDownloadsChanged;
            AttachDownload(null);
            AttachLanguageModelDownload(null);
            _aiSection?.OnHidden();
        };
        ShowStep(0);
    }

    private AppSettings A => _app.Services.Settings.App;

    // --- Navigation -------------------------------------------------------------------

    internal int StepCount => _pages.Length;

    /// Also used by the smoke test, which walks every step.
    internal void ShowStep(int index)
    {
        if (_index == 4 && index != 4) _aiSection?.OnHidden();
        _index = Math.Clamp(index, 0, _pages.Length - 1);
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
            _steps[i].IsCurrent = i == _index;
            _steps[i].IsDone = i < _index;
        }
        BackButton.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        LaterButton.Visibility = _index == _pages.Length - 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _index switch
        {
            0 => L.T("Los geht's"),
            4 => L.T("Weiter"),
            5 => L.T("Fertig"),
            _ => L.T("Weiter"),
        };
        Scroller.ScrollToTop();

        switch (_index)
        {
            case 1: CheckMicrophone(); break;
            case 2: UpdateModel(); break;
            case 3: PrepareTry(); break;
            case 4: PrepareAI(); break;
            case 5: PrepareDone(); break;
        }
        UpdateNextLabel();
    }

    private void UpdateNextLabel()
    {
        if (_index == 2 && !ModelIsInstalled() && _download is null) NextButton.Content = L.T("Überspringen");
        else if (_index == 3 && !_trySucceeded) NextButton.Content = L.T("Überspringen");
        else if (_index == 4 && LanguageModelStepState() == LmState.TooLittleMemory) NextButton.Content = L.T("Ohne KI-Modell weiter");
        else NextButton.Content = _index switch { 0 => L.T("Los geht's"), 5 => L.T("Fertig"), _ => L.T("Weiter") };
        // The AI step is not optional: on, once the model loads or another language model is set up.
        NextButton.IsEnabled = _index != 4 || LanguageModelStepState() != LmState.Missing;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index == _pages.Length - 1)
        {
            Finish();
            return;
        }
        ShowStep(_index + 1);
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_index - 1);

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void Finish()
    {
        try
        {
            A.HasSeenOnboarding = true;
            _app.Services.SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Saving the onboarding state failed", ex);
        }
        Close();
    }

    // --- Microphone -------------------------------------------------------------------

    /// Windows privacy: the switches for all apps and for desktop apps, per user and per device.
    public static bool MicrophoneAccessDenied()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(RegistryKey root, string subKey)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        return Denied(Registry.LocalMachine, path) || Denied(Registry.CurrentUser, path) || Denied(Registry.CurrentUser, path + @"\NonPackaged");
    }

    private void CheckMicrophone()
    {
        var denied = MicrophoneAccessDenied();
        IReadOnlyList<AudioDeviceInfo> devices = [];
        if (!denied)
        {
            try { devices = AudioDevices.InputDevices(); }
            catch (Exception ex) { Log.Warn("Listing the microphones failed", ex); }
        }
        MicDenied.Visibility = denied ? Visibility.Visible : Visibility.Collapsed;
        MicNone.Visibility = !denied && devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MicOk.Visibility = !denied && devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (devices.Count == 0) return;

        _loadingMic = true;
        try
        {
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            var options = new List<Option<string>> { new("", defaultDevice is null ? L.T("Systemstandard") : L.F("Systemstandard ({0})", defaultDevice.Name)) };
            options.AddRange(devices.Select(d => new Option<string>(d.Id, d.Name)));
            MicPicker.ItemsSource = options;
            var selected = A.MicrophoneId;
            MicPicker.SelectedValue = options.Any(o => o.Value == selected) ? selected : "";
        }
        finally
        {
            _loadingMic = false;
        }
        UpdateVolumeHint();
    }

    private void UpdateVolumeHint()
    {
        float? volume = null;
        try { volume = AudioDevices.InputVolume(string.IsNullOrEmpty(A.MicrophoneId) ? null : A.MicrophoneId); }
        catch { }
        if (volume is { } v && v < 0.25f)
        {
            MicVolume.Text = L.F("Das Mikrofon ist sehr leise eingestellt ({0} %). In den Sound-Einstellungen unter Eingabe lauter stellen, sonst gehen Wörter verloren.", Math.Round(v * 100));
            MicVolume.Visibility = Visibility.Visible;
        }
        else
        {
            MicVolume.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMicPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMic || MicPicker.SelectedValue is not string value) return;
        var id = value.Length == 0 ? null : value;
        if (A.MicrophoneId == id) return;
        A.MicrophoneId = id;
        _app.Services.SaveSettings();
        UpdateVolumeHint();
    }

    private void OnOpenMicPrivacy(object sender, RoutedEventArgs e) => Ui.Open(MicrophoneSection.PrivacySettingsUri);
    private void OnRecheckMic(object sender, RoutedEventArgs e) => CheckMicrophone();

    // --- Model ------------------------------------------------------------------------

    private ModelInfo SelectedModel => ModelCatalog.TranscriptionModelOrDefault(A.TranscriptionModel);

    private bool ModelIsInstalled()
    {
        try { return _app.Services.Models.IsInstalled(SelectedModel); }
        catch { return false; }
    }

    private void UpdateModel()
    {
        var model = SelectedModel;
        AttachDownload(ModelDownloads.Find(model.Id));
        var installed = ModelIsInstalled();
        var name = Ui.ShortModelName(model);
        ModelName.Text = model.DisplayName;
        ModelSize.Text = model.SizeLabel;
        ModelDetail.Text = installed
            ? model.Description
            : L.F("{0}, {1}, einmalig. Danach erkennt Diktat Sprache ohne Internet. {2}", name, model.SizeLabel, model.Languages);
        ModelInstalled.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        ModelInstalledText.Text = L.F("{0} ist installiert.", name);
        var running = _download is { IsRunning: true };
        ModelProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ModelProgress.Value = _download?.Progress ?? 0;
        ModelProgressText.Text = _download?.StatusText ?? "";
        ModelDownloadButton.Content = L.F("{0} laden", name);
        ModelDownloadButton.Visibility = !installed && !running ? Visibility.Visible : Visibility.Collapsed;
        ModelCancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        UpdateNextLabel();
    }

    private void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        ModelError.Visibility = Visibility.Collapsed;
        AttachDownload(ModelDownloads.Start(_app.Services, SelectedModel));
        UpdateModel();
    }

    private void OnCancelModel(object sender, RoutedEventArgs e) => _download?.Cancel();

    private void AttachDownload(ModelDownload? download)
    {
        if (ReferenceEquals(download, _download)) return;
        if (_download is not null) _download.Changed -= OnDownloadChanged;
        _download = download;
        if (_download is not null) _download.Changed += OnDownloadChanged;
    }

    private void OnDownloadChanged(object? sender, EventArgs e)
    {
        if (sender is ModelDownload { IsRunning: false } finished)
        {
            AttachDownload(null);
            if (finished.Error is { } error)
            {
                ModelError.Text = error;
                ModelError.Visibility = Visibility.Visible;
            }
        }
        if (_index == 2) UpdateModel();
    }

    private void OnDownloadsChanged(object? sender, ModelDownload download)
    {
        if (_index == 2) Ui.Post(UpdateModel);
        if (_index == 4) Ui.Post(UpdateLanguageModel);
    }

    // --- Try the shortcut -------------------------------------------------------------

    private void PrepareTry()
    {
        var hotkey = Ui.HotkeyLabel(_app.Services.Settings, WorkflowType.TapTalk);
        TryTitle.Text = A.HotkeyMode == HotkeyMode.Hold
            ? L.F("{0} halten und sprechen", hotkey)
            : L.F("{0} drücken, sprechen, nochmal drücken", hotkey);
        TryModelMissing.Visibility = ModelIsInstalled() || _download is { IsRunning: true } ? Visibility.Collapsed : Visibility.Visible;
        _textBeforeTry ??= _app.LastText;
        TrySuccess.Visibility = _trySucceeded ? Visibility.Visible : Visibility.Collapsed;
        UpdateTryStatus();
        TryBox.Focus();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDiktatApp.AudioLevel) or nameof(IDiktatApp.NoSignal)) return;
        Ui.Post(() =>
        {
            if (_index != 3) return;
            if (!_trySucceeded && (_app.Phase == DictationPhase.Done || (_app.LastText is { Length: > 0 } text && text != _textBeforeTry)))
            {
                _trySucceeded = true;
                TrySuccess.Visibility = Visibility.Visible;
                UpdateNextLabel();
            }
            UpdateTryStatus();
        });
    }

    private void UpdateTryStatus()
    {
        string? text = _app.Phase switch
        {
            DictationPhase.Recording => _app.NoSignal ? L.T("Aufnahme läuft, aber das Mikrofon liefert nur Stille.") : L.T("Aufnahme läuft …"),
            DictationPhase.Processing => _app.StatusMessage ?? L.T("Wird transkribiert …"),
            DictationPhase.Error => _app.StatusMessage ?? L.T("Das hat nicht geklappt. Bitte nochmal versuchen."),
            DictationPhase.Done when _app.LastTextWasOnlyCopied => L.T("Der Text liegt in der Zwischenablage. Klick vorher in das Feld."),
            _ => null,
        };
        TryStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        TryStatusText.Text = text ?? "";
        TryStatus.SetResourceReference(StyleProperty, _app.Phase == DictationPhase.Error ? "Diktat.ErrorNotice" : "Diktat.Notice");
    }

    // --- AI model -------------------------------------------------------------------

    private enum LmState { Ready, Missing, TooLittleMemory }

    /// The model the step offers: the one picked in the settings, else the recommended one.
    private ModelInfo OfferedLanguageModel =>
        ModelCatalog.Find(A.LanguageModel) is { IsLanguageModel: true } picked ? picked : ModelCatalog.GemmaE2B;

    private bool LanguageModelIsInstalled()
    {
        try { return _app.Services.LanguageModel is not null; }
        catch { return false; }
    }

    /// Ready: a model is installed or loading, or a provider or local server is set up.
    private LmState LanguageModelStepState()
    {
        if (LanguageModelIsInstalled() || _lmDownload is { IsRunning: true } || _app.Services.AIProviders.IsConfigured) return LmState.Ready;
        return OfferedLanguageModel.MemoryProblem(ModelsViewModel.InstalledMemoryGB) is null ? LmState.Missing : LmState.TooLittleMemory;
    }

    private void PrepareAI()
    {
        if (!_watchingProviders)
        {
            _watchingProviders = true;
            _app.Services.AIProviders.Changed += OnProviderChanged;
            Closed += (_, _) => _app.Services.AIProviders.Changed -= OnProviderChanged;
        }
        // Someone who already uses a provider sees it right away.
        if (_app.Services.AIProviders.IsConfigured && !LanguageModelIsInstalled()) ShowProvider();
        else if (AIHost.Visibility == Visibility.Visible) _aiSection?.OnShown();
        UpdateLanguageModel();
    }

    private void UpdateLanguageModel()
    {
        var model = OfferedLanguageModel;
        AttachLanguageModelDownload(ModelDownloads.Find(model.Id));
        var installedModel = _app.Services.LanguageModel;
        var installed = installedModel is not null;
        var shown = installedModel ?? model;
        var name = Ui.ShortModelName(shown);
        var memoryGB = ModelsViewModel.InstalledMemoryGB;
        var memoryProblem = installed ? null : model.MemoryProblem(memoryGB);
        var running = _lmDownload is { IsRunning: true };

        LmName.Text = shown.DisplayName;
        LmSize.Text = shown.SizeLabel;
        LmDetail.Text = installed
            ? shown.Description
            : L.F("{0}, {1}, einmalig. {2}", name, model.SizeLabel, model.Description);
        LmMemory.Text = memoryProblem is null ? "" : memoryProblem + " " + L.T("Die KI-Modi gehen dann nur mit einem eigenen Anbieter oder Ollama.");
        LmMemory.Visibility = memoryProblem is null ? Visibility.Collapsed : Visibility.Visible;
        LmInstalled.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        LmInstalledText.Text = L.F("{0} ist installiert.", name);
        LmProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        LmProgress.Value = _lmDownload?.Progress ?? 0;
        LmProgressText.Text = _lmDownload?.StatusText ?? "";
        LmDownloadButton.Content = L.F("{0} laden", name);
        LmDownloadButton.Visibility = !installed && !running && memoryProblem is null ? Visibility.Visible : Visibility.Collapsed;
        LmCancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        LmRunningHint.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        LmNeeded.Visibility = LanguageModelStepState() == LmState.Missing ? Visibility.Visible : Visibility.Collapsed;
        LmBigger.Visibility = !installed && model.Id == ModelCatalog.GemmaE2B.Id && ModelCatalog.GemmaE4B.MemoryProblem(memoryGB) is null && memoryGB > 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateNextLabel();
    }

    private void OnDownloadLanguageModel(object sender, RoutedEventArgs e)
    {
        LmError.Visibility = Visibility.Collapsed;
        AttachLanguageModelDownload(ModelDownloads.Start(_app.Services, OfferedLanguageModel));
        UpdateLanguageModel();
    }

    private void OnCancelLanguageModel(object sender, RoutedEventArgs e) => _lmDownload?.Cancel();

    private void AttachLanguageModelDownload(ModelDownload? download)
    {
        if (ReferenceEquals(download, _lmDownload)) return;
        if (_lmDownload is not null) _lmDownload.Changed -= OnLanguageModelDownloadChanged;
        _lmDownload = download;
        if (_lmDownload is not null) _lmDownload.Changed += OnLanguageModelDownloadChanged;
    }

    private void OnLanguageModelDownloadChanged(object? sender, EventArgs e)
    {
        if (sender is ModelDownload { IsRunning: false } finished)
        {
            AttachLanguageModelDownload(null);
            if (finished.Error is { } error)
            {
                LmError.Text = error;
                LmError.Visibility = Visibility.Visible;
            }
        }
        if (_index == 4) UpdateLanguageModel();
    }

    private void OnToggleProvider(object sender, RoutedEventArgs e)
    {
        if (AIHost.Visibility == Visibility.Visible)
        {
            AIHost.Visibility = Visibility.Collapsed;
            _aiSection?.OnHidden();
        }
        else
        {
            ShowProvider();
        }
    }

    private void ShowProvider()
    {
        if (_aiSection is null)
        {
            _aiSection = new AIProviderSection(_app);
            AIHost.Content = _aiSection;
        }
        AIHost.Visibility = Visibility.Visible;
        _aiSection.OnShown();
    }

    private void OnProviderChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (_index == 4) UpdateLanguageModel();
    });

    // --- Done -------------------------------------------------------------------------

    private void PrepareDone()
    {
        DoneModes.ItemsSource = WorkflowTypeInfo.MainMenuCases.Select(m => new ModeRowViewModel(_app, m)).ToList();
        var loading = ModelDownloads.Find(SelectedModel.Id) is not null || ModelDownloads.Find(OfferedLanguageModel.Id) is not null;
        DoneLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        DoneLoadingText.Text = L.T("Ein Modell wird noch geladen. Die Modi, die es brauchen, gehen, sobald der Download fertig ist.");
    }
}
