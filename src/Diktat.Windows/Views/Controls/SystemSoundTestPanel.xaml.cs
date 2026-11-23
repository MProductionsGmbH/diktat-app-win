using System.ComponentModel;
using System.Windows;
using Diktat.Windows.Runtime;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Controls;

/// "Ton testen": a button, a level and a line that says whether the PC's sound arrives.
/// Nothing is recorded; the test stops when the panel is hidden or after 30 seconds.
public partial class SystemSoundTestPanel : UserControl
{
    private readonly SystemSoundTest _test = new();

    public SystemSoundTestPanel()
    {
        InitializeComponent();
        _test.PropertyChanged += OnTestChanged;
        Unloaded += (_, _) => _test.Stop();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) _test.Stop();
        };
        Update();
    }

    /// Before a meeting starts: the meeting records the sound itself.
    public void Stop() => _test.Stop();

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (_test.IsRunning) _test.Stop();
        else _test.Start();
    }

    private void OnTestChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemSoundTest.Level))
        {
            Bars.Level = _test.Level;
            return;
        }
        Update();
    }

    private void Update()
    {
        TestButton.Content = _test.IsRunning ? L.T("Test beenden") : L.T("Ton testen");
        Bars.Visibility = _test.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        Bars.IsActive = _test.IsRunning;
        if (!_test.IsRunning) Bars.Level = 0;
        string brush;
        if (_test.IsRunning)
        {
            Status.Text = _test.IsHearingSound
                ? L.T("Das System-Audio kommt an.")
                : L.T("Noch kein Ton. Spiel etwas ab, zum Beispiel das Meeting oder ein Video.");
            brush = _test.IsHearingSound ? "Diktat.SuccessText" : "Diktat.TextSecondary";
        }
        else if (_test.Problem is { } problem)
        {
            Status.Text = problem;
            brush = "Diktat.ErrorText";
        }
        else
        {
            Status.Text = L.T("Prüft, ob das System-Audio bei Diktat ankommt. Spiel dazu etwas ab, zum Beispiel das Meeting. Es wird nichts aufgenommen.");
            brush = "Diktat.TextSecondary";
        }
        Status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
    }
}
