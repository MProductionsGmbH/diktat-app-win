using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Diktat.Core.Settings;
using Diktat.Core.Text;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// A rule row: a program or a website and the mode the Diktat shortcut uses there.
public sealed class RuleItem
{
    public required bool IsWebsite { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required WorkflowType Mode { get; set; }
    public required IReadOnlyList<Option<WorkflowType>> Modes { get; init; }
    public string Glyph => IsWebsite ? "" : ""; // Globe, AllApps
}

/// A running program for the picker.
public sealed record RunningApp(string ExeName, string Name)
{
    public string Label => Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? ExeName : $"{Name} ({ExeName})";
}

/// "Modus je App": the Diktat shortcut uses another mode in a program or on a website.
public partial class AppRulesSection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private bool _loading;
    private bool _saving;
    private bool _appsLoaded;
    private bool _shown;

    public AppRulesSection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        Presets.ItemsSource = ModeRules.WebsitePresets.Select(p => new Option<string>(p.Host, $"{p.Name} ({p.Host})")).ToList();
        Presets.SelectedIndex = 0;
        Load();
    }

    private AppSettings A => _app.Services.Settings.App;

    private IReadOnlyList<Option<WorkflowType>> ModeOptions() =>
        ModeRules.SelectableModes.Select(m => new Option<WorkflowType>(m, Ui.DisplayName(_app.Services.Settings, m))).ToList();

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.SettingsChanged += OnSettingsChanged;
        _app.Services.AIProviders.Changed += OnSettingsChanged;
        Load();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.Services.SettingsChanged -= OnSettingsChanged;
        _app.Services.AIProviders.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_saving) Ui.Post(Load);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var hotkey = Ui.HotkeyLabel(_app.Services.Settings, WorkflowType.TapTalk);
            Intro.Text = L.F("Das Diktat-Kürzel ({0}) kann je App oder Website automatisch einen anderen Modus nutzen, zum Beispiel Diktat KI-Schreibstil in Outlook. Überall sonst bleibt es wortgenau, und die übrigen Kürzel bleiben, wie sie sind.", hotkey);
            var modes = ModeOptions();
            var items = new List<RuleItem>();
            items.AddRange(A.WebsiteModeRules.Select(r => new RuleItem
            {
                IsWebsite = true, Key = r.Host, Title = PresetName(r.Host) ?? r.Host, Detail = L.F("Website: {0}", r.Host), Mode = r.Mode, Modes = modes,
            }));
            items.AddRange(A.AppModeRules.Select(r => new RuleItem
            {
                IsWebsite = false, Key = r.ProcessName,
                Title = string.IsNullOrWhiteSpace(r.AppName) ? r.ProcessName : r.AppName,
                Detail = L.F("Programm: {0}", r.ProcessName), Mode = r.Mode, Modes = modes,
            }));
            Rules.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AiNote.Visibility = items.Any(i => i.Mode.UsesLanguageModel()) && !_app.Services.AIProviders.IsConfigured
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string? PresetName(string host) =>
        ModeRules.WebsitePresets.FirstOrDefault(p => p.Host == host) is { Name: { } name } && name.Length > 0 ? name : null;

    private void Save()
    {
        _saving = true;
        try { _app.Services.SaveSettings(); }
        finally { _saving = false; }
        Load();
    }

    private void OnRuleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { DataContext: RuleItem item, SelectedValue: WorkflowType mode }) return;
        if (item.IsWebsite)
        {
            var index = A.WebsiteModeRules.FindIndex(r => r.Host == item.Key);
            if (index >= 0) A.WebsiteModeRules[index] = A.WebsiteModeRules[index] with { Mode = mode };
        }
        else
        {
            var index = A.AppModeRules.FindIndex(r => r.ProcessName == item.Key);
            if (index >= 0) A.AppModeRules[index] = A.AppModeRules[index] with { Mode = mode };
        }
        Save();
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleItem item) return;
        if (item.IsWebsite) A.WebsiteModeRules.RemoveAll(r => r.Host == item.Key);
        else A.AppModeRules.RemoveAll(r => r.ProcessName == item.Key);
        Save();
    }

    // --- Apps -------------------------------------------------------------------------

    private void OnAppsDropDown(object? sender, EventArgs e)
    {
        if (!_appsLoaded) _ = LoadRunningAppsAsync();
    }

    private void OnReloadApps(object sender, RoutedEventArgs e) => _ = LoadRunningAppsAsync();

    private async Task LoadRunningAppsAsync()
    {
        _appsLoaded = true;
        var apps = await Task.Run(ListRunningApps);
        RunningApps.ItemsSource = apps;
        if (apps.Count > 0 && RunningApps.SelectedIndex < 0) RunningApps.SelectedIndex = 0;
    }

    /// Programs with a window, by exe name, with the friendly name from the file description.
    private static List<RunningApp> ListRunningApps()
    {
        var result = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return []; }
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                var exe = ForegroundAppInfo.ExeNameOf(process.Id) ?? ProcessNames.Normalize(process.ProcessName);
                if (exe.Length == 0 || result.ContainsKey(exe) || exe is "explorer.exe" or "applicationframehost.exe" or "textinputhost.exe") continue;
                string name = process.ProcessName;
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file is not null)
                    {
                        var description = FileVersionInfo.GetVersionInfo(file).FileDescription;
                        if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                    }
                }
                catch
                {
                    // Elevated or protected process: keep the process name.
                }
                result[exe] = new RunningApp(exe, name);
            }
            catch
            {
                // The process ended meanwhile.
            }
            finally
            {
                process.Dispose();
            }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void OnAddRunningApp(object sender, RoutedEventArgs e)
    {
        if (RunningApps.SelectedItem is RunningApp app) AddApp(app.ExeName, app.Name);
        else if (!_appsLoaded) _ = LoadRunningAppsAsync();
    }

    private void OnExeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAddExe(sender, e);
        e.Handled = true;
    }

    private void OnAddExe(object sender, RoutedEventArgs e)
    {
        var exe = ProcessNames.Normalize(ExeName.Text);
        if (exe.Length <= 4 || exe.Contains(' '))
        {
            ShowError(L.T("Bitte den Namen der .exe-Datei eintragen, zum Beispiel outlook.exe."), app: true);
            return;
        }
        AddApp(exe, "");
        ExeName.Text = "";
    }

    private void AddApp(string exe, string name)
    {
        var normalized = ProcessNames.Normalize(exe);
        if (A.AppModeRules.Any(r => ProcessNames.Normalize(r.ProcessName) == normalized))
        {
            ShowError(L.T("Für dieses Programm gibt es schon eine Regel."), app: true);
            return;
        }
        A.AppModeRules.Add(new AppModeRule { ProcessName = normalized, AppName = name, Mode = WorkflowType.TextImprover });
        HideError();
        Save();
    }

    // --- Websites ---------------------------------------------------------------------

    private void OnAddPreset(object sender, RoutedEventArgs e)
    {
        if (Presets.SelectedValue is string host) AddWebsite(host);
    }

    private void OnHostKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAddHost(sender, e);
        e.Handled = true;
    }

    private void OnAddHost(object sender, RoutedEventArgs e)
    {
        if (WebsiteHost.Normalized(Host.Text) is not { } host)
        {
            ShowError(L.T("Das ist keine Adresse einer Website. Zum Beispiel: mail.google.com"));
            return;
        }
        AddWebsite(host);
        Host.Text = "";
    }

    private void AddWebsite(string host)
    {
        if (A.WebsiteModeRules.Any(r => r.Host == host))
        {
            ShowError(L.T("Für diese Website gibt es schon eine Regel."));
            return;
        }
        A.WebsiteModeRules.Add(new WebsiteModeRule { Host = host, Mode = WorkflowType.TextImprover });
        HideError();
        Save();
    }

    private void ShowError(string text, bool app = false)
    {
        HideError();
        var target = app ? AppError : AddError;
        target.Text = text;
        target.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        AddError.Visibility = Visibility.Collapsed;
        AppError.Visibility = Visibility.Collapsed;
    }
}
