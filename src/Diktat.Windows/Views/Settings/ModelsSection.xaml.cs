using System.Windows;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Modelle": the speech models (download, select, delete), the language, speaker detection.
public partial class ModelsSection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private readonly ModelsViewModel _model;
    private bool _shown;

    public ModelsSection(IDiktatApp app)
    {
        _app = app;
        _model = new ModelsViewModel(app);
        InitializeComponent();
        DataContext = _model;
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.Models.Changed += OnModelsChanged;
        _app.Services.SettingsChanged += OnSettingsChanged;
        ModelDownloads.Changed += OnDownloadsChanged;
        _model.Refresh();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.Services.Models.Changed -= OnModelsChanged;
        _app.Services.SettingsChanged -= OnSettingsChanged;
        ModelDownloads.Changed -= OnDownloadsChanged;
        _model.Detach();
    }

    private void OnModelsChanged(string id) => Ui.Post(_model.Refresh);
    private void OnSettingsChanged(object? sender, EventArgs e) => Ui.Post(_model.Refresh);
    private void OnDownloadsChanged(object? sender, ModelDownload e) => Ui.Post(_model.Refresh);

    private static ModelItemViewModel? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ModelItemViewModel;

    private void OnDownload(object sender, RoutedEventArgs e) => ItemOf(sender)?.Download();
    private void OnCancel(object sender, RoutedEventArgs e) => ItemOf(sender)?.Cancel();

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _model.Select(item);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var question = item.IsSelected
            ? $"{item.Name} ist das aktive Sprachmodell. Löschen? Bis du ein anderes lädst, kann Diktat nicht transkribieren."
            : $"{item.Name} von diesem PC löschen? Du kannst es jederzeit wieder laden.";
        if (Ui.Confirm(this, question, "Modell löschen")) _model.Delete(item);
    }
}
