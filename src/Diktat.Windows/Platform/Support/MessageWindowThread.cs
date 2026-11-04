using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// A dedicated STA thread with a message-only window and its own message loop. Work is
/// marshalled onto it with <see cref="InvokeAsync{T}"/>; window messages go to the handler
/// passed to the constructor. Used for the clipboard, which needs an owner window whose thread
/// keeps pumping (other apps send WM_RENDERFORMAT / WM_DESTROYCLIPBOARD to it synchronously).
internal sealed class MessageWindowThread : IDisposable
{
    /// Return a value to handle the message, null to pass it to DefWindowProc.
    public delegate IntPtr? MessageHandler(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_INVOKE = Win32.WM_APP + 0x51;

    private readonly MessageHandler? _handler;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _className = "Diktat.MessageWindow." + Guid.NewGuid().ToString("N");
    private Win32.WndProc? _wndProc; // kept alive while the window exists
    private Exception? _startError;
    private volatile bool _disposed;

    public IntPtr Handle { get; private set; }
    public int ManagedThreadId => _thread.ManagedThreadId;

    public MessageWindowThread(string name, MessageHandler? handler)
    {
        _handler = handler;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startError is not null) throw _startError;
    }

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.SetException(new ObjectDisposedException(nameof(MessageWindowThread)));
            return tcs.Task;
        }
        if (IsCurrentThread)
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!Win32.PostMessage(Handle, WM_INVOKE, IntPtr.Zero, IntPtr.Zero))
        {
            tcs.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(), "PostMessage failed"));
        }
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    /// Runs <paramref name="action"/> on the thread after <paramref name="delay"/>, unless the
    /// returned handle is disposed first.
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var cancelled = 0;
        var timer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref cancelled) != 0 || _disposed) return;
            _ = InvokeAsync(() =>
            {
                if (Volatile.Read(ref cancelled) == 0) action();
            });
        }, null, delay, Timeout.InfiniteTimeSpan);
        return new ScheduledWork(timer, () => Interlocked.Exchange(ref cancelled, 1));
    }

    private sealed class ScheduledWork(System.Threading.Timer timer, Action cancel) : IDisposable
    {
        public void Dispose()
        {
            cancel();
            timer.Dispose();
        }
    }

    private void Run()
    {
        var hInstance = Win32.GetModuleHandle(null);
        try
        {
            _wndProc = WindowProc;
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = _className,
            };
            if (Win32.RegisterClassEx(ref wc) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
            Handle = Win32.CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }
        _ready.Set();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        DrainWork();
        Win32.UnregisterClass(_className, hInstance);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_INVOKE)
            {
                DrainWork();
                return IntPtr.Zero;
            }
            if (msg == Win32.WM_CLOSE)
            {
                Win32.DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (msg == Win32.WM_DESTROY)
            {
                // Lets the clipboard owner render pending formats before the window goes.
                _handler?.Invoke(hwnd, msg, wParam, lParam);
                PostQuit();
                return IntPtr.Zero;
            }
            var result = _handler?.Invoke(hwnd, msg, wParam, lParam);
            if (result is IntPtr handled) return handled;
        }
        catch (Exception ex)
        {
            Log.Error($"Message 0x{msg:X4} failed", ex);
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    private static void PostQuit() => PostQuitMessage(0);

    private void DrainWork()
    {
        while (_work.TryDequeue(out var work)) work();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != IntPtr.Zero) Win32.PostMessage(Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
