using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Payments.Iyzico;

/// <summary>
/// Holds the two correlated iyzico authentication header values. The values are
/// deliberately exposed through explicit read methods rather than serializable
/// properties so routine JSON serialization and structured logging do not leak them.
/// </summary>
public sealed record IyzicoSignedRequestHeaders
{
    public const string AuthorizationHeaderName = "Authorization";
    public const string RandomKeyHeaderName = "x-iyzi-rnd";

    [JsonIgnore]
    private readonly string _authorizationHeaderValue;

    [JsonIgnore]
    private readonly string _randomKeyHeaderValue;

    internal IyzicoSignedRequestHeaders(
        string authorizationHeaderValue,
        string randomKeyHeaderValue)
    {
        _authorizationHeaderValue = authorizationHeaderValue;
        _randomKeyHeaderValue = randomKeyHeaderValue;
    }

    public string ReadAuthorizationHeaderValue() => _authorizationHeaderValue;

    public string ReadRandomKeyHeaderValue() => _randomKeyHeaderValue;

    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Remove(AuthorizationHeaderName);
        request.Headers.Remove(RandomKeyHeaderName);
        request.Headers.TryAddWithoutValidation(AuthorizationHeaderName, _authorizationHeaderValue);
        request.Headers.TryAddWithoutValidation(RandomKeyHeaderName, _randomKeyHeaderValue);
    }

    public override string ToString() => $"{nameof(IyzicoSignedRequestHeaders)} {{ Redacted }}";
}
