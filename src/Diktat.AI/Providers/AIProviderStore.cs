using System.Text.Json;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.AI;

/// The connected provider: the configuration as JSON in a file, the API keys in the secret
/// store, one per provider ("ai-provider/openAI").
public sealed class AIProviderStore
{
    private readonly string _configPath;
    private readonly ISecretStore _secrets;
    private readonly object _lock = new();
    private AIProviderConfig? _config;
    private bool _loaded;

    public AIProviderStore(string configPath, ISecretStore secrets)
    {
        _configPath = configPath;
        _secrets = secrets;
    }

    /// Raised after the configuration or a key changed.
    public event EventHandler? Changed;

    public static string SecretName(AIProvider provider) => "ai-provider/" + provider.RawValue();

    /// The connected provider, or null when none is set up.
    public AIProviderConfig? Config
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _config = Load();
                    _loaded = true;
                }
                return _config;
            }
        }
        set
        {
            // Read before the change: a key from an older version belongs to the address set up now.
            var keyScope = CustomKeyScope();
            lock (_lock)
            {
                _config = value;
                _loaded = true;
                Save(value);
            }
            // A key saved for one server of "Eigene Adresse" is not sent to another one, also not
            // after a detour over another provider or "Keiner".
            if (value is { Provider: AIProvider.Custom } && keyScope is not null && keyScope != value.KeyScope)
            {
                _secrets.Delete(SecretName(AIProvider.Custom));
                _secrets.Delete(CustomScopeName);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// True when a provider is set up and has its key (or needs none).
    public bool IsConfigured => Config is { } config && (!config.Provider.RequiresApiKey() || Key(config.Provider) is not null);

    /// The server (scheme, host and port) the key of "Eigene Adresse" was saved for.
    public const string CustomScopeName = "ai-provider/custom-server";

    /// The key of a provider. The key of "Eigene Adresse" only while "Eigene Adresse" points at
    /// the server it was saved for.
    public string? Key(AIProvider provider)
    {
        var key = _secrets.Get(SecretName(provider));
        if (string.IsNullOrEmpty(key)) return null;
        if (provider == AIProvider.Custom && Config is { Provider: AIProvider.Custom } config && CustomKeyScope() != config.KeyScope) return null;
        return key;
    }

    /// Stands for a key whose server is not known: it matches no address, so it is never sent.
    private const string UnknownScope = "unknown";

    /// The server the key of "Eigene Adresse" belongs to. A key from a version before this was
    /// stored belongs to the address set up now if "Eigene Adresse" is set up; otherwise its
    /// server is unknown and the key is dropped once "Eigene Adresse" is chosen again.
    private string? CustomKeyScope()
    {
        var scope = _secrets.Get(CustomScopeName);
        if (!string.IsNullOrEmpty(scope)) return scope;
        if (string.IsNullOrEmpty(_secrets.Get(SecretName(AIProvider.Custom)))) return null;
        if (Config is not { Provider: AIProvider.Custom, KeyScope: { } current }) return UnknownScope;
        _secrets.Set(CustomScopeName, current);
        return current;
    }

    /// Saves the key trimmed; an empty key deletes it. The key of "Eigene Adresse" is tied to
    /// the server set up right now.
    public void SaveKey(AIProvider provider, string? key)
    {
        var trimmed = key?.Trim() ?? "";
        _secrets.Delete(SecretName(provider));
        if (provider == AIProvider.Custom) _secrets.Delete(CustomScopeName);
        if (trimmed.Length > 0)
        {
            _secrets.Set(SecretName(provider), trimmed);
            if (provider == AIProvider.Custom && Config is { Provider: AIProvider.Custom, KeyScope: { } scope }) _secrets.Set(CustomScopeName, scope);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteKey(AIProvider provider)
    {
        _secrets.Delete(SecretName(provider));
        if (provider == AIProvider.Custom) _secrets.Delete(CustomScopeName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// "sk-…abcd" for display, never the full key.
    public string? MaskedKey(AIProvider provider) => Mask(Key(provider));

    public static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length <= 8) return "••••••••";
        return key[..3] + "…" + key[^4..];
    }

    private AIProviderConfig? Load()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var config = JsonSerializer.Deserialize<AIProviderConfig>(File.ReadAllText(_configPath), SettingsJson.Options);
            if (config is null) return null;
            // A file from an older build may lack a field; fall back to the provider's defaults.
            return config with
            {
                Model = string.IsNullOrWhiteSpace(config.Model) ? config.Provider.DefaultModel() : config.Model,
                BaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? config.Provider.DefaultBaseUrl() : config.BaseUrl,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Save(AIProviderConfig? config)
    {
        if (config is null)
        {
            if (File.Exists(_configPath)) File.Delete(_configPath);
            return;
        }
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = _configPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, SettingsJson.Options));
        File.Move(temporary, _configPath, overwrite: true);
    }
}
