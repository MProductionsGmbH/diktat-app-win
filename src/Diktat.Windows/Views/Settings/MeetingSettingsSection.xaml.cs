using System.Windows;
using Diktat.Core.Settings;
using Diktat.Transcription.Models;
using Diktat.Windows.Runtime;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Meetings": the default source, call suggestions, summarize automatically, where summaries
/// run, the system audio with its test, speaker detection (MeetingSettingsPane.swift). The
/// meeting page itself holds the choices made per meeting.
public partial class MeetingSettingsSection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private bool _shown;
    private bool _loading;

    public MeetingSettingsSection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        Sources.ItemsSource = Option.List(MeetingSourceInfo.AllCases, s => s.Title());
        Load();
    }

    private MeetingSource Source => MeetingSourceInfo.Resolve(_app.Services.Settings.App.MeetingSource, legacySystemAudio: false, canRecordSystemAudio: true);

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.SettingsChanged += OnChanged;
        _app.Services.AIProviders.Changed += OnChanged;
        _app.Services.Models.Changed += OnModelChanged;
        Load();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.Services.SettingsChanged -= OnChanged;
        _app.Services.AIProviders.Changed -= OnChanged;
        _app.Services.Models.Changed -= OnModelChanged;
        SoundTest.Stop();
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Load);
    private void OnModelChanged(string id) => Ui.Post(Load);

    private void Load()
    {
        _loading = true;
        var source = Source;
        Sources.SelectedValue = source;
        SourceExplanation.Text = source.Explanation();
        _loading = false;
        SuggestOnCall.IsChecked = _app.Services.Settings.App.SuggestsMeetingOnCall;
        Summarize.IsChecked = _app.Services.Settings.App.SummarizesMeetings;
        var server = _app.Services.Rewriter.ServerOnThisPC;
        var builtIn = _app.Services.Rewriter.BuiltInModel;
        var preferServer = _app.Services.Settings.App.RewriteEngine == RewriteEngine.Provider;
        var name = (preferServer ? server?.Model : null) ?? builtIn?.Name ?? server?.Model;
        SummaryNote.Text = name is not null
            ? L.F("Zusammenfassungen schreibt {0} auf diesem PC. Das Transkript verlässt den PC nicht.", name)
            : L.T("Meetings fasst Diktat nur mit einem Modell auf diesem PC zusammen: dem KI-Modell unter Einstellungen › Modelle, Ollama oder LM Studio. So verlässt das Gespräch den PC nicht. Gerade ist keins da: Du bekommst das Transkript ohne Zusammenfassung.");
        bool installed;
        try { installed = _app.Services.Models.IsInstalled(ModelCatalog.SpeakerDiarization); }
        catch { installed = false; }
        SpeakerState.Text = installed ? L.T("Installiert") : L.T("Nicht installiert");
    }

    private void OnSourceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || Sources.SelectedValue is not MeetingSource source || source == _app.Services.Settings.App.MeetingSource) return;
        _app.Services.Settings.App.MeetingSource = source;
        SourceExplanation.Text = source.Explanation();
        _app.Services.SaveSettings();
    }

    private void OnSuggestOnCallClick(object sender, RoutedEventArgs e)
    {
        _app.Services.Settings.App.SuggestsMeetingOnCall = SuggestOnCall.IsChecked == true;
        _app.Services.SaveSettings();
    }

    private void OnSummarizeClick(object sender, RoutedEventArgs e)
    {
        _app.Services.Settings.App.SummarizesMeetings = Summarize.IsChecked == true;
        _app.Services.SaveSettings();
    }

    private void OnProvider(object sender, RoutedEventArgs e) => _app.ShowMainWindow("settings/ai");
    private void OnModels(object sender, RoutedEventArgs e) => _app.ShowMainWindow("settings/models");
    private void OnMeetings(object sender, RoutedEventArgs e) => _app.ShowMainWindow("meetings");
}
