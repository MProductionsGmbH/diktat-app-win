using System.IO;
using System.Windows;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Border = System.Windows.Controls.Border;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Pages;

/// "Audiodatei": transcribes a voice memo or WhatsApp voice message on this PC.
public partial class AudioFilePage : UserControl, IMainPage
{
    public static readonly string[] Extensions = [".wav", ".mp3", ".m4a", ".ogg", ".opus", ".aac", ".mp4", ".wma", ".flac"];

    private readonly IDiktatApp _app;
    private CancellationTokenSource? _cts;
    private string? _fileName;

    public AudioFilePage(IDiktatApp app)
    {
        _app = app;
        InitializeComponent();
        ParagraphsToggle.IsChecked = app.Services.Settings.App.AutomaticParagraphs;
    }

    public void OnShown() { }
    public void OnHidden() { }

    /// Starts with a file, e.g. one dropped on the tray icon (for the runtime, optional).
    public void Transcribe(string path) => _ = RunAsync(path);

    private bool IsWorking => _cts is not null;

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Audiodatei wählen"),
            Filter = L.T("Audiodateien") + "|*.wav;*.mp3;*.m4a;*.ogg;*.opus;*.aac;*.mp4;*.wma;*.flac|" + L.T("Alle Dateien") + "|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = RunAsync(dialog.FileName);
    }

    // --- Drag and drop ----------------------------------------------------------------

    private static string? DroppedAudio(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        return files?.FirstOrDefault(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !IsWorking && DroppedAudio(e.Data) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.SetResourceReference(Border.BorderBrushProperty, ok ? "Diktat.Accent" : "Diktat.Border");
        DropZone.SetResourceReference(Border.BackgroundProperty, ok ? "Diktat.AccentSoft" : "Diktat.Card");
        DropTitle.Text = ok ? L.T("Loslassen zum Transkribieren") : L.T("Audiodatei hierher ziehen");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (IsWorking) return;
        if (DroppedAudio(e.Data) is { } path) _ = RunAsync(path);
        e.Handled = true;
    }

    private void ResetDropZone()
    {
        DropZone.SetResourceReference(Border.BorderBrushProperty, "Diktat.Border");
        DropZone.SetResourceReference(Border.BackgroundProperty, "Diktat.Card");
        DropTitle.Text = L.T("Audiodatei hierher ziehen");
    }

    // --- Transcription ----------------------------------------------------------------

    private async Task RunAsync(string path)
    {
        if (IsWorking) return;
        _cts = new CancellationTokenSource();
        _fileName = Path.GetFileName(path);
        WorkingFile.Text = _fileName;
        WorkingStatus.Text = L.T("Wird transkribiert …");
        WorkingCard.Visibility = Visibility.Visible;
        ErrorCard.Visibility = Visibility.Collapsed;
        ResultSection.Visibility = Visibility.Collapsed;
        ChooseButton.IsEnabled = false;
        ParagraphsToggle.IsEnabled = false;
        var status = new Progress<string>(message => WorkingStatus.Text = message);
        try
        {
            var text = await _app.TranscribeAudioFileAsync(path, ParagraphsToggle.IsChecked == true, status, _cts.Token);
            ResultFile.Text = _fileName;
            ResultText.Text = string.IsNullOrWhiteSpace(text) ? L.T("Kein Text erkannt.") : text;
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
            ResultSection.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(text))
            {
                _app.CopyToClipboard(text);
                CopyButton.Content = L.T("In der Zwischenablage");
                ResetCopyLabelLater();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: back to the start.
        }
        catch (Exception ex)
        {
            Log.Warn("Audio file transcription failed", ex);
            ErrorText.Text = string.IsNullOrWhiteSpace(ex.Message) ? L.T("Die Datei konnte nicht transkribiert werden.") : ex.Message;
            ErrorCard.Visibility = Visibility.Visible;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            WorkingCard.Visibility = Visibility.Collapsed;
            ChooseButton.IsEnabled = true;
            ParagraphsToggle.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void ResetCopyLabelLater()
    {
        await Task.Delay(2000);
        CopyButton.Content = L.T("Kopieren");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultText.Text)) return;
        _app.CopyToClipboard(ResultText.Text);
        CopyButton.Content = L.T("Kopiert");
        ResetCopyLabelLater();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Transkript speichern"),
            FileName = Path.GetFileNameWithoutExtension(_fileName ?? "Transkript") + ".txt",
            Filter = L.T("Textdatei (*.txt)") + "|*.txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ResultText.Text);
        }
        catch (Exception ex)
        {
            Ui.Inform(this, L.F("Die Datei konnte nicht gespeichert werden: {0}", ex.Message), L.T("Speichern"), warning: true);
        }
    }
}
