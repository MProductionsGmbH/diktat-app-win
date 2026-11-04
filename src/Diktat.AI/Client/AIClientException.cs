namespace Diktat.AI;

public enum AIClientErrorKind
{
    NotConfigured,
    MissingKey,
    InvalidAddress,
    Http,
    Refused,
    EmptyResponse,
    /// The provider did not answer in time (60 s, 120 s for local servers and Anthropic).
    TimedOut,
    /// No connection to the address, e.g. Ollama is not running.
    Unreachable,
}

/// A failed model request. <see cref="Exception.Message"/> is the German text for the user.
public sealed class AIClientException : Exception
{
    public AIClientErrorKind Kind { get; }
    public AIProvider? Provider { get; }
    /// The HTTP status for <see cref="AIClientErrorKind.Http"/>.
    public int? Status { get; }
    /// The provider's own error text, if it sent one.
    public string? ProviderMessage { get; }

    private AIClientException(AIClientErrorKind kind, string message, AIProvider? provider = null, int? status = null,
        string? providerMessage = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Provider = provider;
        Status = status;
        ProviderMessage = providerMessage;
    }

    public static AIClientException NotConfigured() => new(AIClientErrorKind.NotConfigured,
        "Kein Modell-Anbieter eingerichtet. Bitte in den Einstellungen einen API-Key hinterlegen.");

    public static AIClientException MissingKey(AIProvider provider) => new(AIClientErrorKind.MissingKey,
        $"Für {provider.DisplayName()} fehlt der API-Key.", provider);

    public static AIClientException InvalidAddress() => new(AIClientErrorKind.InvalidAddress,
        "Die Adresse des Modell-Anbieters ist ungültig.");

    public static AIClientException Http(int status, string? message) => new(AIClientErrorKind.Http, HttpMessage(status, message),
        status: status, providerMessage: message);

    public static AIClientException Refused() => new(AIClientErrorKind.Refused,
        "Der Modell-Anbieter hat die Anfrage abgelehnt.");

    public static AIClientException EmptyResponse() => new(AIClientErrorKind.EmptyResponse,
        "Keine Antwort vom Modell-Anbieter erhalten. Bitte nochmal versuchen.");

    // The two below stand in for URLSession's own errors on the Mac, in the system's German wording.
    public static AIClientException TimedOut(Exception? inner = null) => new(AIClientErrorKind.TimedOut,
        "Zeitüberschreitung bei der Anforderung.", inner: inner);

    public static AIClientException Unreachable(Exception? inner = null) => new(AIClientErrorKind.Unreachable,
        "Es konnte keine Verbindung zum Server hergestellt werden.", inner: inner);

    private static string HttpMessage(int status, string? message) => status switch
    {
        401 or 403 => "Der API-Key wurde abgelehnt. Bitte in den Einstellungen prüfen.",
        429 => "Zu viele Anfragen oder das Guthaben beim Anbieter ist aufgebraucht.",
        _ => $"Fehler vom Modell-Anbieter ({status})" + (message is null ? "." : ": " + message),
    };
}
