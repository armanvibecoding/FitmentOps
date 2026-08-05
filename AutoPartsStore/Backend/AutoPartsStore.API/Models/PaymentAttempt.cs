using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(Provider), nameof(ConversationId), IsUnique = true)]
public sealed class PaymentAttempt
{
    public long Id { get; set; }

    public int PaymentId { get; set; }

    [JsonIgnore]
    public Payment Payment { get; set; } = null!;

    [Required]
    [StringLength(50)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Merchant-generated key that makes checkout initialization safe to retry.
    /// It must never be returned from an API response.
    /// </summary>
    [Required]
    [StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Correlation value sent to the provider. The provider and conversation ID
    /// pair identifies one provider-side initialization attempt.
    /// </summary>
    [Required]
    [StringLength(200)]
    [JsonIgnore]
    public string ConversationId { get; set; } = string.Empty;

    [StringLength(200)]
    [JsonIgnore]
    public string? ProviderPaymentId { get; set; }

    /// <summary>
    /// Opaque token for the provider-hosted payment page. Card numbers and CVV
    /// are intentionally not represented anywhere in this model.
    /// </summary>
    [StringLength(500)]
    [JsonIgnore]
    public string? HostedPaymentToken { get; set; }

    [StringLength(2048)]
    [Url]
    public string? RedirectUrl { get; set; }

    [Required]
    [StringLength(40)]
    public string Status { get; set; } = PaymentAttemptStatuses.Created;

    [StringLength(100)]
    public string? ProviderResultCode { get; set; }

    [StringLength(100)]
    public string? FailureCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class PaymentAttemptStatuses
{
    public const string Created = "Created";
    public const string RequiresCustomerAction = "RequiresCustomerAction";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Unknown = "Unknown";
}
