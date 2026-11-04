using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.Core.Text;

/// Local cleanup of a raw transcript before it is pasted or handed to a language model.
///
/// Order matters: hesitation sounds go first so they can never be read as part of a term, then
/// the user's fixed replacements, then dictated e-mail addresses are written as addresses, then
/// words that clearly mean one of the user's custom terms get the user's spelling, and finally
/// stutters and stray punctuation are tidied (this last step is the rule-based "Leicht" cleanup
/// level). Everything runs in plain string code and only touches the words it changes; spacing
/// and line breaks elsewhere stay exactly as dictated.
public sealed class TranscriptPostProcessor
{
    public IReadOnlyList<string> CustomTerms { get; set; } = [];
    public IReadOnlyList<TermReplacement> Replacements { get; set; } = [];
    public bool CorrectCustomTerms { get; set; } = true;
    public bool RemoveFillerWords { get; set; } = true;
    /// A language code, or "auto" to follow the language of each text.
    public string Language { get; set; } = "auto";
    /// Minimum <see cref="TermSimilarity"/> for a fuzzy term correction. On that scale the
    /// misheard "Clawd" scores 0.82 against "Claude", while real words a dictionary might not
    /// know stay below 0.67 ("glaube" and "Klause" against "Claude", "Kopie" 0.58 against
    /// "Copper"); 0.74 sits in the middle of that gap. Exact matches always apply.
    public double CorrectionThreshold { get; set; } = DefaultCorrectionThreshold;
    /// Returns true when a single word is a correctly spelled dictionary word.
    public Func<string, bool> IsDictionaryWord { get; set; } = _ => false;

    public const double DefaultCorrectionThreshold = 0.74;

    /// The processor the app uses for a dictation, configured from the settings.
    public static TranscriptPostProcessor FromSettings(SettingsContainer settings, IDictionaryWordChecker? checker = null) => new()
    {
        CustomTerms = settings.TextImprovement.CustomTerms,
        Replacements = settings.TextImprovement.Replacements,
        CorrectCustomTerms = settings.App.CorrectCustomTermsEnabled,
        RemoveFillerWords = settings.App.RemoveFillerWordsEnabled,
        Language = settings.Transcription.Language,
        IsDictionaryWord = DictionaryWords.From(checker),
    };

    public string Process(string text)
    {
        text = text.Normalize(NormalizationForm.FormC);
        var result = text;
        if (RemoveFillerWords)
        {
            var language = Language == "auto" ? PromptLanguages.Detect(result).Code() : Language;
            result = RemoveFillers(result, language);
        }
        // Fixed rules first, so the fuzzy matcher sees the intended spelling.
        result = ApplyReplacements(Replacements, result);
        // Addresses before the fuzzy matcher, which then leaves them alone: an address is
        // exact, a similar-sounding term would send the mail somewhere else.
        result = SpokenEmailAddress.Format(result);
        if (CorrectCustomTerms)
        {
            result = ApplyCustomTerms(result, CustomTerms, CorrectionThreshold, IsDictionaryWord);
        }
        return Normalize(result, text);
    }

    // MARK: - Replacements

