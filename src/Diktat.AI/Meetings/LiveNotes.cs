namespace Diktat.AI.Meetings;

public enum LiveNotesFailureKind
{
    NotesTooLong,
    /// The model's answer reached its token limit, e.g. because it repeated itself on a garbled transcript.
    AnswerCutOff,
}

public sealed class LiveNotesException : Exception
{
    public LiveNotesFailureKind Kind { get; }

    public LiveNotesException(LiveNotesFailureKind kind) : base(kind switch
    {
        LiveNotesFailureKind.NotesTooLong => L.T("Die Notizen sind zu lang für das Modell geworden."),
        _ => L.T("Die Antwort des Modells wurde zu lang."),
    })
    {
        Kind = kind;
    }
}

/// The model behind the live notes. On this PC only: a model the app runs itself, or the model
/// server on this PC (Ollama, LM Studio). Never a server elsewhere or a cloud provider.
/// A backend that cuts off its answer should throw <see cref="LiveNotesException"/> with
/// <see cref="LiveNotesFailureKind.AnswerCutOff"/>.
public sealed record LiveNotesBackend(string Name, int CharacterLimit, Func<string, string, CancellationToken, Task<string>> Complete,
    Func<Task>? Close = null)
{
    /// The model server on this PC, null when none is set up. It keeps its model loaded itself.
    public static LiveNotesBackend? ServerOnThisPC(ITextRewriter rewriter)
    {
        if (rewriter.ServerOnThisPC is not { } config) return null;
        var server = config.BaseUrl.Contains(":1234", StringComparison.Ordinal) ? "LM Studio" : "Ollama";
        return new LiveNotesBackend($"{config.Model} ({server})", MeetingModelBackend.LocalServerCharacterLimit,
            (instruction, text, cancellationToken) => rewriter.RewriteOnServerOnThisPCAsync(text, instruction, cancellationToken));
    }
}

/// Notes that grow while a meeting runs: every few minutes the newest stretch is transcribed and
/// the model updates topics, decisions, open questions and to-dos. This is the logic only; the
/// app owns the timer (<see cref="Interval"/>), the audio and the transcription, and hands over
/// each new stretch of text. Use it from one thread (the UI thread).
public sealed class LiveNotes
{
    /// How often the notes catch up with the meeting.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(150);

    private readonly Func<CancellationToken, Task<LiveNotesBackend>> _makeBackend;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private Task<LiveNotesBackend>? _backendLoad;
    private Task? _update;
    private Task? _catchUpTask;
    private bool _isStopped;
    /// Transcribed text that is not in the notes yet.
    private string _pendingText = "";
    /// The start of the pending text the model failed on last time.
    private string? _failedText;
    /// What was said, stretch by stretch, for "Was habe ich verpasst?".
    private readonly List<(DateTimeOffset At, string Text)> _stretches = new();

    /// <param name="makeBackend">Loads the model once, e.g. <c>_ => Task.FromResult(LiveNotesBackend.ServerOnThisPC(rewriter) ?? throw new TextRewriterException())</c>.</param>
    public LiveNotes(Func<CancellationToken, Task<LiveNotesBackend>> makeBackend, TimeProvider? time = null)
    {
        _makeBackend = makeBackend;
        _time = time ?? TimeProvider.System;
    }

    public string Notes { get; private set; } = "";
    public DateTimeOffset? UpdatedAt { get; private set; }
    public bool IsUpdating { get; private set; }
    public string? Problem { get; private set; }
    /// "Was habe ich verpasst?"
    public string? CatchUp { get; private set; }
    public bool IsCatchingUp { get; private set; }
    /// The model that writes the notes, e.g. "qwen3.5:9b (Ollama)".
    public string? ModelName { get; private set; }

    /// Raised after any of the properties above changed.
    public event EventHandler? Changed;

    /// What was transcribed so far.
    public string Transcript => string.Join("\n", _stretches.Select(s => s.Text));

    /// Adds a transcribed stretch without updating the notes yet.
    public void AddStretch(string stretch)
    {
        if (string.IsNullOrWhiteSpace(stretch)) return;
        _stretches.Add((_time.GetUtcNow(), stretch));
        _pendingText += (_pendingText.Length == 0 ? "" : "\n") + stretch;
    }

    /// Transcribes what is new (when <paramref name="transcribeNewAudio"/> is given) and updates the
    /// notes. Does nothing while an update runs or after <see cref="StopAsync"/>. A failed
    /// transcription sets <see cref="Problem"/>; the app keeps its audio for the next try.
    public Task UpdateAsync(Func<CancellationToken, Task<string>>? transcribeNewAudio = null)
    {
        if (IsUpdating || _isStopped) return Task.CompletedTask;
        if (transcribeNewAudio is null && _pendingText.Length == 0) return Task.CompletedTask;
        IsUpdating = true;
        Changed?.Invoke(this, EventArgs.Empty);
        _update = RunUpdateAsync(transcribeNewAudio, _stop.Token);
        return _update;
    }

