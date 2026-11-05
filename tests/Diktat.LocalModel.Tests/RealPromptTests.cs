using Diktat.AI;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.LocalModel.Tests;

/// The app's own prompts through TextRewriter with the built-in model, when DIKTAT_TEST_GGUF names
/// a Gemma file. Prints the answers and timings, so a person can judge them; checks only the basics.
public sealed class RealPromptTests
{
    private static string? ModelPath => Environment.GetEnvironmentVariable("DIKTAT_TEST_GGUF") is { Length: > 0 } path && File.Exists(path) ? path : null;

    private const string Rambling =
        "also ähm ich wollte nur sagen dass wir das Angebot für die Firma Meier äh bis Freitag fertig haben müssen " +
        "und ähm der Peter soll die Zahlen nochmal prüfen nein nicht der Peter die Anna soll die Zahlen prüfen " +
        "und dann schicken wir das am Freitag raus also spätestens Freitag Mittag";

    private static (TextRewriter Rewriter, BuiltInLanguageModel Model, string Directory) Make(string path)
    {
        var directory = Path.Combine(Path.GetTempPath(), "diktat-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var store = new AIProviderStore(Path.Combine(directory, "p.json"), new InMemorySecretStore());
        var model = new BuiltInLanguageModel(() => new LocalModelChoice("Gemma 4 E2B", path), new LocalModelOptions(ContextSize: 8192));
        return (new TextRewriter(store, new AIClient(), model), model, directory);
    }

    [Fact]
    public async Task CleanupStrongAndWtf()
    {
        if (ModelPath is not { } path) return;
        var (rewriter, model, directory) = Make(path);
        using var _ = model;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var strong = await new SpeechPolisher(rewriter) { Log = m => Console.WriteLine("LOG " + m) }
                .PolishAsync(Rambling, new SpeechPolishConfiguration { Level = SpeechCleanupLevel.Strong, ProtectedTerms = ["Meier"] });
            Console.WriteLine($"STARK ({clock.Elapsed.TotalSeconds:0.0} s incl. load):\n{strong}\n");
            Assert.NotEqual(Rambling, strong);
            Assert.DoesNotContain("ähm", strong);
            Assert.Contains("Anna", strong);

            clock.Restart();
            var wtf = await new ModeRewriter(rewriter).DampfAblassenAsync(
                "das ist doch echt unglaublich jetzt warte ich schon seit drei wochen auf die rechnung und keiner meldet sich das ist so ein saftladen",
                new DampfAblassenSettings(), "auto", RewriteEngine.Automatic);
            Console.WriteLine($"WTF ({clock.Elapsed.TotalSeconds:0.0} s):\n{wtf}\n");
            Assert.DoesNotContain("Saftladen", wtf, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
