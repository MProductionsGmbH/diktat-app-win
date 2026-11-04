// TEMPORARY: lets the WinExe compile before App.xaml exists. Remove once the app has its entry point.
namespace Diktat.Windows.Platform;

internal static class Program
{
    [STAThread]
    private static void Main() { }
}