    /// Case-insensitive, whole words only; spaces and hyphens between the spoken words may vary
    /// ("n acht n", "N-acht-N"). Longer rules win over shorter overlapping ones.
    public static string ApplyReplacements(IEnumerable<TermReplacement> replacements, string text)
    {
        var rules = replacements
            .Select(r => (Spoken: (r.Spoken ?? "").Trim(), Written: r.Written ?? ""))
            .Where(r => r.Spoken.Length > 0)
            .OrderByDescending(r => TextScan.CharacterCount(r.Spoken))
            .ToList();
        if (rules.Count == 0) return text;

        var result = text;
        foreach (var rule in rules)
        {
            var words = rule.Spoken
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(w => w.Split('-', StringSplitOptions.RemoveEmptyEntries))
                .Select(Regex.Escape)
                .ToList();
            if (words.Count == 0) continue;
            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[\s\-]+", words) + @"(?![\p{L}\p{N}])";
            var written = rule.Written;
            result = Regex.Replace(result, pattern, _ => written, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return result;
    }

    // MARK: - Custom terms

    /// Recognizers split names ("Hub Spot"), spell digits out ("n acht n") and write what they
    /// hear ("Clawd"). Up to three neighbouring words are tried as one candidate.
    private const int MaxWordsPerCandidate = 3;
    /// With fewer letters one changed sound already makes another word ("Jams" vs. "Jamf"), so
    /// shorter words and terms are only corrected when the letters are the same.
    private const int MinimumFuzzyLength = 5;

    /// Gives every word or group of words that clearly means a custom term the term's spelling.
    /// Candidates are scored with <see cref="TermSimilarity"/>; the best ones win, and on a tie
    /// the longer span. A fuzzy match never changes real dictionary words, so "Cloud" stays
    /// "Cloud" next to the term "Claude". Punctuation and whitespace outside the replaced words
    /// stay untouched.
    public static string ApplyCustomTerms(string text, IEnumerable<string> customTerms, double threshold, Func<string, bool>? isDictionaryWord = null)
    {
        isDictionaryWord ??= _ => false;
        var matcher = new TermMatcher(customTerms);
        if (matcher.IsEmpty) return text;
        var tokens = Tokenize(text);
        // E-mail addresses get no key, so no term ever replaces one.
        var keys = tokens.Select(t => text.AsSpan(t.CoreStart, t.CoreLength).Contains('@') ? Array.Empty<int>() : MatchKey(text.AsSpan(t.CoreStart, t.CoreLength))).ToArray();

        var candidates = new List<TermCandidate>();
        var key = new List<int>();
        for (var first = 0; first < tokens.Count; first++)
        {
            if (keys[first].Length == 0) continue;
            key.Clear();
            for (var last = first; last < Math.Min(first + MaxWordsPerCandidate, tokens.Count); last++)
            {
                if (last > first)
                {
                    // Punctuation or a line break between two words keeps them apart.
                    var previous = tokens[last - 1];
                    var current = tokens[last];
                    if (previous.HasTrailingPunctuation || current.HasLeadingPunctuation || current.FollowsLineBreak || keys[last].Length == 0) break;
                }
                key.AddRange(keys[last]);
                var match = matcher.BestMatch(key.ToArray(), last - first + 1, threshold);
                if (match is not { } m || !(m.IsExact || m.Score >= threshold)) continue;
                candidates.Add(new TermCandidate(first, last, m));
            }
        }
        if (candidates.Count == 0) return text;

        candidates.Sort((lhs, rhs) =>
        {
            if (lhs.Match.Score != rhs.Match.Score) return rhs.Match.Score.CompareTo(lhs.Match.Score);
            if (lhs.WordCount != rhs.WordCount) return rhs.WordCount.CompareTo(lhs.WordCount);
            return lhs.First.CompareTo(rhs.First);
        });

        var claimed = new bool[tokens.Count];
        var knownWords = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsKnown(string word)
        {
            if (knownWords.TryGetValue(word, out var known)) return known;
            known = isDictionaryWord(word);
            knownWords[word] = known;
            return known;
        }

        var edits = new List<(int Start, int End, string Term)>();
        foreach (var candidate in candidates)
        {
            var anyClaimed = false;
            for (var i = candidate.First; i <= candidate.Last; i++) anyClaimed |= claimed[i];
            if (anyClaimed) continue;
            // Every candidate taken here claims its words, even when it changes nothing (the
            // term is already right, or the words are real ones), so no weaker candidate around
            // it can swallow them ("Hab Spot an" after "Hab Spot" was kept).
            for (var i = candidate.First; i <= candidate.Last; i++) claimed[i] = true;
            // Fuzzy matches only change unknown words. Several words count as known when
            // written as one they are a word ("Black Box") or when each of them is ("über den");
            // same letters as the term ("Hub Spot") still join, being exact.
            if (!candidate.Match.IsExact)
            {
                var words = new List<string>();
                for (var i = candidate.First; i <= candidate.Last; i++) words.Add(text.Substring(tokens[i].CoreStart, tokens[i].CoreLength));
                if (IsKnown(DictionaryForm(words)) || words.All(IsKnown)) continue;
            }
            var start = tokens[candidate.First].CoreStart;
            var end = tokens[candidate.Last].CoreEnd;
            var term = matcher.Terms[candidate.Match.Term];
            if (!text.AsSpan(start, end - start).SequenceEqual(term))
            {
                edits.Add((start, end, term));
            }
        }
        if (edits.Count == 0) return text;

        edits.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new StringBuilder(text.Length + 16);
        var position = 0;
        foreach (var edit in edits)
        {
            result.Append(text, position, edit.Start - position);
            result.Append(edit.Term);
            position = edit.End;
        }
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    /// How surely <paramref name="candidate"/> (one or more words) means <paramref name="term"/>,
    /// from 0 to 1.
    ///
    /// 1 means the same letters once case, umlaut spelling (ä/ae, ß/ss), spaces and punctuation
    /// are ignored; digits in the term may also be spoken in German or English ("n acht n",
    /// "n eight n" for "n8n"). Otherwise the score is the geometric mean of two similarities,
    /// each 1 - edits / longer length: spelling (Damerau-Levenshtein distance of the letters)
    /// and sound (the same distance of the phonetic codes). The geometric mean needs both to
    /// agree: "Kobber" sounds like "Copper" (sound 1.0) but half its letters differ (spelling
    /// 0.5), so it scores 0.71 instead of an average of 0.75.
    ///
    /// The score is 0 when fuzzy matching makes no sense: fewer than five letters, digits, a
    /// different first sound, the term plus an ending ("Magnussens") or only the start of the
    /// term ("Magnus"; one missing letter as in "Claud" is still allowed). Several words must
    /// also start with the term's first letter: a word placed in front ("in Hubspott") does not.
    public static double TermSimilarity(string candidate, string term)
    {
        var words = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return new TermMatcher([term]).BestMatch(MatchKey(candidate), words, 0)?.Score ?? 0;
    }

    /// Similarity of the plain letters, 1 for "Müller" and "MUELLER".
    public static double SpellingSimilarity(string lhs, string rhs) => Similarity(MatchKey(lhs), MatchKey(rhs));

    /// Similarity of the phonetic codes, 1 for "Kopper" and "Copper".
    public static double SoundSimilarity(string lhs, string rhs) => Similarity(SoundCode(MatchKey(lhs)), SoundCode(MatchKey(rhs)));

    private static double Similarity(int[] lhs, int[] rhs)
    {
        var length = Math.Max(lhs.Length, rhs.Length);
        if (length == 0) return 1;
        return 1 - (double)EditDistance(lhs, rhs) / length;
    }

    private static readonly string[] GermanDigitNames = ["null", "eins", "zwei", "drei", "vier", "fuenf", "sechs", "sieben", "acht", "neun"];
    private static readonly string[] EnglishDigitNames = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    /// The custom terms prepared once per call: every spelling a term may be dictated in, a
    /// lookup for exact matches and the fuzzy spellings grouped by their first sound.
    private sealed class TermMatcher
    {
        private readonly record struct Spelling(int Term, int[] Key, int[] Code);

        public readonly record struct Match(int Term, double Score, bool IsExact);

        public List<string> Terms { get; } = [];
        private readonly Dictionary<int[], int> _exact = new(KeyComparer.Instance);
        private readonly Dictionary<int, List<Spelling>> _fuzzyByFirstSound = new();

        public bool IsEmpty => Terms.Count == 0;

        public TermMatcher(IEnumerable<string> rawTerms)
        {
            foreach (var raw in rawTerms)
            {
                var term = (raw ?? "").Trim().Normalize(NormalizationForm.FormC);
                var key = MatchKey(term);
                if (key.Length == 0 || Terms.Contains(term)) continue;
                var index = Terms.Count;
                Terms.Add(term);
                foreach (var spelling in SpokenForms(key))
                {
                    _exact.TryAdd(spelling, index);
                    if (!AllowsFuzzyMatch(spelling)) continue;
                    var code = SoundCode(spelling);
                    if (code.Length == 0) continue;
                    if (!_fuzzyByFirstSound.TryGetValue(code[0], out var list))
                    {
                        list = [];
                        _fuzzyByFirstSound[code[0]] = list;
                    }
                    list.Add(new Spelling(index, spelling, code));
                }
            }
        }

        /// The key itself plus, when it contains digits, the digits spelled out in German and in English.
        private static List<int[]> SpokenForms(int[] key)
        {
            if (!key.Any(IsAsciiDigit)) return [key];
            var forms = new List<int[]> { key };
            foreach (var names in new[] { GermanDigitNames, EnglishDigitNames })
            {
                var spelled = new List<int>();
                foreach (var value in key)
                {
                    if (IsAsciiDigit(value)) spelled.AddRange(names[value - 48].Select(c => (int)c));
                    else spelled.Add(value);
                }
                forms.Add(spelled.ToArray());
            }
            return forms;
        }

        /// The best term for a candidate key made of <paramref name="words"/> words. Fuzzy scores
        /// below <paramref name="minimumScore"/> may be cut short and come back as null, so pass 0
        /// to always get the true score.
        public Match? BestMatch(int[] key, int words, double minimumScore)
        {
            if (key.Length == 0) return null;
            if (_exact.TryGetValue(key, out var exactTerm)) return new Match(exactTerm, 1, true);
            if (!AllowsFuzzyMatch(key)) return null;
            var code = SoundCode(key);
            if (code.Length == 0 || !_fuzzyByFirstSound.TryGetValue(code[0], out var spellings)) return null;

            Match? best = null;
            var bestLength = 0;
            foreach (var spelling in spellings)
            {
                if (words != 1 && spelling.Key[0] != key[0]) continue;
                var score = FuzzyScore(key, code, spelling.Key, spelling.Code, minimumScore);
                if (score <= 0) continue;
                // Closest first; on a tie the longer term, then the one listed first.
                if (best is { } current && (score < current.Score || (score == current.Score && spelling.Key.Length <= bestLength))) continue;
                best = new Match(spelling.Term, score, false);
                bestLength = spelling.Key.Length;
            }
            return best;
        }
    }

    private readonly record struct TermCandidate(int First, int Last, TermMatcher.Match Match)
    {
        public int WordCount => Last - First + 1;
    }

    private sealed class KeyComparer : IEqualityComparer<int[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] key)
        {
            var hash = new HashCode();
            foreach (var value in key) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    private static bool IsAsciiDigit(int value) => value is >= 48 and <= 57;

    /// Digits are never fuzzy: "Web2" is not a misheard "Web3".
    private static bool AllowsFuzzyMatch(int[] key) => key.Length >= MinimumFuzzyLength && !key.Any(IsAsciiDigit);

    /// See <see cref="TermSimilarity"/>. Returns 0 as soon as the score cannot reach the minimum.
    private static double FuzzyScore(int[] candidate, int[] candidateCode, int[] term, int[] termCode, double minimumScore)
    {
        if (candidateCode.Length == 0 || termCode.Length == 0 || candidateCode[0] != termCode[0]) return 0;

        // score = sqrt(spelling * sound), so each part must reach minimumScore² on its own.
        // Cheap bounds come first: this runs for every candidate and term.
        var needed = Math.Max(minimumScore, 0) * Math.Max(minimumScore, 0);
        var length = Math.Max(candidate.Length, term.Length);
        if (Math.Abs(candidate.Length - term.Length) > AllowedEdits(length, needed)) return 0;

        var codeLength = Math.Max(candidateCode.Length, termCode.Length);
        var soundLimit = AllowedEdits(codeLength, needed);
        var soundEdits = EditDistance(candidateCode, termCode, soundLimit);
        if (soundEdits > soundLimit || soundEdits >= codeLength) return 0;
        var sound = 1 - (double)soundEdits / codeLength;

        var spellingLimit = AllowedEdits(length, needed / sound);
        var spellingEdits = EditDistance(candidate, term, spellingLimit);
        if (spellingEdits > spellingLimit || spellingEdits >= length) return 0;
        var spelling = 1 - (double)spellingEdits / length;

        if (candidate.Length > term.Length && candidate.AsSpan().StartsWith(term)) return 0;
        if (term.Length - candidate.Length > 1 && term.AsSpan().StartsWith(candidate)) return 0;
        return Math.Sqrt(spelling * sound);
    }

    /// Most edits a string of <paramref name="length"/> may have and still reach <paramref name="similarity"/>.
    private static int AllowedEdits(int length, double similarity) =>
        Math.Max((int)Math.Floor((1 - similarity) * length + 1e-9), 0);

    /// Several words written as one the way a dictionary expects ("Black Box" -> "Blackbox").
    private static string DictionaryForm(List<string> words) =>
        words.Count == 0 ? "" : words[0] + string.Concat(words.Skip(1).Select(w => w.ToLowerInvariant()));

    // MARK: - Spelling and sound

    /// Letters and digits only, lowercased, umlauts spelled out (ä -> ae, ß -> ss) and other
    /// accents dropped, so "Müller", "MUELLER" and "Mül-ler" compare equal.
    private static int[] MatchKey(ReadOnlySpan<char> text)
    {
        var key = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            switch (v)
            {
                case >= 'a' and <= 'z':
                case >= '0' and <= '9':
                    key.Add(v);
                    break;
                case >= 'A' and <= 'Z':
                    key.Add(v + 32);
                    break;
                case 'ä' or 'Ä':
                    key.Add(97); key.Add(101);
                    break;
                case 'ö' or 'Ö':
                    key.Add(111); key.Add(101);
                    break;
                case 'ü' or 'Ü':
                    key.Add(117); key.Add(101);
                    break;
                case 'ß' or 'ẞ':
                    key.Add(115); key.Add(115);
                    break;
                case 0x308 when key.Count > 0 && key[^1] is 97 or 111 or 117:
                    key.Add(101); // a decomposed umlaut, "u" followed by a combining diaeresis
                    break;
                default:
                    if (rune.IsAscii || !IsAlphabetic(rune)) break;
                    foreach (var folded in FoldDiacritics(rune)) key.Add(folded);
                    break;
            }
        }
        return key.ToArray();
    }

    private static bool IsAlphabetic(Rune rune) =>
        Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;

    /// "É" -> "e", "ø" -> "o": lowercased, accents dropped, letters only.
    private static IEnumerable<int> FoldDiacritics(Rune rune)
    {
        var lower = Rune.ToLowerInvariant(rune);
        switch (lower.Value)
        {
            case 'ø': return [(int)'o'];
            case 'ł': return [(int)'l'];
            case 'đ': return [(int)'d'];
            case 'ħ': return [(int)'h'];
            case 'ı': return [(int)'i'];
        }
        var decomposed = lower.ToString().Normalize(NormalizationForm.FormD);
        var result = new List<int>();
        foreach (var part in decomposed.EnumerateRunes())
        {
            if (IsAlphabetic(part)) result.Add(part.Value);
        }
        return result;
    }

    /// Cologne phonetics: a sound code built for German names, so "Meier" and "Mayr" or "Kopper"
    /// and "Copper" get the same code.
    public static string ColognePhonetics(string word) =>
        string.Concat(CologneCode(MatchKey(word)).Select(d => d.ToString(CultureInfo.InvariantCulture)));

    /// The code the matcher compares: Cologne phonetics, except that a "w" after a vowel and
    /// before a consonant or the end is read as part of the vowel, as English names ("Shaw",
    /// "Brown"), recognizer spellings like "Clawd" and northern German names ("Pankow") sound.
    private static int[] SoundCode(int[] key)
    {
        static bool IsVowel(int? letter) => letter is int l && LetterSet.Vowels.Contains(l);
        var letters = new List<int>(key.Length);
        for (var index = 0; index < key.Length; index++)
        {
            var letter = key[index];
            int? next = index + 1 < key.Length ? key[index + 1] : null;
            int? last = letters.Count > 0 ? letters[^1] : null;
            if (letter == 119 && IsVowel(last) && !IsVowel(next)) continue; // w
            letters.Add(letter);
        }
        return CologneCode(letters);
    }

    /// Letters map to digits by sound (vowels 0, b/p 1, d/t 2, f/v/w 3, g/k/q 4, l 5, m/n 6, r 7,
    /// s/z 8; c, d, t, p and x depend on their neighbours, h is silent). Repeated digits count
    /// once and vowels only at the start. Letters outside a-z and digits are skipped.
    private static int[] CologneCode(IReadOnlyList<int> key)
    {
        var letters = key.Where(v => v is >= 97 and <= 122).ToArray();
        var code = new List<int>(letters.Length);
        int? lastDigit = null;
        void Emit(int digit)
        {
            if (digit != lastDigit && (digit != 0 || code.Count == 0)) code.Add(digit);
            lastDigit = digit;
        }

        for (var index = 0; index < letters.Length; index++)
        {
            // 0 stands for "no letter" and is in none of the sets.
            var previous = index > 0 ? letters[index - 1] : 0;
            var next = index + 1 < letters.Length ? letters[index + 1] : 0;
            switch ((char)letters[index])
            {
                case 'a' or 'e' or 'i' or 'j' or 'o' or 'u' or 'y': Emit(0); break;
                case 'b': Emit(1); break;
                case 'p': Emit(next == 104 ? 3 : 1); break; // h
                case 'd' or 't': Emit(LetterSet.Csz.Contains(next) ? 8 : 2); break;
                case 'f' or 'v' or 'w': Emit(3); break;
                case 'g' or 'k' or 'q': Emit(4); break;
                case 'c':
                    var hard = index == 0
                        ? LetterSet.Ahkloqrux.Contains(next)
                        : !LetterSet.Sz.Contains(previous) && LetterSet.Ahkoqux.Contains(next);
                    Emit(hard ? 4 : 8);
                    break;
                case 'x':
                    if (!LetterSet.Ckq.Contains(previous)) Emit(4);
                    Emit(8);
                    break;
                case 'l': Emit(5); break;
                case 'm' or 'n': Emit(6); break;
                case 'r': Emit(7); break;
                case 's' or 'z': Emit(8); break;
                default: break; // h
            }
        }
        return code.ToArray();
    }

    /// The neighbour sets of the Cologne rules as bit masks over a-z, cheap to test.
    private readonly struct LetterSet
    {
        private readonly uint _mask;

        private LetterSet(string letters)
        {
            foreach (var c in letters) _mask |= 1u << (c - 97);
        }

        public bool Contains(int letter) => letter is >= 97 and <= 122 && ((_mask >> (letter - 97)) & 1) == 1;

        public static readonly LetterSet Vowels = new("aeiouy");
        public static readonly LetterSet Csz = new("csz");
        public static readonly LetterSet Sz = new("sz");
        public static readonly LetterSet Ckq = new("ckq");
        public static readonly LetterSet Ahkoqux = new("ahkoqux");
        public static readonly LetterSet Ahkloqrux = new("ahkloqrux");
    }

    /// Optimal string alignment distance (Damerau-Levenshtein): insertions, deletions,
    /// substitutions and swaps of two neighbouring characters cost 1 each.
    public static int EditDistance(string lhs, string rhs) =>
        EditDistance(lhs.EnumerateRunes().Select(r => r.Value).ToArray(), rhs.EnumerateRunes().Select(r => r.Value).ToArray());

    /// Stops early and returns limit + 1 once the distance is certain to exceed the limit.
    private static int EditDistance(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int limit = int.MaxValue)
    {
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;

        // Three rows of the table in one buffer: two rows up (for swaps), previous, current.
        var width = b.Length + 1;
        Span<int> rows = 3 * width <= 512 ? stackalloc int[3 * width] : new int[3 * width];
        int twoRowsUp = 0, previous = width, current = 2 * width;
        for (var j = 0; j < width; j++) rows[previous + j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            rows[current] = i;
            var rowMinimum = i;
            for (var j = 1; j < width; j++)
            {
                var substitution = rows[previous + j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var value = Math.Min(Math.Min(rows[previous + j] + 1, rows[current + j - 1] + 1), substitution);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, rows[twoRowsUp + j - 2] + 1);
                }
                rows[current + j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }
            if (rowMinimum > limit) return limit + 1;
            (twoRowsUp, previous, current) = (previous, current, twoRowsUp);
        }
        return rows[previous + b.Length];
    }

    // MARK: - Filler words

    /// Hesitation sounds that are no word in any language, removed whatever the language. German
    /// keeps "um" (um 10 Uhr), "eh" (das ist eh egal), "mhm" and "mh" (yes), "ah", "oh", "na"
    /// and "hä" (they carry meaning); "ähem" too, people write it on purpose.
    private static readonly HashSet<string> FillerSounds = new(StringComparer.Ordinal) { "äh", "ähm", "äm", "öh", "öhm", "ehm", "uhm", "hm" };

    /// Sounds that are fillers in one language but words elsewhere: "um" is a German preposition,
    /// "uh" an interjection in some languages. English "er" stays everywhere (a German pronoun,
    /// "ER" a hospital ward); "eh" stays as in "nice, eh?".
    private static readonly Dictionary<string, string[]> FillerWordsByLanguage = new(StringComparer.Ordinal)
    {
        ["en"] = ["uh", "um", "erm"],
    };

    /// Removes whole-word hesitation sounds, ignoring case and stretched letters ("Ähhm",
    /// "HMMM"), together with a comma right after them. <see cref="Normalize"/> tidies the rest.
    public static string RemoveFillers(string text, string language)
    {
        var lowered = (language ?? "").ToLowerInvariant();
        var codeLength = 0;
        while (codeLength < lowered.Length && char.IsLetter(lowered[codeLength])) codeLength++;
        var languageCode = lowered[..codeLength];
        var fillers = new HashSet<string>(FillerSounds, StringComparer.Ordinal);
        if (FillerWordsByLanguage.TryGetValue(languageCode, out var extra)) fillers.UnionWith(extra);

        var result = new StringBuilder(text.Length);
        var position = 0;
        var removedAny = false;
        foreach (var token in Tokenize(text))
        {
            if (!token.HasCore) continue;
            if (!TextScan.AllLetters(text, token.CoreStart, token.CoreEnd)) continue;
            var word = text.Substring(token.CoreStart, token.CoreLength).ToLowerInvariant();
            if (!fillers.Contains(WithoutRepeatedLetters(word))) continue;
            var end = token.CoreEnd;
            if (end < text.Length && text[end] == ',') end++;
            result.Append(text, position, token.CoreStart - position);
            position = end;
            removedAny = true;
        }
        if (!removedAny) return text;
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static string WithoutRepeatedLetters(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (builder.Length == 0 || builder[^1] != c) builder.Append(c);
        }
        return builder.ToString();
    }

    // MARK: - Normalization (the rule-based "Leicht" cleanup)

    /// "ich ich ich ich glaube" -> "ich glaube": three or more equal words in a row (case and
    /// punctuation ignored) are a stutter. The first one stays with its case, the last one hands
    /// over its punctuation. Two in a row are valid German ("das das"), and a sentence end in
    /// between means deliberate repetition ("Nein. Nein. Nein.").
    public static string CollapseStutters(string text)
    {
        var tokens = Tokenize(text);
        var words = tokens.Select(t => TextScan.LettersLowercased(text, t.CoreStart, t.CoreEnd)).ToArray();

        var result = new StringBuilder(text.Length);
        var position = 0;
        var collapsedAny = false;
        var first = 0;
        while (first < tokens.Count)
        {
            var last = first;
            while (words[first].Length > 0 && last + 1 < tokens.Count && words[last + 1] == words[first] && !EndsSentence(tokens[last], text))
            {
                last++;
            }
            if (last - first >= 2)
            {
                result.Append(text, position, tokens[first].CoreEnd - position);
                position = tokens[last].CoreEnd;
                collapsedAny = true;
            }
            first = last + 1;
        }
        if (!collapsedAny) return text;
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    /// "wir müssen wir müssen das klären" -> "wir müssen das klären": a group of two to four
    /// words said twice in a row (case and commas ignored) is a restart, not a statement. The
    /// first copy stays, the second copy hands over its punctuation. A sentence end between the
    /// copies means deliberate repetition, and numbers are left alone.
    public static string CollapseRepeatedPhrases(string text)
    {
        var result = text;
        // A phrase said three times collapses in two passes.
        for (var pass = 0; pass < 3; pass++)
        {
            var collapsed = CollapseRepeatedPhrasesOnce(result);
            if (collapsed == result) break;
            result = collapsed;
        }
        return result;
    }

    private static string CollapseRepeatedPhrasesOnce(string text)
    {
        var tokens = Tokenize(text);
        var words = tokens.Select(t => TextScan.LettersLowercased(text, t.CoreStart, t.CoreEnd)).ToArray();

        var result = new StringBuilder(text.Length);
        var position = 0;
        var index = 0;
        var collapsedAny = false;
        while (index < tokens.Count)
        {
            var collapsedHere = false;
            for (var length = 4; length >= 2; length--)
            {
                var end = index + 2 * length;
                if (end > tokens.Count) continue;
                var phrase = words.AsSpan(index, length);
                var hasEmpty = false;
                foreach (var w in phrase) hasEmpty |= w.Length == 0;
                if (hasEmpty || phrase.ToArray().Distinct(StringComparer.Ordinal).Count() <= 1) continue;
                if (!phrase.SequenceEqual(words.AsSpan(index + length, length))) continue;
                var sentenceEnd = false;
                for (var i = index; i < end - 1; i++) sentenceEnd |= EndsSentence(tokens[i], text);
                if (sentenceEnd) continue;

                var last = tokens[end - 1];
                var keepEnd = tokens[index + length - 1].CoreEnd;
                result.Append(text, position, keepEnd - position);
                result.Append(text, last.CoreEnd, last.End - last.CoreEnd);
                position = last.End;
                index = end;
                collapsedAny = true;
                collapsedHere = true;
                break;
            }
            if (!collapsedHere) index++;
        }
        if (!collapsedAny) return text;
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static bool EndsSentence(Token token, string text)
    {
        var trailing = text.AsSpan(token.CoreEnd, token.End - token.CoreEnd);
        // An ellipsis is a pause inside the sentence, which is where stutters happen.
        if (trailing.EndsWith("...") || trailing.EndsWith("…")) return false;
        return trailing.IndexOfAny(".!?") >= 0;
    }

    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.])", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedCommas = new(@",{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex LeadingPunctuation = new(@"^[,. \t]+", RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedSpaces = new(@"[ \t]{2,}", RegexOptions.CultureInvariant);

    /// Stutters and repeated phrases collapsed, orphaned punctuation and double spaces removed,
    /// and the first letter capitalized when the original started with a capital.
    public static string Normalize(string text, string capitalizeFirstLetterLike)
    {
        var result = CollapseRepeatedPhrases(CollapseStutters(text));
        // Remove orphaned punctuation left behind by filler removal (", , ok" -> ", ok").
        result = SpaceBeforePunctuation.Replace(result, "$1");
        result = RepeatedCommas.Replace(result, ",");
        result = LeadingPunctuation.Replace(result, "");
        result = RepeatedSpaces.Replace(result, " ");
        result = result.Trim();

        // An e-mail address at the start stays lowercase.
        var firstWordEnd = 0;
        while (firstWordEnd < result.Length && !char.IsWhiteSpace(result[firstWordEnd])) firstWordEnd++;
        var firstWordHasAt = result.AsSpan(0, firstWordEnd).Contains('@');
        if (FirstLetterIsUppercase(capitalizeFirstLetterLike) && !firstWordHasAt)
        {
            result = TextScan.UppercasingFirst(result);
        }
        return result;
    }

    private static bool FirstLetterIsUppercase(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune)) return Rune.IsUpper(rune);
        }
        return false;
    }

    // MARK: - Tokens

    /// Text between whitespace, with the word inside it (the core, first to last letter or digit)
    /// kept apart from the punctuation around it, so edits never move a comma, period or bracket.
    private readonly record struct Token(int Start, int End, int CoreStart, int CoreEnd, bool FollowsLineBreak)
    {
        public int CoreLength => CoreEnd - CoreStart;
        /// False when the token has no letter or digit, like a lone dash.
        public bool HasCore => CoreEnd > CoreStart;
        public bool HasLeadingPunctuation => CoreStart > Start;
        public bool HasTrailingPunctuation => CoreEnd < End;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        var sawLineBreak = false;
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c))
            {
                sawLineBreak |= TextScan.IsNewline(c);
                index++;
                continue;
            }
            var start = index;
            var coreStart = -1;
            var coreEnd = start;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                var rune = TextScan.RuneAt(text, index, out var length);
                // A combining mark belongs to the letter before it, as in a Swift Character.
                if (TextScan.IsLetterOrNumber(rune) || (coreStart >= 0 && coreEnd == index && TextScan.IsMark(rune)))
                {
                    if (coreStart < 0) coreStart = index;
                    coreEnd = index + length;
                }
                index += length;
            }
            tokens.Add(coreStart >= 0
                ? new Token(start, index, coreStart, coreEnd, sawLineBreak)
                : new Token(start, index, start, start, sawLineBreak));
            sawLineBreak = false;
        }
        return tokens;
    }
}

/// Builds the dictionary-word test the term correction uses from the platform spell checker.
public static class DictionaryWords
{
    /// True for single letters and for words the checker knows in German or English, like the
    /// macOS app, which asks the system spell checker for "de" and "en".
    public static Func<string, bool> From(IDictionaryWordChecker? checker, params string[] languages)
    {
        if (checker is null) return _ => false;
        var codes = languages.Length > 0 ? languages : new[] { "de", "en" };
        return word =>
        {
            if (TextScan.CharacterCount(word) <= 1) return true;
            foreach (var code in codes)
            {
                try
                {
                    if (checker.IsDictionaryWord(word, code)) return true;
                }
                catch
                {
                    // A missing dictionary must not break the dictation.
                }
            }
            return false;
        };
    }
}
