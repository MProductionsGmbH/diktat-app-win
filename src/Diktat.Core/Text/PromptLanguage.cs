namespace Diktat.Core.Text;

/// The language the instructions to the language model are written in. It follows what was
/// spoken: a German dictation gets the German instructions, anything else the English ones,
/// which tell the model to answer in the language of the transcript.
public enum PromptLanguage
{
    German,
    English,
}

public static class PromptLanguages
{
    /// The code the filler-word list and the sentence tokenizer know.
    public static string Code(this PromptLanguage language) => language == PromptLanguage.German ? "de" : "en";

    /// German, or too little text to tell: German is the language the prompts were written in.
    public static PromptLanguage Detect(string text) =>
        LanguageGuesser.IsGermanOrUnknown(text) ? PromptLanguage.German : PromptLanguage.English;

    /// The dictation language setting ("de", "en", or "auto") decides when it names a language;
    /// "auto" follows the text.
    public static PromptLanguage Resolve(string setting, string text)
    {
        var prefix = (setting ?? "").Trim().ToLowerInvariant();
        if (prefix.StartsWith("de")) return PromptLanguage.German;
        if (prefix.StartsWith("en")) return PromptLanguage.English;
        return Detect(text);
    }
}
