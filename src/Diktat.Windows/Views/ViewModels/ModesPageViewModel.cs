using System.Collections.ObjectModel;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

/// A choice card: a cleanup level or a tone, with its example.
public sealed record ChoiceOption<T>(T Value, string Title, string Detail, string ExampleLabel1, string Example1, string ExampleLabel2, string Example2);

/// "Modi": the modes, Glättung, Stil je Kontext and the settings of the AI modes.
public sealed class ModesPageViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private readonly SettingsSaver _saver;
    private StyleContext _context = StyleContext.Personal;

    public ModesPageViewModel(IDiktatApp app, SettingsSaver saver)
    {
        _app = app;
        _saver = saver;
        Modes = new ObservableCollection<ModeRowViewModel>(WorkflowTypeInfo.MainMenuCases.Select(m => new ModeRowViewModel(app, m)));
        CleanupOptions = SpeechCleanupLevelInfo.AllCases.Select(level =>
        {
            var (before, after) = CleanupExample(level);
            return new ChoiceOption<SpeechCleanupLevel>(level, level.DisplayName(), level.Detail(), "Gesprochen", before, "Geschrieben", after);
        }).ToList();
        ToneOptions = StyleToneInfo.AllCases.Select(tone => new ChoiceOption<StyleTone>(
            tone, tone.DisplayName(), tone.Detail(), "Mit Glättung", tone.ExampleCleaned(), "Mit KI-Schreibstil", tone.ExampleRewritten())).ToList();
    }

    private SettingsContainer S => _app.Services.Settings;

    public ObservableCollection<ModeRowViewModel> Modes { get; }

    // --- Glättung -----------------------------------------------------------------------

    public IReadOnlyList<ChoiceOption<SpeechCleanupLevel>> CleanupOptions { get; }

    public SpeechCleanupLevel CleanupLevel
    {
        get => S.App.SpeechCleanupLevel;
        set
        {
            if (S.App.SpeechCleanupLevel == value) return;
            S.App.SpeechCleanupLevel = value;
            _saver.SaveNow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsEngineNote));
        }
    }

    public bool ShowsEngineNote => CleanupLevel.UsesLanguageModel();

    /// Where the model step of the cleanup runs, so the privacy promise stays visible.
    public string EngineNote
    {
        get
        {
            var store = _app.Services.AIProviders;
            if (store.IsConfigured && store.Config is { } config)
            {
                return config.IsLocalServer
                    ? $"Läuft über dein lokales Modell {config.Model} ({config.BaseUrl}). Der Text verlässt dein Netz nicht, das Modell muss dafür laufen."
                    : "Das Transkript geht dafür an deinen Modell-Anbieter. Ohne Anbieter bleibt es bei „Leicht“.";
            }
            return "Braucht einen Modell-Anbieter (Einstellungen, KI-Anbieter). Bis dahin gilt „Leicht“.";
        }
    }

    private static (string Before, string After) CleanupExample(SpeechCleanupLevel level) => level switch
    {
        SpeechCleanupLevel.Light => ("Ich ähm wollte wollte noch sagen, dass das Meeting morgen morgen beginnt.",
            "Ich wollte noch sagen, dass das Meeting morgen beginnt."),
        SpeechCleanupLevel.Medium => ("Wir treffen uns am Mittwoch, nein, Donnerstag um zehn.",
            "Wir treffen uns am Donnerstag um zehn."),
        SpeechCleanupLevel.Strong => ("wir hat gestern ein Treffen gehabt und die Kunde sind zufrieden",
            "Wir hatten gestern ein Treffen, und die Kunden sind zufrieden."),
        _ => ("ich wollte fragen ob wir den Termin am Donnerstag äh ich meine Freitag verschieben können",
            "Ich möchte fragen, ob wir den Termin auf Freitag verschieben können."),
    };

    // --- Stil je Kontext ------------------------------------------------------------------

    public IReadOnlyList<Option<StyleContext>> Contexts { get; } = Option.List(StyleContextInfo.AllCases, c => c.DisplayName());
    public IReadOnlyList<ChoiceOption<StyleTone>> ToneOptions { get; }

    public StyleContext Context
    {
        get => _context;
        set
        {
            if (!Set(ref _context, value)) return;
            OnPropertyChanged(nameof(ContextNote));
            OnPropertyChanged(nameof(Tone));
        }
    }

    public string ContextNote => _context.AppsNote();
    public string ExampleSpoken => StyleToneInfo.ExampleSpoken;

    public StyleTone Tone
    {
        get => S.App.StyleSettings.ToneFor(_context);
        set
        {
            if (S.App.StyleSettings.ToneFor(_context) == value) return;
            S.App.StyleSettings.SetTone(value, _context);
            _saver.SaveNow();
            OnPropertyChanged();
        }
    }

    // --- KI-Schreibstil -----------------------------------------------------------------

    public string ImproverTitle => WorkflowType.TextImprover.DisplayName();

    public string ImproverName
    {
        get => S.TextImprovement.CustomName;
        set => SetText(v => S.TextImprovement.CustomName = v, value);
    }

    public string ImproverContext
    {
        get => S.TextImprovement.Context;
        set => SetText(v => S.TextImprovement.Context = v, value);
    }

    public string ImproverPrompt
    {
        get => S.TextImprovement.SystemPrompt;
        set
        {
            SetText(v => S.TextImprovement.SystemPrompt = v, value);
            OnPropertyChanged(nameof(ImproverPromptHeader));
        }
    }

    public string ImproverPromptHeader => string.IsNullOrWhiteSpace(S.TextImprovement.SystemPrompt) ? "Eigene Anweisung" : "Eigene Anweisung (aktiv)";

    // --- WTF ----------------------------------------------------------------------------

    public string WtfTitle => Ui.DisplayName(S, WorkflowType.DampfAblassen);
    public string WtfSummary => $"{WorkflowType.DampfAblassen.Subtitle()} Kürzel: {Ui.HotkeyLabel(S, WorkflowType.DampfAblassen)}";

    public string WtfName
    {
        get => S.DampfAblassen.CustomName;
        set
        {
            SetText(v => S.DampfAblassen.CustomName = v, value);
            OnPropertyChanged(nameof(WtfTitle));
            RefreshModes();
        }
    }

    public string WtfPrompt
    {
        get => S.DampfAblassen.SystemPrompt;
        set
        {
            SetText(v => S.DampfAblassen.SystemPrompt = v, value);
            OnPropertyChanged(nameof(WtfPromptIsCustom));
            OnPropertyChanged(nameof(WtfPromptHeader));
        }
    }

    public bool WtfPromptIsCustom => S.DampfAblassen.SystemPrompt != DampfAblassenSettings.DefaultPrompt;
    public string WtfPromptHeader => WtfPromptIsCustom ? "Anweisung (angepasst)" : "Anweisung (Standard)";

    public void RestoreWtfPrompt()
    {
        S.DampfAblassen.SystemPrompt = DampfAblassenSettings.DefaultPrompt;
        _saver.SaveNow();
        OnPropertyChanged(nameof(WtfPrompt));
        OnPropertyChanged(nameof(WtfPromptIsCustom));
        OnPropertyChanged(nameof(WtfPromptHeader));
    }

    // --- :-) ----------------------------------------------------------------------------

    public string EmojiTitle => Ui.DisplayName(S, WorkflowType.EmojiText);
    public string EmojiSummary => $"{WorkflowType.EmojiText.Subtitle()} Kürzel: {Ui.HotkeyLabel(S, WorkflowType.EmojiText)}";

    public string EmojiName
    {
        get => S.EmojiText.CustomName;
        set
        {
            SetText(v => S.EmojiText.CustomName = v, value);
            OnPropertyChanged(nameof(EmojiTitle));
            RefreshModes();
        }
    }

    public IReadOnlyList<Option<EmojiDensity>> Densities { get; } =
        [new(EmojiDensity.Wenig, "Wenig"), new(EmojiDensity.Mittel, "Mittel"), new(EmojiDensity.Viel, "Viel")];

    public EmojiDensity Density
    {
        get => S.EmojiText.EmojiDensity;
        set
        {
            if (S.EmojiText.EmojiDensity == value) return;
            S.EmojiText.EmojiDensity = value;
            _saver.SaveNow();
            OnPropertyChanged();
        }
    }

    // --- Helpers ------------------------------------------------------------------------

    private void SetText(Action<string> apply, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply(value ?? "");
        _saver.SaveSoon();
        OnPropertyChanged(name);
    }

    private void RefreshModes()
    {
        foreach (var mode in Modes) mode.Refresh();
    }

    /// After settings changed elsewhere (import, shortcuts) or the provider changed.
    public void Reload()
    {
        RefreshModes();
        OnAllPropertiesChanged();
    }
}
