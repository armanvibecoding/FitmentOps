namespace AutoPartsStore.API.Models;

public sealed class CorsSettings
{
    public string[] AllowedOrigins { get; init; } = [];

    public IReadOnlyList<string> GetValidatedOrigins()
    {
        var normalized = AllowedOrigins
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Cors:AllowedOrigins must contain at least one trusted origin.");
        }

        foreach (var origin in normalized)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath != "/" ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins may contain only exact HTTPS origins or loopback HTTP origins.");
            }
        }

        return normalized;
    }
}
