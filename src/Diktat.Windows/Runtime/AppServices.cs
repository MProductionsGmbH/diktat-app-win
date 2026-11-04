using System.IO;
using Diktat.AI;
using Diktat.AI.Meetings;
using Diktat.AI.Updates;
using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Core.Support;
using Diktat.Transcription;
using Diktat.Transcription.Models;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// The composition root: every long-lived service of the app, created once at startup.
/// The runtime (dictation, meetings) and the windows share this one instance.
public sealed class AppServices : IDisposable
{
    public AppServices(SynchronizationContext uiContext)
    {
        AppPaths.EnsureCreated();
        UiContext = uiContext;

        Secrets = new DpapiSecretStore();
        Protector = new DpapiDataProtector();
        SpellChecker = new WindowsSpellChecker();

        SettingsStore = new SettingsStore(AppPaths.SettingsFile);
        Settings = SettingsStore.Load();

        AIProviders = new AIProviderStore(Path.Combine(AppPaths.RoamingDirectory, "ai-provider.json"), Secrets);
        AIClient = new AIClient();
        Rewriter = new TextRewriter(AIProviders, AIClient);
        ModeRewriter = new ModeRewriter(Rewriter);
        SpeechPolisher = new SpeechPolisher(Rewriter);
        ServerDiscovery = new LocalServerDiscovery();
        MeetingSummaries = new MeetingSummaryService(Rewriter);

        Models = new ModelStore(AppPaths.ModelsDirectory);
        Transcription = new LocalTranscriptionService(Models, Settings.App.TranscriptionModel);

        History = new TranscriptHistoryStore(AppPaths.HistoryDirectory, Protector, Settings.App.HistoryRetention);
        Meetings = new MeetingStore(AppPaths.MeetingsDirectory, Protector);

        Updates = new UpdateChecker();
    }

    public SynchronizationContext UiContext { get; }

    // Storage and OS
    public ISecretStore Secrets { get; }
    public IDataProtector Protector { get; }
    public WindowsSpellChecker SpellChecker { get; }

    // Settings
    public SettingsStore SettingsStore { get; }
    /// The current settings. Windows edit this object and then call <see cref="SaveSettings"/>.
    public SettingsContainer Settings { get; private set; }

    /// Raised on the UI thread after the settings were saved or replaced (import).
    public event EventHandler? SettingsChanged;

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        Transcription.SelectModel(Settings.App.TranscriptionModel);
        History.UpdateRetention(Settings.App.HistoryRetention);
        AutostartService.Apply(Settings.App.LaunchAtLogin);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// Imports a settings export of the macOS app (custom terms, replacements, modes).
    public void ImportMacSettings(string json)
    {
        Settings = SettingsStore.ImportMacExport(json);
        SaveSettings();
    }

    // AI
    public AIProviderStore AIProviders { get; }
    public AIClient AIClient { get; }
    public TextRewriter Rewriter { get; }
    public ModeRewriter ModeRewriter { get; }
    public SpeechPolisher SpeechPolisher { get; }
    public LocalServerDiscovery ServerDiscovery { get; }
    public MeetingSummaryService MeetingSummaries { get; }

    // Speech recognition
    public ModelStore Models { get; }
    public LocalTranscriptionService Transcription { get; }

    // Data
    public TranscriptHistoryStore History { get; }
    public MeetingStore Meetings { get; }

    public UpdateChecker Updates { get; }

    public static string Version =>
        typeof(AppServices).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public void Dispose()
    {
        Transcription.Unload();
        AIClient.Dispose();
        SpellChecker.Dispose();
    }
}