    private async Task RunUpdateAsync(Func<CancellationToken, Task<string>>? transcribeNewAudio, CancellationToken cancellationToken)
    {
        try
        {
            if (transcribeNewAudio is not null)
            {
                AddStretch(await transcribeNewAudio(cancellationToken).ConfigureAwait(true));
            }
            if (_pendingText.Length == 0 || cancellationToken.IsCancellationRequested) return;
            var leftOut = await WriteNotesAsync(await LoadedBackendAsync(cancellationToken).ConfigureAwait(true), cancellationToken)
                .ConfigureAwait(true);
            UpdatedAt = _time.GetUtcNow();
            Problem = leftOut
                ? L.T("Ein Abschnitt ließ sich nicht in die Notizen übernehmen. Im Transkript nach dem Meeting fehlt nichts.")
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Problem = L.F("Live-Notizen gerade nicht möglich: {0}", e.Message);
        }
        finally
        {
            IsUpdating = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// Works the pending text into the notes, in parts when it does not fit at once. The model
    /// answers the same text the same way every time, so text it fails on twice, or that makes its
    /// answer too long once, is left out instead of blocking every later update. Returns whether
    /// that happened.
    private async Task<bool> WriteNotesAsync(LiveNotesBackend backend, CancellationToken cancellationToken)
    {
        var german = MeetingSummarizer.IsGermanMeeting(Transcript);
        var instruction = german ? Instruction : EnglishInstruction;
        var heading = german ? "Neuer Abschnitt:\n" : "New section:\n";
        var leftOut = false;
        while (_pendingText.Length > 0)
        {
            var earlier = Notes.Length == 0 ? "" : (german ? "Bisherige Notizen:\n" : "Notes so far:\n") + Notes + "\n\n";
            var room = backend.CharacterLimit - earlier.Length - heading.Length;
            if (room < 1_000) throw new LiveNotesException(LiveNotesFailureKind.NotesTooLong);
            var end = TextParts.EndOfLeadingPart(_pendingText, room);
            var part = _pendingText[..end];
            try
            {
                var result = await backend.Complete(instruction, earlier + heading + part, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                Notes = MeetingSummarizer.Cleaned(result);
                _failedText = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                if (e is LiveNotesException { Kind: LiveNotesFailureKind.AnswerCutOff })
                {
                    // Leave out this part.
                }
                else if (_failedText is not null && part.StartsWith(_failedText, StringComparison.Ordinal))
                {
                    end = _failedText.Length;
                }
                else
                {
                    _failedText = part;
                    throw;
                }
                _failedText = null;
                leftOut = true;
            }
            _pendingText = _pendingText[end..].Trim();
        }
        return leftOut;
    }

    /// A few sentences on the last minutes.
    public Task CatchUpOnLastMinutesAsync(double minutes = 10)
    {
        if (IsCatchingUp || _isStopped) return Task.CompletedTask;
        var since = _time.GetUtcNow().AddMinutes(-minutes);
        var recent = string.Join("\n\n", _stretches.Where(s => s.At >= since).Select(s => s.Text));
        if (recent.Length == 0)
        {
            CatchUp = L.T("Noch nichts aufgeschrieben. Die ersten Notizen kommen nach etwa zweieinhalb Minuten.");
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        IsCatchingUp = true;
        Changed?.Invoke(this, EventArgs.Empty);
        _catchUpTask = RunCatchUpAsync(recent, _stop.Token);
        return _catchUpTask;
    }

    private async Task RunCatchUpAsync(string recent, CancellationToken cancellationToken)
    {
        try
        {
            var backend = await LoadedBackendAsync(cancellationToken).ConfigureAwait(true);
            // In a busy meeting ten minutes can be more than the model reads; the newest part counts.
            var text = recent[TextParts.StartOfTrailingPart(recent, backend.CharacterLimit)..];
            var german = MeetingSummarizer.IsGermanMeeting(text);
            var answer = await backend.Complete(german ? CatchUpInstruction : EnglishCatchUpInstruction, text, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            CatchUp = answer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            CatchUp = L.F("Gerade nicht möglich: {0}", e.Message);
        }
        finally
        {
            IsCatchingUp = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DismissCatchUp()
    {
        CatchUp = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// Ends the notes and frees the model; the meeting's evaluation waits for this.
    public async Task StopAsync()
    {
        _isStopped = true;
        _stop.Cancel();
        if (_update is { } update) await update.ConfigureAwait(true);
        if (_catchUpTask is { } catchUp) await catchUp.ConfigureAwait(true);
        // A load still running ends with a model in memory; free it as well.
        if (_backendLoad is { } load)
        {
            try
            {
                var backend = await load.ConfigureAwait(true);
                if (backend.Close is { } close) await close().ConfigureAwait(true);
            }
            catch (Exception) { }
        }
        _backendLoad = null;
    }

    private async Task<LiveNotesBackend> LoadedBackendAsync(CancellationToken cancellationToken)
    {
        var load = _backendLoad ??= _makeBackend(_stop.Token);
        try
        {
            var backend = await load.WaitAsync(cancellationToken).ConfigureAwait(true);
            ModelName = backend.Name;
            return backend;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The next update tries again, e.g. once memory is free.
            if (_backendLoad == load) _backendLoad = null;
            throw;
        }
    }

    /// The newest stretch as "Ich: ..." and "Andere: ..." paragraphs. People are only told apart
    /// at the end; live, the system sound is everybody else.
    /// <param name="postProcess">The dictation post-processing, or null.</param>
    public static string ComposeStretch(IReadOnlyList<TimedWord> own, IReadOnlyList<TimedWord> others, string? ownName,
        Func<string, string>? postProcess = null)
    {
        var kept = others.Count == 0 ? own : EchoFilter.RemoveEcho(own, others);
        var me = ownName ?? "Ich";
        List<(double Start, string Text)> lines;
        if (others.Count == 0)
        {
            lines = SpeakerAlignment.Paragraphs(kept, []).Select(p => (p.Start, p.Text)).ToList();
        }
        else
        {
            lines = SpeakerAlignment.Paragraphs(kept, []).Select(p => (p.Start, $"{me}: {p.Text}"))
                .Concat(SpeakerAlignment.Paragraphs(others, []).Select(p => (p.Start, $"Andere: {p.Text}")))
                .ToList();
        }
        return string.Join("\n", lines.OrderBy(l => l.Start)
            .Select(l => (postProcess?.Invoke(l.Text) ?? l.Text).Trim())
            .Where(text => text.Length > 0));
    }

    // MARK: Instructions

    public const string Instruction = """
        Du führst laufende Notizen zu einem Meeting, das gerade stattfindet. Du erhältst die bisherigen Notizen und den neuesten Abschnitt des Transkripts. Wer spricht, ist markiert; „Andere“ sind alle Personen außer der, die die Notizen nutzt.
        Gib die vollständigen, aktualisierten Notizen zurück, genau mit diesen vier Abschnitten, jede Überschrift auf einer eigenen Zeile und ihre Punkte direkt darunter:

        Themen
        Spiegelstriche mit „- “: worüber gesprochen wurde.

        Entscheidungen
        Spiegelstriche mit „- “: nur, was die Runde ausdrücklich vereinbart hat, keine Meinung und kein Vorschlag.

        Offene Fragen
        Spiegelstriche mit „- “: was noch ungeklärt ist.

        To-dos
        Spiegelstriche im Format „- Wer: Was“. „Wer“ ist der Name der Person, die es laut Transkript übernimmt. Nennt das Transkript niemanden, schreibe „- Offen: Was“.

        Höchstens acht Punkte je Abschnitt; werden es mehr, fasse Ähnliches zusammen. Übernimm aus den bisherigen Notizen, was weiterhin gilt, und ergänze das Neue. Hat ein Abschnitt keinen Inhalt, steht darunter „- Noch keine“.
        Erfinde nichts dazu, vor allem keine Termine, Fristen, Namen oder Zahlen. Keine Einleitung.
        """;

    public const string EnglishInstruction = """
        You keep running notes on a meeting that is taking place right now. You receive the notes so far and the newest section of the transcript. Speakers are marked; "Andere" means everybody except the person using the notes.
        Return the complete, updated notes with exactly these four sections, each heading on its own line and its bullet points right below it:

        Topics
        Bullet points starting with "- ": what was discussed.

        Decisions
        Bullet points starting with "- ": only what the group explicitly agreed on, not an opinion or a proposal.

        Open questions
        Bullet points starting with "- ": what is still unresolved.

        To-dos
        Bullet points in the format "- Who: What". "Who" is the name of the person who takes it on according to the transcript. If the transcript names nobody, write "- Open: What".

        At most eight bullet points per section; when there are more, merge similar ones. Keep from the notes so far what still holds and add what is new. A section without content gets "- None yet" below it.
        Do not invent anything, above all no dates, deadlines, names or numbers. No introduction.
        """;

    public const string CatchUpInstruction = """
        Du erhältst das Transkript der letzten Minuten eines laufenden Meetings. Fasse in drei bis fünf kurzen Sätzen zusammen, was besprochen wurde, das Wichtigste zuerst: Entscheidungen, Aufgaben, offene Fragen. Nenne als Entscheidung nur, was ausdrücklich vereinbart wurde; Meinungen und Vorschläge sind keine. Erfinde nichts dazu. Keine Einleitung.
        """;

    public const string EnglishCatchUpInstruction = """
        You receive the transcript of the last minutes of a meeting that is still running. Summarize in three to five short sentences what was discussed, most important first: decisions, tasks, open questions. Call something a decision only when it was explicitly agreed; opinions and proposals are not. Do not invent anything. No introduction.
        """;
}
