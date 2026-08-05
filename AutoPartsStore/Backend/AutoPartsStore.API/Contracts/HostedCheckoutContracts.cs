using System.ComponentModel.DataAnnotations;

namespace AutoPartsStore.API.Contracts;

/// <summary>
/// Public hosted-payment request. Card number, expiry and security code are
/// intentionally absent: those values may only be collected by the provider.
/// </summary>
public sealed class CreateHostedCheckoutDto
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string FirstName { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 1)]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required, Phone, StringLength(20)]
    public string Phone { get; set; } = string.Empty;

    // Some Turkish payment providers require this during initialization. The
    // coordinator passes it in memory and never persists it in an entity.
    [Required, StringLength(32, MinimumLength = 5)]
    public string IdentityNumber { get; set; } = string.Empty;

    [Required, StringLength(500, MinimumLength = 10)]
    public string ShippingAddress { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 1)]
    public string City { get; set; } = string.Empty;

    [Required, StringLength(10, MinimumLength = 1)]
    public string PostalCode { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(100)]
    public List<OrderItemDto> Items { get; set; } = new();

    [Required, MinLength(1), MaxLength(10)]
    public List<LegalAcceptanceDto> LegalAcceptances { get; set; } = new();

    public override string ToString() => $"{nameof(CreateHostedCheckoutDto)} {{ Sensitive = true }}";
}

public sealed class HostedCheckoutResponseDto
{
    public string Outcome { get; set; } = string.Empty;
    public bool Replayed { get; set; }
    public int? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string? OrderStatus { get; set; }
    public string? PaymentStatus { get; set; }
    public string? AttemptStatus { get; set; }
    public Uri? RedirectUri { get; set; }
    public string? Message { get; set; }
}

public sealed class HostedCheckoutEndpointOptions
{
    public string? CallbackUri { get; init; }
    public string? ReturnUri { get; init; }

    public bool TryGetTrustedUris(out Uri callbackUri, out Uri returnUri)
    {
        var callbackValid = TryGetHttpsUri(CallbackUri, out callbackUri);
        var returnValid = TryGetHttpsUri(ReturnUri, out returnUri);
        return callbackValid && returnValid;
    }

    private static bool TryGetHttpsUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }
}
