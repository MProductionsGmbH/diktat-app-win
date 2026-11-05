using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Diktat.Core.Settings;
using Diktat.Windows.Pill;
using Diktat.Windows.Platform;
using Diktat.Windows.Tray;
using Diktat.Windows.Views;

namespace Diktat.Windows.Runtime;

/// "Diktat.exe --smoke-test", for CI on a real Windows machine without microphone or user:
/// builds the services, constructs and shows every window off screen (so XAML errors surface),
/// creates the tray icon and the pill, installs and removes the keyboard hook, and exits with 0,
/// or 1 and the error on the console and in smoke-test.log next to the exe. Never blocks; a
/// watchdog ends it after 45 seconds. The windows are shown twice, in German and in English;
/// in English, a German text on screen that has an English version fails the test.
internal static class SmokeTest
{
    public const string Argument = "--smoke-test";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static int Run()
    {
        RuntimeNative.AttachConsole(-1); // a WinExe has no console of its own
        using var report = new Report(Path.Combine(AppContext.BaseDirectory, "smoke-test.log"));
        App.IsUnattended = true;
        var finished = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (finished.Wait(Timeout)) return;
            report.Line("FAILED: timeout after " + Timeout.TotalSeconds + " s");
            report.Dispose();
            Environment.Exit(1);
        }) { IsBackground = true, Name = "Diktat.SmokeWatchdog" };
        watchdog.Start();

        int code;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new App();
            Exception? uiError = null;
            App.UnhandledError += (_, ex) => uiError ??= ex;
            app.Startup += async (_, _) =>
            {
                int result = 1;
                try
                {
                    await RunStepsAsync(report);
                    if (uiError is not null) report.Fail("UI thread", uiError);
                    else result = 0;
                }
                catch (Exception ex)
                {
                    report.Fail("smoke test", ex);
                }
                app.Shutdown(result);
            };
            code = app.Run();
        }
        catch (Exception ex)
        {
            report.Fail("startup", ex);
            code = 1;
        }
        finished.Set();
        report.Line(code == 0 ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
        return code;
    }

    private static async Task RunStepsAsync(Report report)
    {
        report.Line($"Diktat {AppServices.Version} smoke test, {Environment.OSVersion}");
        var services = await Step(report, "services", () => Task.FromResult(new AppServices(SynchronizationContext.Current!)));
        var diktat = await Step(report, "runtime", () => Task.FromResult(new DiktatApp(services)));
        try
        {
            await Step(report, "keyboard hook", () =>
            {
                if (!diktat.StartHotkeys()) throw new InvalidOperationException("The keyboard hook could not be installed (see the log).");
                diktat.StopHotkeys();
                return Task.FromResult(true);
            });

            foreach (var language in new[] { UiLanguage.German, UiLanguage.English })
            {
                L.Use(language);
                if (language == UiLanguage.English && L.T("Verlauf") != "History")
                    throw new InvalidOperationException("The English texts were not loaded.");
                await Step(report, $"MainWindow ({language})", async () =>
                {
                    var window = OffScreen(new MainWindow(diktat));
                    window.Show();
                    foreach (var page in new[] { "history", "modes", "meetings", "audio", "settings", "settings/general", "settings/models", "settings/microphone", "settings/dictionary", "settings/apps", "settings/ai", "settings/meetings", "settings/privacy" })
                    {
                        window.ShowPage(page);
                        await Settle();
                        CheckLanguage(window, page);
                    }
                    window.Close();
                    return true;
                });

                await Step(report, $"SetupAssistantWindow ({language})", async () =>
                {
                    var window = OffScreen(new SetupAssistantWindow(diktat));
                    window.Show();
                    await Settle();
                    CheckLanguage(window, "setup");
                    window.Close();
                    return true;
                });

                await Step(report, $"TrayPanelWindow ({language})", async () =>
                {
                    var window = OffScreen(new TrayPanelWindow(diktat));
                    window.ShowNear(new System.Windows.Point(-20_000, -20_000));
                    await Settle();
                    CheckLanguage(window, "tray panel");
                    window.Close();
                    return true;
                });
            }
            L.Use(UiLanguage.German);

            await Step(report, "RecordingPillWindow", async () =>
            {
                var pill = new RecordingPillWindow();
                pill.SetState(PillState.Recording, clickable: true);
                pill.ShowPill(place: true, PillPosition.Right);
                for (int i = 0; i < 12; i++) pill.Visual.PushLevel(i / 12f);
                await Settle();
                foreach (var state in new[] { PillState.Processing, PillState.Done, PillState.Copied, PillState.Error, PillState.Meeting })
                {
                    pill.SetState(state, clickable: state == PillState.Meeting);
                    await Settle();
                }
                pill.HidePill();
                pill.Close();
                return true;
            });

            await Step(report, "tray icon", async () =>
            {
                using var tray = new TrayIcon(diktat);
                tray.Refresh();
                await Settle();
                return true;
            });
        }
        finally
        {
            await Step(report, "shutdown", () =>
            {
                diktat.Dispose();
                return Task.FromResult(true);
            });
        }
    }

    /// In English, every text on screen that is a German text with an English version is a miss.
    private static void CheckLanguage(DependencyObject root, string where)
    {
        if (L.Current != UiLanguage.English) return;
        var missed = new SortedSet<string>(StringComparer.Ordinal);
        Visit(root, missed);
        if (missed.Count > 0)
            throw new InvalidOperationException($"German texts in English ({where}): " + string.Join(" | ", missed));
    }

    private static void Visit(DependencyObject node, SortedSet<string> missed)
    {
        string? text = node switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            HeaderedContentControl { Header: string header } => header,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(text) && L.English.TryGetValue(text, out var english) && english != text) missed.Add(text);
        if (node is FrameworkElement { ToolTip: string tip } && L.English.TryGetValue(tip, out var tipEnglish) && tipEnglish != tip) missed.Add(tip);
        var count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) Visit(VisualTreeHelper.GetChild(node, i), missed);
    }

    private static async Task<T> Step<T>(Report report, string name, Func<Task<T>> action)
    {
        report.Line($"- {name} ...");
        var result = await action();
        report.Line($"  ok: {name}");
        return result;
    }

    /// Lets layout, data binding and rendering run.
    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static T OffScreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20_000;
        window.Top = -20_000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    /// Writes to the console (when there is one) and to the log file.
    private sealed class Report : IDisposable
    {
        private readonly object _gate = new();
        private StreamWriter? _file;

        public Report(string path)
        {
            try
            {
                _file = new StreamWriter(path, append: false) { AutoFlush = true };
            }
            catch (Exception)
            {
                // A read-only folder: the console still gets the report.
            }
        }

        public void Line(string text)
        {
            lock (_gate)
            {
                try { Console.WriteLine(text); } catch (Exception) { }
                try { _file?.WriteLine(text); } catch (Exception) { }
            }
        }

        public void Fail(string step, Exception ex)
        {
            Line($"FAILED in {step}: {ex}");
            try { Console.Error.WriteLine($"FAILED in {step}: {ex.GetType().Name}: {ex.Message}"); } catch (Exception) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
            }
        }
    }
}
