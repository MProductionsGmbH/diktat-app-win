namespace Diktat.Windows.Platform;

/// Runs UI Automation calls with a time cap. UIA talks to the target process and can hang when
/// that app hangs; each call gets its own MTA thread (LongRunning), so a stuck call never blocks
/// the caller or the next call. A hung thread is abandoned (background thread).
internal static class UiaRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(300);

    public static async Task<T?> RunAsync<T>(Func<T?> func, TimeSpan? timeout = null)
    {
        var task = Task.Factory.StartNew(() =>
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                // ElementNotAvailableException, COMException, timeouts inside UIA ...
                Log.Warn("UI Automation call failed", ex);
                return default;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        var finished = await Task.WhenAny(task, Task.Delay(timeout ?? DefaultTimeout)).ConfigureAwait(false);
        if (finished == task) return await task.ConfigureAwait(false);
        Log.Info("UI Automation call timed out");
        return default;
    }
}
