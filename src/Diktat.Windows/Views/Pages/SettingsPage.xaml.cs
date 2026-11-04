using System.Windows;
using System.Windows.Controls;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.SettingsSections;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Pages;

public sealed record SettingsTab(string Key, string Title);

/// "Einstellungen" with its sections as tabs.
public partial class SettingsPage : UserControl, IMainPage
{
    public static readonly IReadOnlyList<SettingsTab> AllTabs =
    [
        new("general", "Allgemein"),
        new("models", "Modelle"),
        new("microphone", "Mikrofon"),
        new("dictionary", "Wörterbuch"),
        new("apps", "Modus je App"),
        new("ai", "KI-Anbieter"),
        new("meetings", "Meetings"),
        new("privacy", "Datenschutz"),
    ];

    private readonly IDiktatApp _app;
    private readonly Dictionary<string, FrameworkElement> _sections = new();
    private IMainPage? _current;
    private bool _shown;

    public SettingsPage(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        Tabs.ItemsSource = AllTabs;
        Tabs.SelectedIndex = 0;
    }

    /// Opens a section by key: general, models, microphone, dictionary, apps, ai, meetings, privacy.
    public void ShowSection(string section)
    {
        var key = section.Trim().ToLowerInvariant() switch
        {
            "allgemein" or "shortcuts" or "hotkeys" or "about" or "updates" => "general",
            "modelle" or "model" or "language" or "speech" => "models",
            "mikrofon" or "mic" or "audio" => "microphone",
            "wörterbuch" or "woerterbuch" or "terms" => "dictionary",
            "app-rules" or "rules" or "modus-je-app" => "apps",
            "provider" or "ki" or "ki-anbieter" or "aiprovider" => "ai",
            "meeting" => "meetings",
            "datenschutz" => "privacy",
            var other => other,
        };
        var tab = AllTabs.FirstOrDefault(t => t.Key == key) ?? AllTabs[0];
        Tabs.SelectedItem = tab;
    }

    public void OnShown()
    {
        _shown = true;
        _current?.OnShown();
    }

    public void OnHidden()
    {
        _shown = false;
        _current?.OnHidden();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not SettingsTab tab) return;
        _current?.OnHidden();
        if (!_sections.TryGetValue(tab.Key, out var section))
        {
            section = Create(tab.Key);
            _sections[tab.Key] = section;
        }
        SectionHost.Content = section;
        _current = section as IMainPage;
        if (_shown) _current?.OnShown();
        Scroller.ScrollToTop();
    }

    private FrameworkElement Create(string key) => key switch
    {
        "models" => new ModelsSection(_app),
        "microphone" => new MicrophoneSection(_app),
        "dictionary" => new DictionarySection(_app),
        "apps" => new AppRulesSection(_app),
        "ai" => new AIProviderSection(_app),
        "meetings" => new MeetingSettingsSection(_app),
        "privacy" => new PrivacySection(),
        _ => new GeneralSection(_app),
    };
}
