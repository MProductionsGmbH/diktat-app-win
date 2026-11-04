using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;

namespace Diktat.Windows;

/// Entry point: one Diktat per user session; a second start opens the first one's window.
/// "--background" (autostart) starts in the tray only, "--smoke-test" checks the build and exits.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (SmokeTest.IsRequested(args)) return SmokeTest.Run();

        // The dispatcher's context first, so every service posts its events to the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var instance = SingleInstance.TryAcquire(SynchronizationContext.Current);
        if (instance is null)
        {
            SingleInstance.SignalFirstInstance(args);
            return 0;
        }

        try
        {
            var app = new App();
            app.Startup += (_, _) =>
            {
                try
                {
                    app.StartDiktat(args, instance);
                }
                catch (Exception ex)
                {
                    Log.Error("Diktat could not start", ex);
                    SystemAudioMuter.RestoreAfterCrash();
                    System.Windows.MessageBox.Show("Diktat konnte nicht starten.\n\n" + ex.Message, "Diktat",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            return app.Run();
        }
        finally
        {
            Log.Flush();
        }
    }
}
