using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.Pages;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Diktat.Windows.Views;

/// A page of the main window; told when it becomes visible, so it subscribes only then.
public interface IMainPage
{
    void OnShown();
    void OnHidden();
}

public sealed class NavItem : ObservableObject
{
    private bool _hasBadge;

    public NavItem(string key, string title, string glyph)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
    }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public bool HasBadge { get => _hasBadge; set => Set(ref _hasBadge, value); }
}

/// The main window: sidebar with Verlauf, Modi, Meetings, Audiodatei, Einstellungen.
public partial class MainWindow : Window
{
    private readonly IDiktatApp _app;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly List<NavItem> _items =
    [
        new("history", L.T("Verlauf"), ""),
        new("modes", L.T("Modi"), ""),
        new("meetings", L.T("Meetings"), ""),
        new("audio", L.T("Audiodatei"), ""),
        new("settings", L.T("Einstellungen"), ""),
    ];
    private IMainPage? _current;
    private string? _pendingSection;

    public MainWindow(IDiktatApp app)
    {
        ThemeManager.EnsureInitialized();
        _app = app;
        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        VersionText.Text = L.F("Version {0}", AppServices.Version);
        Nav.ItemsSource = _items;

        SourceInitialized += (_, _) =>
        {
            // Mica behind the sidebar on Windows 11 22H2+, the plain sidebar color elsewhere.
            if (Dwm.TryEnableMica(this)) Background = System.Windows.Media.Brushes.Transparent;
        };

        _app.PropertyChanged += OnAppChanged;
        _app.Services.SettingsChanged += OnPrivacyInputsChanged;
        _app.Services.AIProviders.Changed += OnPrivacyInputsChanged;
        ModelDownloads.Changed += OnDownloadChanged;
        Activated += (_, _) => UpdatePrivacyBadge();
        Closed += (_, _) =>
        {
            _app.PropertyChanged -= OnAppChanged;
            _app.Services.SettingsChanged -= OnPrivacyInputsChanged;
            _app.Services.AIProviders.Changed -= OnPrivacyInputsChanged;
            ModelDownloads.Changed -= OnDownloadChanged;
            _current?.OnHidden();
            _current = null;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _current?.OnHidden();
            else _current?.OnShown();
        };
        UpdateBadges();
        UpdatePrivacyBadge();
        Nav.SelectedIndex = 0;
    }

    private void OnPrivacyInputsChanged(object? sender, EventArgs e) => Ui.Post(UpdatePrivacyBadge);

    // A downloaded AI model takes over from the provider.
    private void OnDownloadChanged(object? sender, ModelDownload e) => Ui.Post(UpdatePrivacyBadge);

    /// "Alles auf diesem PC" holds only while the AI modes keep the text here. When they send it
    /// to a provider, the badge says so.
    private void UpdatePrivacyBadge()
    {
        string? provider = null;
        try { provider = _app.Services.Rewriter.ProviderInUse(_app.Services.Settings.App.RewriteEngine); }
        catch (Exception ex) { Platform.Log.Warn("Reading the provider failed", ex); }
        var stays = provider is null;
        PrivacyBadge.SetResourceReference(Border.BackgroundProperty, stays ? "Diktat.SuccessSoft" : "Diktat.AccentSoft");
        var color = stays ? "Diktat.SuccessText" : "Diktat.Accent";
        PrivacyIcon.SetResourceReference(TextBlock.ForegroundProperty, color);
        PrivacyTitle.SetResourceReference(TextBlock.ForegroundProperty, color);
        // Lock, open lock.
        PrivacyIcon.Text = stays ? "\uE72E" : "\uE785";
        PrivacyTitle.Text = stays ? L.T("Alles auf diesem PC") : L.T("Sprache auf diesem PC");
        PrivacyText.Text = stays
            ? L.T("Kein Konto, keine Telemetrie. Audio wird nach dem Diktat gelöscht.")
            : L.F("Die KI-Modi senden den erkannten Text an {0}. Kein Konto, keine Telemetrie. Audio wird nach dem Diktat gelöscht.", provider);
    }

