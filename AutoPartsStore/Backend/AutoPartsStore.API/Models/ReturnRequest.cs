using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(RefundId), IsUnique = true)]
[Index(nameof(ExternalRefundRequestReference), IsUnique = true)]
[Index(nameof(ExternalRefundConfirmationReference), IsUnique = true)]
public sealed class ReturnRequest
{
    public long Id { get; set; }

    public int OrderId { get; set; }

    [JsonIgnore]
    public Order Order { get; set; } = null!;

    [JsonIgnore]
    public long? RefundId { get; set; }

    [JsonIgnore]
    public Refund? Refund { get; set; }

    [Required]
    [StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string Status { get; set; } = ReturnRequestStatuses.Requested;

    [StringLength(200)]
    [JsonIgnore]
    public string? ExternalRefundRequestReference { get; set; }

    [StringLength(200)]
    [JsonIgnore]
    public string? ExternalRefundConfirmationReference { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RefundedAt { get; set; }

    [ConcurrencyCheck]
    [JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public ICollection<ReturnItem> Items { get; } = new List<ReturnItem>();
}

public static class ReturnRequestStatuses
{
    public const string Requested = "Requested";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Received = "Received";
    public const string Inspected = "Inspected";
    public const string RefundPending = "RefundPending";
    public const string Refunded = "Refunded";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
}

public static class ReturnReasonCodes
{
    public const string Defective = "defective";
    public const string DamagedInTransit = "damaged-in-transit";
    public const string WrongItem = "wrong-item";
    public const string Incompatible = "incompatible";
    public const string NotAsDescribed = "not-as-described";
    public const string UnopenedWithdrawal = "unopened-withdrawal";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(
        [
            Defective,
            DamagedInTransit,
            WrongItem,
            Incompatible,
            NotAsDescribed,
            UnopenedWithdrawal
        ],
        StringComparer.Ordinal);
}
