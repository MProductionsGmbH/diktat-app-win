using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Diktat.AI;
using Diktat.Core.Settings;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "KI-Anbieter": the provider with model, address and API key (the key is never shown), a
/// connection test, and one-click setup of Ollama or LM Studio found on this PC.
public partial class AIProviderSection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private readonly DispatcherTimer _saveTimer;
    private CancellationTokenSource? _testCts;
    private CancellationTokenSource? _searchCts;
    private bool _loading;
    private bool _shown;

    public AIProviderSection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => SaveModelAndAddress();

        var providers = new List<Option<AIProvider?>> { new(null, "Keiner") };
        providers.AddRange(AIProviderInfo.All.Select(p => new Option<AIProvider?>(p, p.DisplayName())));
        Providers.ItemsSource = providers;
        Engines.ItemsSource = new List<Option<RewriteEngine>>
        {
            new(RewriteEngine.Automatic, "Modell auf dem PC zuerst"),
            new(RewriteEngine.Provider, "Immer Modell-Anbieter"),
        };
        Engines.SelectedValuePath = "Value";
        Load();
    }

    private AIProviderStore Store => _app.Services.AIProviders;

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        Store.Changed += OnStoreChanged;
        Load();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        if (_saveTimer.IsEnabled) SaveModelAndAddress();
        Store.Changed -= OnStoreChanged;
        _testCts?.Cancel();
        _searchCts?.Cancel();
    }

    private bool _writing;

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (!_writing) Ui.Post(Load);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var config = SafeConfig();
            Providers.SelectedIndex = config is null ? 0 : Array.IndexOf(AIProviderInfo.All, config.Provider) + 1;
            Engines.SelectedValue = _app.Services.Settings.App.RewriteEngine;
            UpdateDetails(config);
        }
        finally
        {
            _loading = false;
        }
    }

    private AIProviderConfig? SafeConfig()
    {
        try { return Store.Config; }
        catch (Exception ex)
        {
            Log.Warn("Reading the provider failed", ex);
            return null;
        }
    }

    private void UpdateDetails(AIProviderConfig? config)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            ProviderDetails.Visibility = config is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateEngineNote(config);
            if (config is null) return;
            var provider = config.Provider;
            if (ModelBox.Text != config.Model) ModelBox.Text = config.Model;
            if (AddressBox.Text != config.BaseUrl) AddressBox.Text = config.BaseUrl;
            var custom = provider == AIProvider.Custom;
            AddressRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            AddressNote.Visibility = AddressRow.Visibility;

            string? masked = null;
            try { masked = Store.MaskedKey(provider); }
            catch (Exception ex) { Log.Warn("Reading the key failed", ex); }
            SavedKeyRow.Visibility = masked is null ? Visibility.Collapsed : Visibility.Visible;
            SavedKeyText.Text = masked is null ? "" : $"API-Key gespeichert: {masked}";
            KeyBox.Password = "";
            SaveKeyButton.IsEnabled = false;
            KeyPlaceholder.Text = masked is not null ? "Neuen API-Key einfügen, um ihn zu ersetzen"
                : provider.RequiresApiKey() ? "API-Key einfügen" : "API-Key (optional)";
            KeyPlaceholder.Visibility = Visibility.Visible;

            var help = provider.KeyHelpUrl();
            KeyHelpLink.Visibility = help is null ? Visibility.Collapsed : Visibility.Visible;
            KeyHelpLink.Content = $"Schlüssel holen bei {provider.DisplayName()}";
            KeyHelpLink.Tag = help;

            PrivacyNote.Text = config.IsOnThisPC
                ? "Die Texte der KI-Modi gehen an das Modell auf diesem PC. Sie verlassen den PC nicht. Das Programm muss dazu laufen."
                : config.IsLocalServer
                    ? "Die Texte der KI-Modi gehen an den Server in deinem Netz. Sie verlassen dein Netz nicht. Der Server muss dazu laufen."
                    : $"Ab jetzt gehen die Texte der KI-Modi an {provider.DisplayName()}. Die Sprache selbst bleibt auf diesem PC. Es gelten die Bedingungen und Kosten des Anbieters. Der Key liegt verschlüsselt für dein Windows-Konto auf diesem PC.";
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void UpdateEngineNote(AIProviderConfig? config)
    {
        var engine = _app.Services.Settings.App.RewriteEngine;
        EngineNote.Text = engine == RewriteEngine.Automatic
            ? "Ist oben ein Modell auf diesem PC eingetragen (Ollama, LM Studio), laufen die KI-Modi dort. Sonst über den Modell-Anbieter."
            : "Die KI-Modi laufen immer über den eingetragenen Modell-Anbieter.";
        if (config is null) EngineNote.Text += " Gerade ist keiner eingetragen: Diktat schreibt dann wortgenau.";
    }

    private void Write(Action action)
    {
        _writing = true;
        try { action(); }
        catch (Exception ex) { Log.Warn("Saving the provider failed", ex); }
        finally { _writing = false; }
    }

    // --- Provider, model, address ------------------------------------------------------

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Providers.SelectedItem is not Option<AIProvider?> option) return;
        _saveTimer.Stop();
        TestResult.Text = "";
        var current = SafeConfig();
        AIProviderConfig? next = option.Value is { } provider
            ? current?.Provider == provider ? current : new AIProviderConfig(provider)
            : null;
        Write(() => Store.Config = next);
        UpdateDetails(next);
    }

    private void OnModelChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs e) => OnModelChanged(sender, e);

    private void SaveModelAndAddress()
    {
        _saveTimer.Stop();
        if (SafeConfig() is not { } config) return;
        var model = ModelBox.Text.Trim();
        var address = AddressBox.Text.Trim();
        var next = config with
        {
            Model = model.Length > 0 ? model : config.Provider.DefaultModel(),
            BaseUrl = config.Provider == AIProvider.Custom && address.Length > 0 ? address : config.BaseUrl,
        };
        if (next == config) return;
        Write(() => Store.Config = next);
        UpdateDetails(next);
    }

    // --- Key ----------------------------------------------------------------------------

    private void OnKeyTyped(object sender, RoutedEventArgs e)
    {
        var typed = KeyBox.Password.Length > 0;
        KeyPlaceholder.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        SaveKeyButton.IsEnabled = typed;
    }

    private void OnKeyBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || KeyBox.Password.Length == 0) return;
        OnSaveKey(sender, e);
        e.Handled = true;
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (SafeConfig() is not { } config) return;
        var key = KeyBox.Password;
        KeyBox.Password = "";
        Write(() => Store.SaveKey(config.Provider, key));
        UpdateDetails(SafeConfig());
        TestResult.Text = "";
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        if (SafeConfig() is not { } config) return;
        if (!Ui.Confirm(this, $"Den API-Key für {config.Provider.DisplayName()} von diesem PC löschen?", "API-Key entfernen")) return;
        Write(() => Store.DeleteKey(config.Provider));
        UpdateDetails(SafeConfig());
    }

    private void OnKeyHelp(object sender, RoutedEventArgs e) => Ui.Open((sender as Button)?.Tag as string);

    // --- Test ---------------------------------------------------------------------------

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_saveTimer.IsEnabled) SaveModelAndAddress();
        if (SafeConfig() is not { } config) return;
        var key = KeyBox.Password.Length > 0 ? KeyBox.Password.Trim() : Store.Key(config.Provider);
        if (config.Provider.RequiresApiKey() && string.IsNullOrEmpty(key))
        {
            ShowTest("Bitte zuerst einen API-Key eintragen.", ok: false);
            return;
        }
        _testCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        _testCts = cts;
        TestButton.IsEnabled = false;
        ShowTest("Teste …", ok: null);
        try
        {
            await _app.Services.AIClient.TestConnectionAsync(config, key, cts.Token);
            ShowTest($"Funktioniert, mit {config.Model}.", ok: true);
        }
        catch (OperationCanceledException)
        {
            if (_shown) ShowTest("Keine Antwort. Ist die Adresse richtig und der Server erreichbar?", ok: false);
        }
        catch (Exception ex)
        {
            ShowTest(string.IsNullOrWhiteSpace(ex.Message) ? "Die Verbindung hat nicht geklappt." : ex.Message, ok: false);
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts)) _testCts = null;
            cts.Dispose();
            TestButton.IsEnabled = true;
        }
    }

    private void ShowTest(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.SetResourceReference(TextBlock.ForegroundProperty,
            ok switch { true => "Diktat.SuccessText", false => "Diktat.ErrorText", _ => "Diktat.TextSecondary" });
        TestResult.FontWeight = ok == true ? FontWeights.SemiBold : FontWeights.Normal;
    }

    // --- Local servers ------------------------------------------------------------------

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _searchCts = cts;
        SearchButton.IsEnabled = false;
        SearchProgress.Visibility = Visibility.Visible;
        SearchResult.Visibility = Visibility.Collapsed;
        Servers.ItemsSource = null;
        try
        {
            var servers = await _app.Services.ServerDiscovery.DiscoverAsync(cts.Token);
            var withModels = servers.Where(s => s.Models.Count > 0).ToList();
            Servers.ItemsSource = withModels;
            var lines = servers.Where(s => s.Models.Count == 0)
                .Select(s => $"{s.Name} läuft, hat aber noch kein Modell. In {s.Name} ein Modell laden und erneut suchen.").ToList();
            if (servers.Count == 0) lines.Add("Kein Ollama und kein LM Studio gefunden. Ist das Programm gestartet? Ollama lädst du auf ollama.com.");
            SearchResult.Text = string.Join("\n", lines);
            SearchResult.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Local server discovery failed", ex);
            SearchResult.Text = "Die Suche hat nicht geklappt.";
            SearchResult.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            cts.Dispose();
            SearchButton.IsEnabled = true;
            SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUseServer(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalServerDiscovery.Server server } button) return;
        var model = button.Tag as string ?? server.Models.FirstOrDefault();
        if (string.IsNullOrEmpty(model)) return;
        var config = new AIProviderConfig(AIProvider.Custom, model, server.BaseUrl);
        Write(() => Store.Config = config);
        Load();
        ShowTest($"{server.Name} mit {model} ist eingetragen.", ok: true);
    }

    // --- Engine -------------------------------------------------------------------------

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Engines.SelectedValue is not RewriteEngine engine) return;
        if (_app.Services.Settings.App.RewriteEngine == engine) return;
        _app.Services.Settings.App.RewriteEngine = engine;
        _app.Services.SaveSettings();
        UpdateEngineNote(SafeConfig());
    }
}
