using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Diktat.Core.Support;

namespace Diktat.Windows.Platform;

/// <see cref="IDictionaryWordChecker"/> on the Windows Spell Checking API (ISpellCheckerFactory,
/// Windows 8+). "de" checks de-DE, de-CH and de-AT, "en" checks en-US and en-GB — whichever
/// dictionaries are installed. Unknown languages or a missing API answer false. Results are
/// cached. Thread-safe; the COM objects live on one private MTA thread.
public sealed class WindowsSpellChecker : IDictionaryWordChecker, IDisposable
{
    private static readonly Dictionary<string, string[]> LanguageTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = ["de-CH", "de-DE", "de-AT"],
        ["en"] = ["en-US", "en-GB"],
        ["fr"] = ["fr-FR", "fr-CH"],
        ["it"] = ["it-IT", "it-CH"],
    };

    private readonly ConcurrentDictionary<(string Language, string Word), bool> _cache = new();
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly Dictionary<string, List<ISpellChecker>> _checkers = new(StringComparer.OrdinalIgnoreCase);
    private ISpellCheckerFactory? _factory;
    private bool _factoryFailed;

    public WindowsSpellChecker()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "Diktat.SpellChecker" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool IsDictionaryWord(string word, string languageCode)
    {
        word = word.Trim();
        if (word.Length <= 1) return true; // as on the Mac: single letters are never "misheard names"
        var language = languageCode.Split('-', '_')[0];
        var key = (language.ToLowerInvariant(), word);
        if (_cache.TryGetValue(key, out var known)) return known;

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _work.Add(() =>
            {
                try { done.TrySetResult(CheckOnThread(word, language)); }
                catch (Exception ex) { Log.Warn("Spell check failed", ex); done.TrySetResult(false); }
            });
        }
        catch (InvalidOperationException)
        {
            return false; // disposed
        }
        bool result = done.Task.Wait(TimeSpan.FromSeconds(2)) && done.Task.Result;
        if (_cache.Count > 20_000) _cache.Clear();
        _cache[key] = result;
        return result;
    }

    private bool CheckOnThread(string word, string language)
    {
        var checkers = CheckersFor(language);
        if (checkers.Count == 0) return false;
        foreach (var checker in checkers)
        {
            var errors = checker.Check(word);
            try
            {
                if (errors.Next(out var error) != 0) return true; // S_FALSE: no error, a known word
                if (error is not null) Marshal.ReleaseComObject(error);
            }
            finally
            {
                Marshal.ReleaseComObject(errors);
            }
        }
        return false;
    }

    private List<ISpellChecker> CheckersFor(string language)
    {
        if (_checkers.TryGetValue(language, out var list)) return list;
        list = new List<ISpellChecker>();
        _checkers[language] = list;
        var factory = Factory();
        if (factory is null || !LanguageTags.TryGetValue(language, out var tags)) return list;
        foreach (var tag in tags)
        {
            try
            {
                if (factory.IsSupported(tag) != 0) list.Add(factory.CreateSpellChecker(tag));
            }
            catch (Exception ex)
            {
                Log.Warn($"Spell checker for {tag} unavailable", ex);
            }
        }
        if (list.Count == 0) Log.Info($"No Windows spell checker installed for '{language}'");
        return list;
    }

    private ISpellCheckerFactory? Factory()
    {
        if (_factory is not null || _factoryFailed) return _factory;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), throwOnError: true)!;
            _factory = (ISpellCheckerFactory)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            _factoryFailed = true;
            Log.Warn("Windows Spell Checking API unavailable", ex);
        }
        return _factory;
    }

    private void Pump()
    {
        foreach (var action in _work.GetConsumingEnumerable()) action();
        foreach (var checker in _checkers.Values.SelectMany(c => c)) Marshal.ReleaseComObject(checker);
        if (_factory is not null) Marshal.ReleaseComObject(_factory);
    }

    public void Dispose() => _work.CompleteAdding();

    // COM declarations (spellcheck.h). Methods in vtable order; only the used ones are called.

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        // Further methods (Suggest, Add, Ignore, AutoCorrect, ...) are not declared: never called.
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next(out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
    }
}
