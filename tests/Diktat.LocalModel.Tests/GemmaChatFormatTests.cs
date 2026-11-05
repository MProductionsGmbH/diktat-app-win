namespace Diktat.LocalModel.Tests;

/// The same cases as GemmaChatFormatTests.swift in the Mac app.
public sealed class GemmaChatFormatTests
{
    [Fact]
    public void PromptWithSystemTurnAndClosedThinking() => Assert.Equal(
        "<bos><|turn>system\nFasse zusammen.<turn|>\n<|turn>user\nWir verschieben den Launch.<turn|>\n<|turn>model\n<|channel>thought\n<channel|>",
        GemmaChatFormat.Prompt("Fasse zusammen.", "Wir verschieben den Launch.", closesThinking: true));

    [Fact]
    public void PromptForOlderTemplateEndsAtModelTurn() => Assert.Equal(
        "<bos><|turn>system\nFasse zusammen.<turn|>\n<|turn>user\nText<turn|>\n<|turn>model\n",
        GemmaChatFormat.Prompt("Fasse zusammen.", "Text", closesThinking: false));

    [Fact]
    public void PromptWithoutSystemTurn() =>
        Assert.Equal("<bos><|turn>user\nHallo<turn|>\n<|turn>model\n", GemmaChatFormat.Prompt("  ", "Hallo", closesThinking: false));

    [Fact]
    public void PromptTrimsLikeTheTemplate()
    {
        var prompt = GemmaChatFormat.Prompt("\n Anweisung \n", "\n\nText\n", closesThinking: false);
        Assert.Contains("system\nAnweisung<turn|>", prompt);
        Assert.Contains("user\nText<turn|>", prompt);
    }

    [Fact]
    public void DetectsTemplateThatClosesThinking()
    {
        Assert.True(GemmaChatFormat.ClosesThinking("{%- if not enable_thinking | default(false) -%}\n    {{- '<|channel>thought\\n<channel|>' -}}\n{%- endif -%}"));
        Assert.False(GemmaChatFormat.ClosesThinking("{{- '<|channel>thought\\n' + thinking_text + '\\n<channel|>' -}}"));
    }

    [Fact]
    public void RecognizesGemma4()
    {
        Assert.True(GemmaChatFormat.IsGemma4("gemma4"));
        Assert.True(GemmaChatFormat.IsGemma4("gemma4_text"));
        Assert.False(GemmaChatFormat.IsGemma4("gemma3"));
        Assert.False(GemmaChatFormat.IsGemma4(null));
    }

    [Theory]
    [InlineData("Der Launch verschiebt sich.\n", "Der Launch verschiebt sich.")]
    [InlineData("<|channel>thought\nDer Nutzer will zwei Sätze.<channel|>Der Launch verschiebt sich um eine Woche.", "Der Launch verschiebt sich um eine Woche.")]
    [InlineData("<|channel>thought\nIch überlege noch", "")]
    [InlineData("Kurzfassung.<turn|>\n<|turn>user\nNoch etwas", "Kurzfassung.")]
    [InlineData("<channel|>Antwort<eos>", "Antwort")]
    public void CleanedKeepsTheAnswerOnly(string output, string expected) => Assert.Equal(expected, GemmaChatFormat.Cleaned(output));
}
