namespace Diktat.LocalModel;

/// The Gemma 4 chat format, written out like the model's chat template renders it (the same
/// as on the Mac, GemmaChatFormat.swift):
///
///     <bos><|turn>system\n{system}<turn|>\n<|turn>user\n{user}<turn|>\n<|turn>model\n
///
/// with thinking off, which newer templates close right away with an empty thought channel.
/// Also strips what is not part of the answer from the generated text.
public static class GemmaChatFormat
{
    public const string BosToken = "<bos>";
    public const string TurnStart = "<|turn>";
    public const string TurnEnd = "<turn|>";
    public const string ChannelStart = "<|channel>";
    public const string ChannelEnd = "<channel|>";

    /// Whether a GGUF architecture or model type is Gemma 4 ("gemma4", "gemma4_text").
    public static bool IsGemma4(string? architecture) =>
        architecture is not null && architecture.StartsWith("gemma4", StringComparison.OrdinalIgnoreCase);

    /// Whether the chat template closes the thinking channel when thinking is off.
    public static bool ClosesThinking(string? template) =>
        template is not null && template.Contains("<|channel>thought\\n<channel|>", StringComparison.Ordinal);

    public static string Prompt(string system, string user, bool closesThinking)
    {
        var text = BosToken;
        system = system.Trim();
        if (system.Length > 0) text += TurnStart + "system\n" + system + TurnEnd + "\n";
        text += TurnStart + "user\n" + user.Trim() + TurnEnd + "\n";
        text += TurnStart + "model\n";
        if (closesThinking) text += ChannelStart + "thought\n" + ChannelEnd;
        return text;
    }

    /// The answer alone: without thinking, without anything after the end of the model's turn
    /// and without control tokens.
    public static string Cleaned(string output)
    {
        var text = output;
        // Thinking runs from <|channel> to <channel|>; one that never closes was cut off.
        while (text.IndexOf(ChannelStart, StringComparison.Ordinal) is var start and >= 0)
        {
            var end = text.IndexOf(ChannelEnd, start + ChannelStart.Length, StringComparison.Ordinal);
            text = end >= 0 ? text.Remove(start, end + ChannelEnd.Length - start) : text[..start];
        }
        text = text.Replace(ChannelEnd, "", StringComparison.Ordinal);
        var turnEnd = text.IndexOf(TurnEnd, StringComparison.Ordinal);
        if (turnEnd >= 0) text = text[..turnEnd];
        foreach (var token in new[] { "<eos>", BosToken, "<pad>", TurnStart })
            text = text.Replace(token, "", StringComparison.Ordinal);
        return text.Trim();
    }
}
