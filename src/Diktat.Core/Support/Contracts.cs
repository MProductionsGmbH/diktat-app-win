namespace Diktat.Core.Support;

// Small interfaces the platform-neutral code needs from the operating system. The Windows app
// implements them (DPAPI, Windows spell checker); tests use in-memory fakes.

/// Secrets such as API keys, one value per name. On Windows: encrypted with DPAPI for the
/// current user, never in plain text on disk.
public interface ISecretStore
{
    string? Get(string name);
    /// An empty or whitespace value deletes the entry.
    void Set(string name, string? value);
    void Delete(string name);
}

/// Encrypts data at rest for the current user (history, meetings). On Windows: DPAPI.
public interface IDataProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

/// Tells whether a single word is a correctly spelled dictionary word in a language
/// ("de", "en"). Used so the fuzzy custom-term correction never replaces real words.
public interface IDictionaryWordChecker
{
    bool IsDictionaryWord(string word, string languageCode);
}

/// In-memory implementations for tests and for platforms without a secure store.
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) _values.Remove(name);
        else _values[name] = value.Trim();
    }

    public void Delete(string name) => _values.Remove(name);
}

public sealed class PassThroughDataProtector : IDataProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] protectedData) => protectedData;
}
