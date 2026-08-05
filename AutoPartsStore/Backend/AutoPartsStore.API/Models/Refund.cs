using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(Provider), nameof(ProviderRefundId), IsUnique = true)]
public sealed class Refund
{
    public long Id { get; set; }

    public int PaymentId { get; set; }

    [JsonIgnore]
    public Payment Payment { get; set; } = null!;

    public long? PaymentTransactionId { get; set; }

    [JsonIgnore]
    public PaymentTransaction? PaymentTransaction { get; set; }

    [Required]
    [StringLength(50)]
    public string Provider { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string Status { get; set; } = RefundStatuses.Requested;

    [Range(0.01, double.MaxValue)]
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [StringLength(200)]
    [JsonIgnore]
    public string? ProviderRefundId { get; set; }

    [StringLength(100)]
    public string? FailureCode { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [ConcurrencyCheck]
    [JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}

public static class RefundStatuses
{
    public const string Requested = "Requested";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
}
