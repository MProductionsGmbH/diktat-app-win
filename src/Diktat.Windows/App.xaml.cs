using System.Windows;
using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace Diktat.Windows;

/// The WPF application: theme resources, global error handling and the normal start in the tray.
public partial class App : WpfApplication
{
    private DiktatApp? _diktat;
    private SingleInstance? _instance;
    private bool _showingError;

    /// The smoke test runs without a user: no message boxes then.
    internal static bool IsUnattended { get; set; }

    /// Exceptions the UI thread did not handle (the smoke test reads them).
    internal static event EventHandler<Exception>? UnhandledError;

    public App()
    {
        InitializeComponent();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// Normal start: the tray icon, the shortcuts and, unless started with Windows, a window.
    internal void StartDiktat(string[] args, SingleInstance instance)
    {
        _instance = instance;
        SystemAudioMuter.RestoreAfterCrash();
        var services = new AppServices(SynchronizationContext.Current!);
        var diktat = new DiktatApp(services);
        _diktat = diktat;
        instance.ActivationRequested += (_, _) => diktat.ShowMainWindow();
        SessionEnding += (_, _) => diktat.Shutdown();
        diktat.Start();

        Log.Info($"Diktat {AppServices.Version} started{(AutostartService.WasStartedInBackground(args) ? " in the background" : "")}");
        if (AutostartService.WasStartedInBackground(args)) return;
        if (diktat.NeedsSetup) diktat.ShowSetupAssistant();
        else diktat.ShowMainWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _diktat?.Shutdown();
        _instance?.Dispose();
        Log.Flush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread", e.Exception);
        e.Handled = true;
        UnhandledError?.Invoke(this, e.Exception);
        // Never leave the PC muted because of a bug.
        try { _diktat?.Dictation.RecoverAfterError(); } catch (Exception ex) { Log.Warn("Recovering after an error failed", ex); }
        if (IsUnattended || _showingError) return;
        _showingError = true;
        try
        {
            WpfMessageBox.Show(
                "In Diktat ist ein Fehler aufgetreten. Diktat läuft weiter; falls etwas nicht mehr geht, beende Diktat und starte es neu.\n\n" + e.Exception.Message,
                "Diktat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }
}
