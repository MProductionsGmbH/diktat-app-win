using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Diktat.Core.Settings;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// Global shortcuts through a low-level keyboard hook (WH_KEYBOARD_LL).
///
/// Threading: the hook runs on its own thread with its own message loop, so a busy UI thread
/// never delays keyboard input (Windows drops a hook that is slower than LowLevelHooksTimeout).
/// All shortcut state lives on that thread. Public members may be called from any thread.
/// Events are raised through the <see cref="SynchronizationContext"/> given to the constructor
/// (pass the WPF dispatcher's context to get them on the UI thread); without one, on a private
/// background thread, in order.
///
/// Semantics (see <see cref="HotkeyStateMachine"/>): <see cref="Pressed"/> = start recording,
/// <see cref="Released"/> = stop and process, <see cref="Cancelled"/> = discard (Escape, or a
/// Windows shortcut such as Ctrl+Win+D typed over a held combo). Injected input (our own Ctrl+V,
/// other tools' SendInput) is ignored.
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WM_INVOKE = Win32.WM_APP + 0x31;

    private readonly EventDispatcher _events;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly HotkeyStateMachine _machine;
    private readonly Host _host;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Win32.HookProc? _hookProc; // kept alive while installed
    private UIntPtr _timerId;
    private readonly object _gate = new();
    private TaskCompletionSource<KeyCombo?>? _capture;
    private IProgress<KeyCombo>? _captureProgress;
    private volatile bool _disposed;

    // Mirrors of settings, so getters need no round trip to the hook thread.
    private volatile HotkeyMode _mode = HotkeyMode.Hold;
    private volatile bool _busy;
    private volatile bool _suspended;

    /// Start recording for this mode. Hold: the combo went down. Toggle: first press.
    public event EventHandler<WorkflowType>? Pressed;
    /// Stop and process. Hold: a key of the combo went up. Toggle: the second press.
    public event EventHandler<WorkflowType>? Released;
    /// Discard the running recording (Escape, or another key typed over a held modifier combo).
    public event EventHandler? Cancelled;
    /// A combo is held but may still become a more specific one (decision within ~150 ms).
    /// Use it to pre-warm the microphone (<see cref="AudioRecorder.Prewarm"/>).
    public event EventHandler<WorkflowType>? PotentialStart;

    public GlobalHotkeyService(SynchronizationContext? eventContext = null)
    {
        _events = new EventDispatcher(eventContext);
        _host = new Host(this);
        _machine = new HotkeyStateMachine(_host);
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public HotkeyMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Post(() =>
            {
                _machine.Mode = value;
                _machine.ResetToggle();
            });
        }
    }

    /// Set by the orchestrator while a dictation records or is processed: then Escape is taken
    /// from the foreground app and raises <see cref="Cancelled"/>.
    public bool IsWorkflowBusy
    {
        get => _busy;
        set
        {
            _busy = value;
            Post(() => _machine.WorkflowBusy = value);
        }
    }

    /// Turns all shortcuts off (keys pass through), e.g. while a settings field has focus.
    public bool IsSuspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            Post(() => _machine.SetSuspended(value));
        }
    }

    /// Replaces the shortcuts. Null entries are off. Combos with a single modifier and no main
    /// key are ignored (they would fire with every normal shortcut).
    public void SetCombos(IReadOnlyDictionary<WorkflowType, KeyCombo?> combos)
    {
        var copy = combos.ToArray();
        Post(() => _machine.SetBindings(copy));
    }

    /// Convenience: the shortcuts of all modes as the settings define them (with defaults).
    public void SetCombos(AppSettings settings)
    {
        var map = Enum.GetValues<WorkflowType>().ToDictionary(type => type, settings.HotkeyFor);
        SetCombos(map);
        Mode = settings.HotkeyMode;
    }

    /// The orchestrator ended a toggled recording itself (error, length limit): the next press
    /// starts instead of stopping.
    public void ResetToggle() => Post(() => _machine.ResetToggle());

    /// Installs the hook on its thread. Throws <see cref="Win32Exception"/> when Windows refuses.
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() => HookThread(ready, e => error = e))
        {
            IsBackground = true,
            Name = "Diktat.KeyboardHook",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        ready.Wait();
        if (error is not null)
        {
            _thread = null;
            throw error;
        }
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;
        Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        if (Environment.CurrentManagedThreadId != thread.ManagedThreadId) thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;
    }

    /// Records the next shortcut the user presses, for the settings UI. While it runs the
    /// shortcuts are off and the pressed main key is swallowed. Completes with the combo,
    /// or null for Escape / cancellation. Modifier-only combos need at least two modifiers and
    /// complete when the first of them is released; a main key needs a modifier other than
    /// Shift (F-keys excepted). <paramref name="progress"/> reports the modifiers held so far
    /// (on the event context).
    public Task<KeyCombo?> CaptureNextComboAsync(IProgress<KeyCombo>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is null) throw new InvalidOperationException("Start() the hotkey service first.");
        var tcs = new TaskCompletionSource<KeyCombo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = tcs;
            _captureProgress = progress;
        }
        Post(() => _machine.BeginCapture());
        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => Post(() =>
            {
                if (_machine.IsCapturing) _machine.EndCapture(null);
            }));
            tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return tcs.Task;
    }

    /// German display name of a virtual key ("Leertaste", "F9", "A"), from the keyboard layout.
    public static string KeyName(int vk)
    {
        if (vk is >= VK.F1 and <= VK.F24) return "F" + (vk - VK.F1 + 1);
        uint scan = Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_VSC);
        if (scan == 0) return $"0x{vk:X2}";
        int lParam = (int)(scan << 16);
        if (KeyboardInput.IsExtendedKey(vk)) lParam |= 1 << 24;
        var buffer = new char[64];
        int length = Win32.GetKeyNameText(lParam, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : $"0x{vk:X2}";
    }

    // ---------------------------------------------------------------- hook thread

    private void HookThread(ManualResetEventSlim ready, Action<Exception> fail)
    {
        _threadId = Win32.GetCurrentThreadId();
        // Creates the thread's message queue before anyone posts to it.
        Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE);

        _hookProc = HookCallback;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            fail(new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed"));
            ready.Set();
            return;
        }
        ready.Set();
        Log.Info("Keyboard hook installed");

        try
        {
            int result;
            while ((result = Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                if (msg.message == Win32.WM_TIMER && msg.hwnd == IntPtr.Zero && msg.wParam == _timerId && _timerId != UIntPtr.Zero)
                {
                    SafeRun(_machine.OnDecisionTimer);
                    continue;
                }
                if (msg.message == WM_INVOKE)
                {
                    while (_work.TryDequeue(out var work)) SafeRun(work);
                    continue;
                }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
            if (result < 0) Log.Error($"Hook thread GetMessage failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            StopTimer();
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Info("Keyboard hook removed");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & (Win32.LLKHF_INJECTED | Win32.LLKHF_LOWER_IL_INJECTED)) != 0
                    || data.dwExtraInfo == (UIntPtr)(nuint)KeyboardInput.ExtraInfoTag;
                // AltGr sends a fake left Ctrl (scan code 0x21D) before right Alt; it is not a Ctrl press.
                bool altGrCtrl = data.vkCode == VK.LControl && data.scanCode == 0x21D;
                if (!injected && !altGrCtrl)
                {
                    uint message = (uint)wParam;
                    bool down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                    bool extended = (data.flags & Win32.LLKHF_EXTENDED) != 0;
                    if (_machine.OnKey((int)data.vkCode, (int)data.scanCode, extended, down))
                    {
                        return new IntPtr(1);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let an exception cross into Windows; never block the key because of a bug.
                Log.Error("Keyboard hook callback failed", ex);
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void StartTimer(int milliseconds)
    {
        StopTimer();
        _timerId = Win32.SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)milliseconds, IntPtr.Zero);
    }

    private void StopTimer()
    {
        if (_timerId == UIntPtr.Zero) return;
        Win32.KillTimer(IntPtr.Zero, _timerId);
        _timerId = UIntPtr.Zero;
    }

    private void Post(Action action)
    {
        if (_thread is null || _threadId == 0)
        {
            // Not started yet: apply directly (no concurrency before the thread exists).
            lock (_gate) SafeRun(action);
            return;
        }
        _work.Enqueue(action);
        Win32.PostThreadMessage(_threadId, WM_INVOKE, UIntPtr.Zero, IntPtr.Zero);
    }

    private static void SafeRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey work item failed", ex);
        }
    }

    private sealed class Host(GlobalHotkeyService owner) : IHotkeyHost
    {
        public void RaisePressed(WorkflowType type) => owner._events.Post(() => owner.Pressed?.Invoke(owner, type));
        public void RaiseReleased(WorkflowType type) => owner._events.Post(() => owner.Released?.Invoke(owner, type));
        public void RaiseCancelled() => owner._events.Post(() => owner.Cancelled?.Invoke(owner, EventArgs.Empty));
        public void RaisePotentialStart(WorkflowType type) => owner._events.Post(() => owner.PotentialStart?.Invoke(owner, type));

        public void CaptureProgress(KeyCombo held)
        {
            IProgress<KeyCombo>? progress;
            lock (owner._gate) progress = owner._captureProgress;
            if (progress is not null) owner._events.Post(() => progress.Report(held));
        }

        public void CaptureCompleted(KeyCombo? combo)
        {
            TaskCompletionSource<KeyCombo?>? capture;
            lock (owner._gate)
            {
                capture = owner._capture;
                owner._capture = null;
                owner._captureProgress = null;
            }
            capture?.TrySetResult(combo);
        }

        public void SendMenuMask() => KeyboardInput.Send(KeyboardInput.MenuMask());

        public void SendMenuMaskThenKeyUp(int vk, int scanCode, bool extended)
        {
            var inputs = KeyboardInput.MenuMask().ToList();
            inputs.Add(KeyboardInput.Key(vk, up: true, extended, (ushort)scanCode));
            KeyboardInput.Send(inputs);
        }

        public void StartDecisionTimer(int milliseconds) => owner.StartTimer(milliseconds);
        public void StopDecisionTimer() => owner.StopTimer();
        public bool IsKeyDown(int vk) => KeyboardInput.IsPhysicallyDown(vk);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = null;
        }
        _events.Dispose();
    }
}
