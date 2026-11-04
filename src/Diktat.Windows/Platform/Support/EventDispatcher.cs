using System.Threading.Channels;

namespace Diktat.Windows.Platform;

/// Delivers callbacks of the platform services in order, without blocking the thread they
/// come from (keyboard hook, audio capture thread, COM notification thread).
///
/// With a <see cref="SynchronizationContext"/> (typically the WPF dispatcher's) callbacks run
/// there via <see cref="SynchronizationContext.Post"/>. Without one they run on one private
/// background thread, one after the other.
internal sealed class EventDispatcher : IDisposable
{
    private readonly SynchronizationContext? _context;
    private readonly Channel<Action>? _queue;

    public EventDispatcher(SynchronizationContext? context)
    {
        _context = context;
        if (context is null)
        {
            _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
            var thread = new Thread(Pump) { IsBackground = true, Name = "Diktat.Events" };
            thread.Start();
        }
    }

    public void Post(Action action)
    {
        if (_context is not null)
        {
            _context.Post(static state => Run((Action)state!), action);
        }
        else
        {
            _queue!.Writer.TryWrite(action);
        }
    }

    private void Pump()
    {
        var reader = _queue!.Reader;
        while (true)
        {
            try
            {
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return;
            }
            catch
            {
                return;
            }
            while (reader.TryRead(out var action)) Run(action);
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // An exception in a subscriber must not kill the hook or capture thread.
            Log.Error("Event handler failed", ex);
        }
    }

    public void Dispose() => _queue?.Writer.TryComplete();
}
