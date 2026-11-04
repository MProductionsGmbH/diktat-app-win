using System.Collections.ObjectModel;
using System.Windows;
using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Pages;

public sealed record HistoryGroupViewModel(string Title, IReadOnlyList<HistoryEntryViewModel> Entries);

public sealed class HistoryPageViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private string _query = "";

    public HistoryPageViewModel(IDiktatApp app) => _app = app;

    public ObservableCollection<HistoryGroupViewModel> Groups { get; } = new();

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? "")) Refresh();
        }
    }

    public bool HasEntries { get; private set; }
    public bool ShowsStatistics { get; private set; }
    public string Words { get; private set; } = "0";
    public string Dictations { get; private set; } = "0";
    public string WordsPerDictation { get; private set; } = "0";
    public string EmptyTitle { get; private set; } = "";
    public string EmptyDetail { get; private set; } = "";
    public string EmptyGlyph { get; private set; } = "";
    public string FooterText { get; private set; } = "";

    public void Refresh()
    {
        var settings = _app.Services.Settings;
        var store = _app.Services.History;
        try { store.Prune(); }
        catch { /* the file may be busy; the list still shows */ }
        var entries = store.Entries;
        var retention = settings.App.HistoryRetention;

        HasEntries = entries.Count > 0;
        var stats = HistoryOverview.Statistics(entries.Select(e => e.Text));
        ShowsStatistics = HasEntries && settings.App.ShowsHistoryStatistics;
        Words = Ui.Number(stats.Words);
        Dictations = Ui.Number(stats.Dictations);
        WordsPerDictation = Ui.Number(stats.WordsPerDictation);

        var matching = entries.Where(e => HistoryOverview.Matches(e.Text, Query)).ToList();
        Groups.Clear();
        foreach (var group in HistoryOverview.Groups(matching, e => e.CreatedAt))
        {
            Groups.Add(new HistoryGroupViewModel(
                Ui.DayTitle(group.Day),
                group.Items.Select(e => new HistoryEntryViewModel(e, Ui.DisplayName(settings, e.Workflow))).ToList()));
        }

        if (retention == HistoryRetention.Off && !HasEntries)
        {
            EmptyGlyph = "";
            EmptyTitle = "Verlauf ist aus";
            EmptyDetail = "In den Einstellungen unter Allgemein, „Verlauf aufbewahren“ einschalten.";
        }
        else if (!HasEntries)
        {
            EmptyGlyph = "";
            EmptyTitle = "Noch nichts im Verlauf";
            EmptyDetail = $"Ergebnisse bleiben {Ui.RetentionHint(retention)} nur auf diesem PC, verschlüsselt.";
        }
        else if (matching.Count == 0)
        {
            EmptyGlyph = "";
            EmptyTitle = "Nichts gefunden";
            EmptyDetail = "Kein Eintrag enthält diesen Suchbegriff.";
        }
        else
        {
            EmptyTitle = "";
            EmptyDetail = "";
        }
        FooterText = HasEntries && retention != HistoryRetention.Off ? $"Nur auf diesem PC, {Ui.RetentionHint(retention)}." : "";
        OnAllPropertiesChanged();
    }
}

/// "Verlauf": the results of the last day or week, grouped by day, with search and counts.
public partial class HistoryPage : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private readonly HistoryPageViewModel _model;
    private bool _shown;

    public HistoryPage(IDiktatApp app)
    {
        _app = app;
        _model = new HistoryPageViewModel(app);
        InitializeComponent();
        DataContext = _model;
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.History.Changed += OnChanged;
        _app.Services.SettingsChanged += OnChanged;
        _model.Refresh();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.Services.History.Changed -= OnChanged;
        _app.Services.SettingsChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Ui.Post(_model.Refresh);

    private static HistoryEntryViewModel? EntryOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as HistoryEntryViewModel;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        _app.CopyToClipboard(entry.Text);
        entry.FlashCopied();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _app.Services.History.Remove(entry.Id);
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(this, "Den ganzen Verlauf auf diesem PC löschen?", "Verlauf löschen")) _app.Services.History.Clear();
    }
}
