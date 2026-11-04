using System.Windows;
using System.Windows.Input;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Wörterbuch": custom terms, replacements spoken → written, filler words.
public partial class DictionarySection : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private bool _saving;
    private bool _shown;

    public DictionarySection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        Load();
    }

    private SettingsContainer S => _app.Services.Settings;

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.Services.SettingsChanged += OnSettingsChanged;
        Load();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.Services.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_saving) Ui.Post(Load);
    }

    private void Load()
    {
        Terms.ItemsSource = S.TextImprovement.CustomTerms.ToList();
        Replacements.ItemsSource = S.TextImprovement.Replacements.ToList();
        CorrectTerms.IsChecked = S.App.CorrectCustomTermsEnabled;
        RemoveFillers.IsChecked = S.App.RemoveFillerWordsEnabled;
    }

    private void Save()
    {
        _saving = true;
        try { _app.Services.SaveSettings(); }
        finally { _saving = false; }
        Load();
    }

    // --- Terms ------------------------------------------------------------------------

    private void OnAddTerm(object sender, RoutedEventArgs e) => AddTerm();

    private void OnTermKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddTerm();
        e.Handled = true;
    }

    private void AddTerm()
    {
        var term = NewTerm.Text.Trim();
        if (term.Length == 0) return;
        var terms = S.TextImprovement.CustomTerms;
        if (!terms.Any(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase))) terms.Add(term);
        NewTerm.Text = "";
        Save();
        NewTerm.Focus();
    }

    private void OnRemoveTerm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string term) return;
        S.TextImprovement.CustomTerms.RemoveAll(t => t == term);
        Save();
    }

    private void OnCorrectTermsClick(object sender, RoutedEventArgs e)
    {
        S.App.CorrectCustomTermsEnabled = CorrectTerms.IsChecked == true;
        Save();
    }

    // --- Replacements -----------------------------------------------------------------

    private void OnAddReplacement(object sender, RoutedEventArgs e) => AddReplacement();

    private void OnReplacementKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ReferenceEquals(sender, NewSpoken) && NewWritten.Text.Trim().Length == 0) NewWritten.Focus();
        else AddReplacement();
        e.Handled = true;
    }

    private void AddReplacement()
    {
        var spoken = NewSpoken.Text.Trim();
        var written = NewWritten.Text.Trim();
        if (spoken.Length == 0 || written.Length == 0)
        {
            ShowReplacementError("Bitte beides eintragen: was du sagst und wie es geschrieben wird.");
            return;
        }
        if (string.Equals(spoken, written, StringComparison.Ordinal))
        {
            ShowReplacementError("Gesprochen und geschrieben sind gleich. Dafür reicht ein Eigenname.");
            return;
        }
        var list = S.TextImprovement.Replacements;
        list.RemoveAll(r => string.Equals(r.Spoken, spoken, StringComparison.OrdinalIgnoreCase));
        list.Add(new TermReplacement(spoken, written));
        NewSpoken.Text = "";
        NewWritten.Text = "";
        ReplacementError.Visibility = Visibility.Collapsed;
        Save();
        NewSpoken.Focus();
    }

    private void ShowReplacementError(string text)
    {
        ReplacementError.Text = text;
        ReplacementError.Visibility = Visibility.Visible;
    }

    private void OnRemoveReplacement(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TermReplacement rule) return;
        S.TextImprovement.Replacements.Remove(rule);
        Save();
    }

    private void OnRemoveFillersClick(object sender, RoutedEventArgs e)
    {
        S.App.RemoveFillerWordsEnabled = RemoveFillers.IsChecked == true;
        Save();
    }
}