    /// "history", "modes", "meetings", "audio", "settings", or "settings/&lt;section&gt;"
    /// (general, models, microphone, dictionary, apps, ai, meetings, privacy). Null keeps the page.
    public void ShowPage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        var parts = page.Trim().ToLowerInvariant().Split('/', 2);
        var key = parts[0] switch
        {
            "verlauf" => "history",
            "modi" or "style" or "stil" or "workflows" => "modes",
            "meeting" => "meetings",
            "audiofile" or "audiodatei" or "file" => "audio",
            "einstellungen" => "settings",
            var other => other,
        };
        if (_items.All(i => i.Key != key)) key = "history";
        if (key == "settings") _pendingSection = parts.Length > 1 ? parts[1] : null;
        var item = _items.First(i => i.Key == key);
        if (Nav.SelectedItem == item) ApplyPendingSection();
        else Nav.SelectedItem = item;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not NavItem item) return;
        _current?.OnHidden();
        if (!_pages.TryGetValue(item.Key, out var page))
        {
            page = CreatePage(item.Key);
            _pages[item.Key] = page;
        }
        PageHost.Content = page;
        _current = page as IMainPage;
        if (IsVisible) _current?.OnShown();
        ApplyPendingSection();
    }

    private void ApplyPendingSection()
    {
        if (_pendingSection is { } section && PageHost.Content is SettingsPage settings)
        {
            settings.ShowSection(section);
            _pendingSection = null;
        }
    }

    private FrameworkElement CreatePage(string key) => key switch
    {
        "modes" => new ModesPage(_app),
        "meetings" => new MeetingsPage(_app),
        "audio" => new AudioFilePage(_app),
        "settings" => new SettingsPage(_app),
        _ => new HistoryPage(_app),
    };

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDiktatApp.Meeting) or "" or null) Ui.Post(UpdateBadges);
    }

    private IMeetingSession? _watchedMeeting;

    private void UpdateBadges()
    {
        if (!ReferenceEquals(_watchedMeeting, _app.Meeting))
        {
            if (_watchedMeeting is not null) _watchedMeeting.PropertyChanged -= OnMeetingChanged;
            _watchedMeeting = _app.Meeting;
            if (_watchedMeeting is not null) _watchedMeeting.PropertyChanged += OnMeetingChanged;
        }
        _items.First(i => i.Key == "meetings").HasBadge = _app.Meeting is { Phase: not MeetingPhase.Idle };
    }

    private void OnMeetingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IMeetingSession.Phase) or "" or null) Ui.Post(UpdateBadges);
    }

    // --- Audio files dropped on the window ---------------------------------------------

    /// Null while the audio page is shown: it handles the drop itself, with its drop zone.
    private string? DroppedAudio(System.Windows.DragEventArgs e) =>
        PageHost.Content is AudioFilePage ? null : AudioFilePage.DroppedAudio(e.Data);

    private bool AudioPageIsWorking => _pages.TryGetValue("audio", out var page) && page is AudioFilePage { IsWorking: true };

    private void OnWindowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (PageHost.Content is AudioFilePage) return;
        var ok = DroppedAudio(e) is not null && !AudioPageIsWorking;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (!ok) return;
        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, System.Windows.DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void OnWindowDrop(object sender, System.Windows.DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (DroppedAudio(e) is not { } path || AudioPageIsWorking) return;
        e.Handled = true;
        ShowPage("audio");
        (PageHost.Content as AudioFilePage)?.Transcribe(path);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Strg + 1 ... 5 switch the pages.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D5)
        {
            Nav.SelectedIndex = e.Key - Key.D1;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_watchedMeeting is not null) _watchedMeeting.PropertyChanged -= OnMeetingChanged;
        foreach (var page in _pages.Values.OfType<IDisposable>()) page.Dispose();
        base.OnClosed(e);
    }
}
