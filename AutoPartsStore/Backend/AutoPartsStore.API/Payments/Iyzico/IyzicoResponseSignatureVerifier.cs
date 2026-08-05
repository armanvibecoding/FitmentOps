using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AutoPartsStore.API.Payments.Iyzico;

/// <summary>
/// Verifies iyzico Checkout Form response signatures using the endpoint-specific
/// field order documented by iyzico.
/// </summary>
public static class IyzicoResponseSignatureVerifier
{
    private const int Sha256HexLength = 64;

    public static bool VerifyCheckoutFormInitialization(
        string secretKey,
        string? providedSignatureHex,
        string? conversationId,
        string? token)
    {
        ValidateSecret(secretKey);

        if (!HasValue(conversationId) || !HasValue(token))
        {
            return false;
        }

        return Verify(
            secretKey,
            providedSignatureHex,
            conversationId!,
            token!);
    }

    public static bool VerifyCheckoutFormRetrieve(
        string secretKey,
        string? providedSignatureHex,
        string? paymentStatus,
        string? paymentId,
        string? currency,
        string? basketId,
        string? conversationId,
        decimal paidPrice,
        decimal price,
        string? token)
    {
        ValidateSecret(secretKey);

        if (!HasValue(paymentStatus) ||
            !HasValue(paymentId) ||
            !HasValue(currency) ||
            !HasValue(basketId) ||
            !HasValue(conversationId) ||
            !HasValue(token))
        {
            return false;
        }

        return Verify(
            secretKey,
            providedSignatureHex,
            paymentStatus!,
            paymentId!,
            currency!,
            basketId!,
            conversationId!,
            NormalizeDecimal(paidPrice),
            NormalizeDecimal(price),
            token!);
    }

    private static bool Verify(
        string secretKey,
        string? providedSignatureHex,
        params string[] orderedFields)
    {
        if (!TryDecodeLowercaseSha256(providedSignatureHex, out var providedSignature))
        {
            return false;
        }

        var expectedSignature = ComputeSignature(secretKey, orderedFields);

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                expectedSignature,
                providedSignature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSignature);
            CryptographicOperations.ZeroMemory(providedSignature);
        }
    }

    private static byte[] ComputeSignature(string secretKey, IReadOnlyList<string> orderedFields)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secretKey);

        try
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secretBytes);

            for (var index = 0; index < orderedFields.Count; index++)
            {
                if (index > 0)
                {
                    hash.AppendData(":"u8);
                }

                AppendUtf8(hash, orderedFields[index]);
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static bool TryDecodeLowercaseSha256(
        string? signatureHex,
        out byte[] signature)
    {
        signature = [];

        if (signatureHex is null || signatureHex.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in signatureHex)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        signature = Convert.FromHexString(signatureHex);
        return true;
    }

    private static string NormalizeDecimal(decimal value)
    {
        return value.ToString("0.############################", CultureInfo.InvariantCulture);
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

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static void ValidateSecret(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
    }
}
