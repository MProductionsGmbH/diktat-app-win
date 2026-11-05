using System.Collections.ObjectModel;
using Diktat.Transcription.Models;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

/// One downloadable model in "Modelle".
public sealed class ModelItemViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private ModelDownload? _download;

    public ModelItemViewModel(IDiktatApp app, ModelInfo model)
    {
        _app = app;
        Model = model;
        Refresh();
    }

    public ModelInfo Model { get; }
    public string Name => Model.DisplayName;
    public string Description => Model.Description;
    public string Languages => Model.Languages;
    public string Size => Model.SizeLabel;
    public string? Attribution => Model.Attribution;
    public bool IsTranscription => Model.IsTranscriptionModel;
    public bool IsLanguageModel => Model.IsLanguageModel;

    /// Why this PC cannot run the model (too little memory), else null.
    public string? Requirement => Model.MemoryProblem(ModelsViewModel.InstalledMemoryGB);

    public bool IsInstalled { get; private set; }
    public bool IsSelected { get; private set; }
    public bool IsDownloading => _download is { IsRunning: true };
    public double Progress => _download?.Progress ?? 0;
    public string ProgressText => _download?.StatusText ?? "";
    public string? Error { get; private set; }

    public bool CanDownload => !IsInstalled && !IsDownloading && Requirement is null;
    public bool CanSelect => (IsTranscription || IsLanguageModel) && IsInstalled && !IsSelected;
    public bool CanDelete => IsInstalled && !IsDownloading;

    public string StateText
    {
        get
        {
            if (IsDownloading) return L.T("Wird geladen");
            if (IsSelected && IsInstalled) return L.T("Aktiv");
            if (IsSelected) return L.T("Ausgewählt, nicht installiert");
            return IsInstalled ? L.T("Installiert") : L.T("Nicht installiert");
        }
    }

    public string AutomationName => $"{Name}, {Size}, {StateText}";

    public void Refresh()
    {
        try { IsInstalled = _app.Services.Models.IsInstalled(Model); }
        catch { IsInstalled = false; }
        IsSelected = IsTranscription
            ? ModelCatalog.TranscriptionModelOrDefault(_app.Services.Settings.App.TranscriptionModel).Id == Model.Id
            : IsLanguageModel && _app.Services.LanguageModel?.Id == Model.Id;
        Attach(ModelDownloads.Find(Model.Id));
        OnAllPropertiesChanged();
    }

    public void Download()
    {
        Error = null;
        Attach(ModelDownloads.Start(_app.Services, Model));
        OnAllPropertiesChanged();
    }

    public void Cancel() => _download?.Cancel();

    private void Attach(ModelDownload? download)
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
            Error = finished.Error;
            Attach(null);
            Refresh();
            return;
        }
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsDownloading));
    }

    public void Detach() => Attach(null);
}

public sealed class ModelsViewModel : ObservableObject
{
    private readonly IDiktatApp _app;

    public ModelsViewModel(IDiktatApp app)
    {
        _app = app;
        TranscriptionModels = new ObservableCollection<ModelItemViewModel>(ModelCatalog.TranscriptionModels.Select(m => new ModelItemViewModel(app, m)));
        SpeakerModel = new ModelItemViewModel(app, ModelCatalog.SpeakerDiarization);
        LanguageModels = new ObservableCollection<ModelItemViewModel>(ModelCatalog.LanguageModels.Select(m => new ModelItemViewModel(app, m)));
    }

    public ObservableCollection<ModelItemViewModel> TranscriptionModels { get; }
    public ModelItemViewModel SpeakerModel { get; }
    public ObservableCollection<ModelItemViewModel> LanguageModels { get; }

    public IEnumerable<ModelItemViewModel> All => TranscriptionModels.Append(SpeakerModel).Concat(LanguageModels);

    /// The memory of this PC in GB (0 when unknown).
    public static int InstalledMemoryGB
    {
        get
        {
            try { return (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_073_741_824.0); }
            catch { return 0; }
        }
    }

    /// Which language model runs, in one line.
    public string LanguageModelSummary => _app.Services.LanguageModel is { } model
        ? L.F("Die KI-Modi laufen mit {0} auf diesem PC.", model.DisplayName.Split(" (", 2)[0])
        : L.T("Noch kein KI-Modell geladen. Die KI-Modi brauchen dann einen Modell-Anbieter oder Ollama.");

    public void Select(ModelItemViewModel item)
    {
        if (item.IsTranscription) _app.Services.Settings.App.TranscriptionModel = item.Model.Id;
        else if (item.IsLanguageModel) _app.Services.Settings.App.LanguageModel = item.Model.Id;
        else return;
        _app.Services.SaveSettings();
        Refresh();
    }

    public void Delete(ModelItemViewModel item)
    {
        try
        {
            // The loaded model holds its files open.
            if (item.IsSelected && item.IsTranscription && _app.Phase is not (DictationPhase.Recording or DictationPhase.Processing)) _app.Services.Transcription.Unload();
            if (item.IsLanguageModel) _app.Services.LocalModel.Unload();
            _app.Services.Models.Delete(item.Model);
        }
        catch (Exception ex)
        {
            Platform.Log.Warn($"Deleting model {item.Model.Id} failed", ex);
        }
        Refresh();
    }

    // --- Language -------------------------------------------------------------------

    public IReadOnlyList<Option<string>> Languages { get; } =
    [
        new("auto", L.T("Automatisch")),
        new("de", "Deutsch"),
        new("en", "English"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("es", "Español"),
        new("nl", "Nederlands"),
        new("pt", "Português"),
        new("pl", "Polski"),
    ];

    public string Language
    {
        get => string.IsNullOrWhiteSpace(_app.Services.Settings.Transcription.Language) ? "auto" : _app.Services.Settings.Transcription.Language;
        set
        {
            var code = string.IsNullOrWhiteSpace(value) ? "auto" : value;
            if (_app.Services.Settings.Transcription.Language == code) return;
            _app.Services.Settings.Transcription.Language = code;
            _app.Services.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LanguageNote));
        }
    }

    public string LanguageNote => Language switch
    {
        "auto" => L.T("Diktat erkennt bei jedem Diktat selbst, welche Sprache du sprichst. Wähle eine Sprache, wenn es bei kurzen Sätzen danebenliegt."),
        "de" => L.T("Mit Whisper hört Diktat nur auf Deutsch. Sprichst du Englisch, wird daraus deutscher Text. Parakeet erkennt die Sprache immer selbst."),
        "en" => L.T("Mit Whisper hört Diktat nur auf Englisch. Sprichst du Deutsch, wird daraus englischer Text. Parakeet erkennt die Sprache immer selbst."),
        _ => L.T("Mit Whisper hört Diktat nur auf diese Sprache. Parakeet erkennt die Sprache immer selbst."),
    };

    public string InstalledSummary
    {
        get
        {
            var count = TranscriptionModels.Count(m => m.IsInstalled);
            return count switch
            {
                0 => L.T("Noch kein Sprachmodell installiert. Ohne Modell kann Diktat nicht transkribieren."),
                1 => L.T("1 Sprachmodell installiert."),
                _ => L.F("{0} Sprachmodelle installiert.", count),
            };
        }
    }

    public void Refresh()
    {
        foreach (var item in All) item.Refresh();
        OnAllPropertiesChanged();
    }

    public void Detach()
    {
        foreach (var item in All) item.Detach();
    }
}
