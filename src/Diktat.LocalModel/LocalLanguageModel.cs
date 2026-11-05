using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Diktat.LocalModel;

/// A failure of the local model the user can read.
public sealed class LocalModelException(string message, Exception? inner = null) : Exception(message, inner);

/// How the model runs.
/// <param name="ContextSize">Tokens of prompt plus answer; 16K fit the longest meeting part (40,000 characters).</param>
/// <param name="GpuLayers">Layers on the graphics card; 0 runs on the processor only.</param>
public sealed record LocalModelOptions(int ContextSize = 16_384, int GpuLayers = 0);

/// The answer and how long it took.
public sealed record LocalCompletion(string Text, int PromptTokens, int GeneratedTokens, TimeSpan PromptTime, TimeSpan GenerationTime, bool CutOff)
{
    public double TokensPerSecond => GenerationTime.TotalSeconds > 0 ? GeneratedTokens / GenerationTime.TotalSeconds : 0;
}

/// A language model in a GGUF file, run by llama.cpp inside the app (LLamaSharp). Tested with
/// Gemma 4 E2B and E4B (Google's 4-bit QAT files). Load once, answer one or more prompts, then
/// <see cref="Unload"/>: the weights stay in memory until then (3 to 5 GB).
///
/// One request at a time; others wait. Greedy (temperature 0), so the same input gives the same
/// answer, like the local model on the Mac.
public sealed class LocalLanguageModel : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _isGemma4;
    private bool _closesThinking;

    public LocalLanguageModel(string path, LocalModelOptions? options = null)
    {
        Path = path;
        Options = options ?? new LocalModelOptions();
    }

    public string Path { get; }
    public LocalModelOptions Options { get; }
    public bool IsLoaded => _context is not null;

    /// Figures of the last answer.
    public LocalCompletion? LastCompletion { get; private set; }

    /// Reads the weights; <see cref="CompleteAsync"/> calls it when needed.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Answers <paramref name="text"/> following <paramref name="instruction"/> (the system turn).
    /// <param name="failsWhenCutOff">Throw instead of returning an answer that hit <paramref name="maxTokens"/>.</param>
    public async Task<LocalCompletion> CompleteAsync(string instruction, string text, int maxTokens, bool failsWhenCutOff = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var completion = await Task.Run(() => Generate(instruction, text, maxTokens, cancellationToken), cancellationToken).ConfigureAwait(false);
            LastCompletion = completion;
            if (failsWhenCutOff && completion.CutOff) throw new LocalModelException(L.T("Die Antwort war zu lang und wurde abgeschnitten."));
            return completion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Frees the weights. Waits for a running answer.
    public void Unload()
    {
        _gate.Wait();
        try
        {
            UnloadLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => Unload();

    private async Task LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (_context is not null) return;
        if (!File.Exists(Path)) throw new LocalModelException(L.F("Die Modelldatei fehlt: {0}", System.IO.Path.GetFileName(Path)));
        LocalModelRuntime.Configure();
        var parameters = new ModelParams(Path)
        {
            ContextSize = (uint)Options.ContextSize,
            GpuLayerCount = Options.GpuLayers,
            Threads = LocalModelRuntime.Threads,
            BatchThreads = Environment.ProcessorCount,
            BatchSize = 512,
        };
        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
            _context = _weights.CreateContext(parameters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UnloadLocked();
            throw new LocalModelException(L.F("Das Modell konnte nicht geladen werden: {0}", ex.Message), ex);
        }
        _weights.Metadata.TryGetValue("general.architecture", out var architecture);
        _weights.Metadata.TryGetValue("tokenizer.chat_template", out var template);
        _isGemma4 = GemmaChatFormat.IsGemma4(architecture);
        _closesThinking = GemmaChatFormat.ClosesThinking(template);
    }

    private void UnloadLocked()
    {
        _context?.Dispose();
        _context = null;
        _weights?.Dispose();
        _weights = null;
    }

    private string PromptFor(string instruction, string text)
    {
        if (_isGemma4) return GemmaChatFormat.Prompt(instruction, text, _closesThinking);
        var template = new LLamaTemplate(_weights!) { AddAssistant = true };
        if (instruction.Trim().Length > 0) template.Add("system", instruction.Trim());
        template.Add("user", text.Trim());
        return Encoding.UTF8.GetString(template.Apply());
    }

    private LocalCompletion Generate(string instruction, string text, int maxTokens, CancellationToken cancellationToken)
    {
        var context = _context!;
        var vocab = context.Vocab;
        var tokens = context.Tokenize(PromptFor(instruction, text), addBos: false, special: true);
        if (tokens.Length + maxTokens > context.ContextSize)
            throw new LocalModelException(L.T("Der Text ist für das Modell auf diesem PC zu lang."));

        context.NativeHandle.MemoryClear(true);
        var clock = Stopwatch.StartNew();
        var batch = new LLamaBatch();
        var batchSize = (int)context.BatchSize;
        for (var start = 0; start < tokens.Length; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Clear();
            var end = Math.Min(start + batchSize, tokens.Length);
            for (var i = start; i < end; i++) batch.Add(tokens[i], i, LLamaSeqId.Zero, i == tokens.Length - 1);
            Check(context.Decode(batch));
        }
        var promptTime = clock.Elapsed;
        clock.Restart();

        using var sampler = new GreedySamplingPipeline();
        var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = true };
        var output = new StringBuilder();
        var position = tokens.Length;
        var generated = 0;
        var ended = false;
        while (generated < maxTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = sampler.Sample(context.NativeHandle, batch.TokenCount - 1);
            if (token.IsEndOfGeneration(vocab))
            {
                ended = true;
                break;
            }
            sampler.Accept(token);
            decoder.Add(token);
            output.Append(decoder.Read());
            generated++;
            // A turn end that is not an end-of-generation token in this vocabulary.
            if (_isGemma4 && output.ToString().Contains(GemmaChatFormat.TurnEnd, StringComparison.Ordinal))
            {
                ended = true;
                break;
            }
            batch.Clear();
            batch.Add(token, position++, LLamaSeqId.Zero, true);
            Check(context.Decode(batch));
        }
        var answer = _isGemma4 ? GemmaChatFormat.Cleaned(output.ToString()) : output.ToString().Trim();
        return new LocalCompletion(answer, tokens.Length, generated, promptTime, clock.Elapsed, CutOff: !ended);
    }

    private static void Check(DecodeResult result)
    {
        if (result != DecodeResult.Ok) throw new LocalModelException(L.F("Das Modell konnte nicht rechnen ({0}).", result));
    }
}
