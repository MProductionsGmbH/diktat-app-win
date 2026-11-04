using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diktat.Core.Settings;

/// JSON options shared by everything Diktat stores: enums as camelCase strings like the macOS
/// app ("tapTalk", "light"), unknown keys ignored, missing keys keep their defaults.
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = Create(writeIndented: true);

    public static JsonSerializerOptions Create(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new RewriteEngineConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }

    /// Settings of builds with the former Diktat server store "server"; it means the provider.
    private sealed class RewriteEngineConverter : JsonConverter<RewriteEngine>
    {
        public override RewriteEngine Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return raw is "provider" or "server" ? RewriteEngine.Provider : RewriteEngine.Automatic;
        }

        public override void Write(Utf8JsonWriter writer, RewriteEngine value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value == RewriteEngine.Provider ? "provider" : "automatic");
    }
}
