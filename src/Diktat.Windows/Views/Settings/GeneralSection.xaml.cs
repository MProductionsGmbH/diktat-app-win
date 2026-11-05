using System.IO;
using System.Windows;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Allgemein": app language, start, shortcuts, recording and pasting, history, updates, about.
public partial class GeneralSection : UserControl, IMainPage
{
    public const string GitHubUrl = "https://github.com/MProductionsGmbH/diktat-app-win";

    private readonly IDiktatApp _app;
    private readonly SettingsSaver _saver;
    private readonly GeneralSettingsViewModel _model;
    private bool _shown;

    public GeneralSection(IDiktatApp app)
    {
        _app = app;
        _saver = new SettingsSaver(app.Services);
        _model = new GeneralSettingsViewModel(app, _saver);
        InitializeComponent();
        DataContext = _model;
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.SettingsChanged += OnSettingsChanged;
        _model.Reload();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _model.CancelCapture();
        _saver.Flush();
        _app.Services.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_saver.IsSaving) return;
        Ui.Post(_model.Reload);
    }

    private static HotkeyRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as HotkeyRowViewModel;

    private async void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await _model.CaptureAsync(row);
    }

    private void OnCancelCapture(object sender, RoutedEventArgs e) => _model.CancelCapture();

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _model.ResetHotkey(row);
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await _model.CheckUpdatesAsync();
    private void OnOpenUpdate(object sender, RoutedEventArgs e) => Ui.Open(_model.UpdateUrl);
    private void OnGitHub(object sender, RoutedEventArgs e) => Ui.Open(GitHubUrl);
    private void OnFeedback(object sender, RoutedEventArgs e) => Ui.Open(_model.FeedbackUrl());
    private void OnLogs(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LogsDirectory);
    private void OnSetup(object sender, RoutedEventArgs e) => _app.ShowSetupAssistant();
    private void OnRestart(object sender, RoutedEventArgs e) => _model.Restart();

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Einstellungen vom Mac übernehmen"),
            Filter = L.T("Diktat-Export (*.json)|*.json|Alle Dateien|*.*"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        string json;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 5_000_000)
            {
                _model.ShowImportError(L.T("Die Datei ist zu groß für einen Einstellungs-Export."));
                return;
            }
            json = await File.ReadAllTextAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            _model.ShowImportError(L.F("Die Datei konnte nicht gelesen werden: {0}", ex.Message));
            return;
        }
        _model.Import(json);
    }
}
