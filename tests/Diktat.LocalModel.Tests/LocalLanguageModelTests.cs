namespace Diktat.LocalModel.Tests;

/// Runs the real model when DIKTAT_TEST_GGUF names a GGUF file (Gemma 4 E2B or E4B). Without it the
/// test passes without doing anything: CI has no 3 GB model.
public sealed class LocalLanguageModelTests
{
    private static string? ModelPath => Environment.GetEnvironmentVariable("DIKTAT_TEST_GGUF") is { Length: > 0 } path && File.Exists(path) ? path : null;

    [Fact]
    public async Task RewritesAGermanDictation()
    {
        if (ModelPath is not { } path) return;
        using var model = new LocalLanguageModel(path, new LocalModelOptions(ContextSize: 4096));
        var answer = await model.CompleteAsync(
            "Überarbeite das diktierte Transkript zu sauberem Text. Entferne Füllwörter und Wiederholungen, korrigiere Grammatik und Zeichensetzung. Gib NUR den fertigen Text zurück.",
            "ähm also ich wollte dir nur kurz sagen dass das meeting morgen ähm nicht um zehn sondern um elf uhr stattfindet also um elf",
            maxTokens: 200);
        var stats = model.LastCompletion!;
        Console.WriteLine($"{answer.Text}\n--- prompt {stats.PromptTokens} tokens in {stats.PromptTime.TotalSeconds:0.0} s, {stats.GeneratedTokens} tokens at {stats.TokensPerSecond:0.0}/s");
        Assert.False(answer.CutOff);
        Assert.Contains("11", answer.Text.Replace("elf", "11", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ähm", answer.Text);
        Assert.DoesNotContain("<", answer.Text);
    }

    [Fact]
    public async Task ReportsAnAnswerCutOffByTheLimit()
    {
        if (ModelPath is not { } path) return;
        using var model = new LocalLanguageModel(path, new LocalModelOptions(ContextSize: 2048));
        var answer = await model.CompleteAsync("Schreibe eine lange Geschichte.", "Ein Hund und eine Katze.", maxTokens: 8);
        Assert.True(answer.CutOff);
        await Assert.ThrowsAsync<LocalModelException>(() =>
            model.CompleteAsync("Schreibe eine lange Geschichte.", "Ein Hund und eine Katze.", maxTokens: 8, failsWhenCutOff: true));
    }

    [Fact]
    public async Task MissingFileIsAReadableError()
    {
        using var model = new LocalLanguageModel(Path.Combine(Path.GetTempPath(), "fehlt-" + Guid.NewGuid() + ".gguf"));
        var error = await Assert.ThrowsAsync<LocalModelException>(() => model.CompleteAsync("a", "b", 4));
        Assert.StartsWith("Die Modelldatei fehlt", error.Message);
    }
}
