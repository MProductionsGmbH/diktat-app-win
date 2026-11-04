using System.ComponentModel;
using System.Windows;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Pages;

/// "Modi": the modes, how much Diktat tidies up, the tone per context, the AI modes.
public partial class ModesPage : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private readonly SettingsSaver _saver;
    private readonly ModesPageViewModel _model;
    private bool _shown;

    public ModesPage(IDiktatApp app)
    {
        _app = app;
        _saver = new SettingsSaver(app.Services);
        _model = new ModesPageViewModel(app, _saver);
        InitializeComponent();
        DataContext = _model;
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.SettingsChanged += OnSettingsChanged;
        _app.Services.AIProviders.Changed += OnSettingsChanged;
        _app.Services.Models.Changed += OnModelChanged;
        _app.PropertyChanged += OnAppChanged;
        _model.Reload();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _saver.Flush();
        _app.Services.SettingsChanged -= OnSettingsChanged;
        _app.Services.AIProviders.Changed -= OnSettingsChanged;
        _app.Services.Models.Changed -= OnModelChanged;
        _app.PropertyChanged -= OnAppChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_saver.IsSaving) return;
        Ui.Post(_model.Reload);
    }

    private void OnModelChanged(string id) => Ui.Post(_model.Reload);

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDiktatApp.AudioLevel) or nameof(IDiktatApp.NoSignal)) return;
        Ui.Post(() =>
        {
            foreach (var mode in _model.Modes) mode.Refresh();
        });
    }

    private void OnRestoreWtf(object sender, RoutedEventArgs e) => _model.RestoreWtfPrompt();
    private void OnShortcuts(object sender, RoutedEventArgs e) => _app.ShowMainWindow("settings/general");
    private void OnAppRules(object sender, RoutedEventArgs e) => _app.ShowMainWindow("settings/apps");
}
