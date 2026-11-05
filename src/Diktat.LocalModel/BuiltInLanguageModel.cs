using Diktat.AI;

namespace Diktat.LocalModel;

/// The model file the app should use right now: its name and path.
public sealed record LocalModelChoice(string Name, string Path);

/// The model the app runs itself, as the AI layer sees it. Which file that is comes from
/// <c>choose</c> (the installed and picked Gemma variant), asked anew on every request, so a
/// download, a switch or a deletion takes effect without a restart. The weights load on the first
/// request and are freed after <see cref="IdleTimeout"/> without one, so 3 to 5 GB of memory are
/// only taken while the model is in use.
public sealed class BuiltInLanguageModel : ILocalTextModel, IDisposable
{
    private readonly Func<LocalModelChoice?> _choose;
    private readonly LocalModelOptions _options;
    private readonly object _gate = new();
    private readonly Timer _idleTimer;
    private LocalLanguageModel? _model;
    private int _running;

    public BuiltInLanguageModel(Func<LocalModelChoice?> choose, LocalModelOptions? options = null)
    {
        _choose = choose;
        _options = options ?? new LocalModelOptions();
        _idleTimer = new Timer(_ => UnloadIfIdle());
    }

    /// Unused this long, the weights are freed. Live notes ask every few minutes, so this keeps
    /// the model during a meeting.
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(6);

    public bool IsAvailable => Current() is not null;

    public string Name => Current()?.Name ?? "";

    public bool IsLoaded
    {
        get { lock (_gate) return _model?.IsLoaded == true; }
    }

    private LocalModelChoice? Current()
    {
        try
        {
            return _choose() is { } choice && File.Exists(choice.Path) ? choice : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<LocalTextAnswer> CompleteAsync(string instruction, string text, int maxTokens, CancellationToken cancellationToken = default)
    {
        var choice = Current() ?? throw new TextRewriterException();
        LocalLanguageModel model;
        LocalLanguageModel? replaced = null;
        lock (_gate)
        {
            if (_model is null || _model.Path != choice.Path)
            {
                // Another model was picked: the old weights go (outside the lock, it may still answer).
                replaced = _model;
                _model = new LocalLanguageModel(choice.Path, _options);
            }
            model = _model;
            _running++;
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        replaced?.Dispose();
        try
        {
            var completion = await model.CompleteAsync(instruction, text, maxTokens, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LocalTextAnswer(completion.Text, completion.CutOff);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
                if (_running == 0) _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// Frees the weights now (before deleting the file, at quit).
    public void Unload()
    {
        LocalLanguageModel? model;
        lock (_gate)
        {
            model = _model;
            _model = null;
        }
        model?.Dispose();
    }

    private void UnloadIfIdle()
    {
        LocalLanguageModel? model;
        lock (_gate)
        {
            if (_running > 0) return;
            model = _model;
            _model = null;
        }
        model?.Dispose();
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Unload();
    }
}
