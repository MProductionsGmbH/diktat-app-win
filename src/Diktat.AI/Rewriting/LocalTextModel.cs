namespace Diktat.AI;

/// The language model the app runs itself (on Windows Gemma 4 through llama.cpp), once it is
/// downloaded. It plays the part Apple Intelligence plays on the Mac: the text never leaves the PC
/// and no other software is needed.
public interface ILocalTextModel
{
    /// Whether a model is installed and can answer.
    bool IsAvailable { get; }

    /// Shown in notes, e.g. "Gemma 4 E2B".
    string Name { get; }

    /// Answers <paramref name="text"/> following <paramref name="instruction"/>, greedy.
    Task<LocalTextAnswer> CompleteAsync(string instruction, string text, int maxTokens, CancellationToken cancellationToken = default);
}

/// <param name="CutOff">The answer reached the token limit and may be incomplete.</param>
public sealed record LocalTextAnswer(string Text, bool CutOff);
