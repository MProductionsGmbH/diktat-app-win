using System.Windows.Input;
using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

/// One mode in the tray panel and on the modes page.
public sealed class ModeRowViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private string _name = "";
    private string _subtitle = "";
    private string _hotkey = "";
    private bool _isAvailable;

    /// <param name="beforeStart">E.g. the tray panel hides itself, so the window in front gets the text.</param>
    public ModeRowViewModel(IDiktatApp app, WorkflowType mode, Action? beforeStart = null)
    {
        _app = app;
        Mode = mode;
        StartCommand = new RelayCommand(() =>
        {
            if (!_app.IsModeAvailable(Mode)) return;
            beforeStart?.Invoke();
            _app.StartDictation(Mode);
        });
        Refresh();
    }

    public WorkflowType Mode { get; }
    public ICommand StartCommand { get; }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Hotkey { get => _hotkey; private set => Set(ref _hotkey, value); }
    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }

    public string ToolTip => IsAvailable ? L.F("Startet {0}", Name) : _app.ModeUnavailableReason(Mode) ?? Subtitle;
    public string AutomationName => Hotkey.Length > 0 ? L.F("{0}, {1}, Kürzel {2}", Name, Subtitle, Hotkey) : $"{Name}, {Subtitle}";

    public void Refresh()
    {
        var settings = _app.Services.Settings;
        Name = Ui.DisplayName(settings, Mode);
        IsAvailable = SafeAvailable();
        Subtitle = SafeSubtitle();
        Hotkey = Ui.HotkeyLabel(settings, Mode);
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(AutomationName));
    }

    private bool SafeAvailable()
    {
        try { return _app.IsModeAvailable(Mode); }
        catch { return false; }
    }

    private string SafeSubtitle()
    {
        try { return Ui.Subtitle(_app, Mode); }
        catch { return Mode.Subtitle(); }
    }
}

/// One result in the history lists.
public sealed class HistoryEntryViewModel : ObservableObject
{
    private bool _copied;

    public HistoryEntryViewModel(TranscriptHistoryEntry entry, string modeName)
    {
        Entry = entry;
        ModeName = modeName;
    }

    public TranscriptHistoryEntry Entry { get; }
    public Guid Id => Entry.Id;
    public string Text => Entry.Text;
    public string ModeName { get; }
    public WorkflowType Mode => Entry.Workflow;
    public string Time => Entry.CreatedAt.ToLocalTime().ToString(L.Current == UiLanguage.English ? "h:mm tt" : "HH:mm", L.Culture);
    public string Meta => $"{ModeName}, {Ui.RelativeTime(Entry.CreatedAt)}";
    public string AutomationName => $"{ModeName}, {Time}: {Text}";

    /// Shows "Kopiert" for a moment after a click.
    public bool Copied { get => _copied; set => Set(ref _copied, value); }

    public async void FlashCopied()
    {
        Copied = true;
        await Task.Delay(1600);
        Copied = false;
    }
}
