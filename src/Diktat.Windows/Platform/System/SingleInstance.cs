using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diktat.Windows.Platform;

/// One Diktat per user session. The first instance holds a named mutex and listens on a named
/// pipe; a second start sends its command line there (so the first one opens its window) and
/// exits.
///
/// Usage at startup:
/// <code>
/// var instance = SingleInstance.TryAcquire(SynchronizationContext.Current);
/// if (instance is null) { SingleInstance.SignalFirstInstance(args); return; }
/// instance.ActivationRequested += (_, a) => ShowMainWindow();
/// </code>
/// <see cref="ActivationRequested"/> is raised on the given context (or a background thread).
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventDispatcher _events;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;

    /// Another start of Diktat asked for the window; the argument is its command line.
    public event EventHandler<string[]>? ActivationRequested;

    private SingleInstance(Mutex mutex, string pipeName, SynchronizationContext? context)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _events = new EventDispatcher(context);
        _ = Task.Run(ListenAsync);
    }

    /// The per-user, per-session name base ("Local\" scopes the mutex to the session; the user
    /// hash keeps fast-user-switching sessions apart for the pipe).
    private static string BaseName
    {
        get
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
            return $"Diktat-{hash}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        }
    }

    /// Becomes the first instance, or returns null when one is running already.
    public static SingleInstance? TryAcquire(SynchronizationContext? eventContext = null)
    {
        var name = BaseName;
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous instance crashed
        }
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, name, eventContext);
    }

    /// Sends <paramref name="args"/> to the running instance. False when it did not answer.
    public static bool SignalFirstInstance(string[] args, TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", BaseName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);
            var payload = JsonSerializer.SerializeToUtf8Bytes(args);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not reach the running Diktat", ex);
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while (buffer.Length < 64 * 1024 && (read = await server.ReadAsync(chunk, _stop.Token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                }
                string[] args;
                try { args = JsonSerializer.Deserialize<string[]>(buffer.ToArray()) ?? []; }
                catch (JsonException) { args = []; }
                _events.Post(() => ActivationRequested?.Invoke(this, args));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("Single-instance pipe failed", ex);
                try { await Task.Delay(500, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _events.Dispose();
    }
}
