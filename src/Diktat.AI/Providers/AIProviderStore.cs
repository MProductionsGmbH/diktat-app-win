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
            lock (_lock)
            {
                _config = value;
                _loaded = true;
                Save(value);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// True when a provider is set up and has its key (or needs none).
    public bool IsConfigured => Config is { } config && (!config.Provider.RequiresApiKey() || Key(config.Provider) is not null);

    public string? Key(AIProvider provider)
    {
        var key = _secrets.Get(SecretName(provider));
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// Saves the key trimmed; an empty key deletes it.
    public void SaveKey(AIProvider provider, string? key)
    {
        var trimmed = key?.Trim() ?? "";
        _secrets.Delete(SecretName(provider));
        if (trimmed.Length > 0) _secrets.Set(SecretName(provider), trimmed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteKey(AIProvider provider)
    {
        _secrets.Delete(SecretName(provider));
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
