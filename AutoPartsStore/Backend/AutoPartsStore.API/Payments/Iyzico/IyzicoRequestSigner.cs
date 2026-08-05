using System.Security.Cryptography;
using System.Text;

namespace AutoPartsStore.API.Payments.Iyzico;

/// <summary>
/// Creates iyzico IYZWSv2 request authentication without parsing or reserializing
/// the body. The bytes supplied here must be the exact bytes sent over HTTP.
/// </summary>
public static class IyzicoRequestSigner
{
    private const int RandomKeyByteLength = 32;

    public static IyzicoSignedRequestHeaders SignWithGeneratedRandomKey(
        string apiKey,
        string secretKey,
        string uriPath,
        ReadOnlySpan<byte> exactJsonBodyUtf8)
    {
        return Sign(apiKey, secretKey, uriPath, exactJsonBodyUtf8, GenerateRandomKey());
    }

    public static IyzicoSignedRequestHeaders Sign(
        string apiKey,
        string secretKey,
        string uriPath,
        ReadOnlySpan<byte> exactJsonBodyUtf8,
        string randomKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(randomKey);
        ValidateUriPath(uriPath);

        var signatureBytes = ComputeRequestSignature(
            secretKey,
            randomKey,
            uriPath,
            exactJsonBodyUtf8);

        try
        {
            var signatureHex = Convert.ToHexString(signatureBytes).ToLowerInvariant();
            var authorizationBytes = Encoding.UTF8.GetBytes(
                $"apiKey:{apiKey}&randomKey:{randomKey}&signature:{signatureHex}");

            try
            {
                var authorization = $"IYZWSv2 {Convert.ToBase64String(authorizationBytes)}";
                return new IyzicoSignedRequestHeaders(authorization, randomKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authorizationBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signatureBytes);
        }
    }

    public static string GenerateRandomKey()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(RandomKeyByteLength);

        try
        {
            return Convert.ToHexString(randomBytes).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
        }
    }

    private static byte[] ComputeRequestSignature(
        string secretKey,
        string randomKey,
        string uriPath,
        ReadOnlySpan<byte> exactJsonBodyUtf8)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secretKey);

        try
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secretBytes);
            AppendUtf8(hash, randomKey);
            AppendUtf8(hash, uriPath);
            hash.AppendData(exactJsonBodyUtf8);
            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        try
        {
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateUriPath(string uriPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriPath);

        if (!uriPath.StartsWith("/", StringComparison.Ordinal) ||
            uriPath.StartsWith("//", StringComparison.Ordinal) ||
            uriPath.Contains('?') ||
            uriPath.Contains('#') ||
            uriPath.Contains('\\') ||
            uriPath.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException(
                "The iyzico signing input must be an absolute URI path without query or fragment.",
                nameof(uriPath));
        }
    }
}
