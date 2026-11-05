using System.Windows;
using Diktat.Transcription.Models;
using Diktat.Windows.Runtime;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Meetings": summarize automatically, where summaries run, speaker detection.
public partial class MeetingSettingsSection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private bool _shown;

    public MeetingSettingsSection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        Load();
    }

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
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(Load);
    private void OnModelChanged(string id) => Ui.Post(Load);

    private void Load()
    {
        Summarize.IsChecked = _app.Services.Settings.App.SummarizesMeetings;
        var local = _app.Services.Rewriter.ServerOnThisPC;
        SummaryNote.Text = local is not null
            ? L.F("Zusammenfassungen schreibt {0} auf diesem PC. Das Transkript verlässt den PC nicht.", local.Model)
            : L.T("Meetings fasst Diktat nur mit einem Modell auf diesem PC zusammen (Ollama oder LM Studio), damit das Gespräch den PC nicht verlässt. Gerade ist keins eingetragen: Du bekommst das Transkript ohne Zusammenfassung.");
        bool installed;
        try { installed = _app.Services.Models.IsInstalled(ModelCatalog.SpeakerDiarization); }
        catch { installed = false; }
        SpeakerState.Text = installed ? L.T("Installiert") : L.T("Nicht installiert");
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
