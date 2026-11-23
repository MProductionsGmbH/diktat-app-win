using System.Windows;
using Diktat.Windows.Runtime;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.SettingsSections;

/// "Datenschutz": what runs where (from the README), adapted to Windows, and "Alle Daten löschen".
public partial class PrivacySection : UserControl
{
    private readonly IDiktatApp _app;

    public PrivacySection(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
    }

    private void OnRemoveAllData(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm(this, L.T("Alle Daten von Diktat auf diesem PC löschen? Einstellungen, Verlauf, Meetings, API-Keys und Modelle lassen sich danach nicht wiederherstellen. Eine laufende Aufnahme wird verworfen. Diktat beendet sich danach."),
                L.T("Alle Daten löschen"))) return;
        _app.RemoveAllDataAndQuit();
    }
}
