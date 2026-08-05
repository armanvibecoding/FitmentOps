using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class BulkQuoteStatuses
{
    public const string Submitted = "Submitted";
    public const string UnderReview = "UnderReview";
    public const string Quoted = "Quoted";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
}

public static class BulkQuoteLineStatuses
{
    public const string Unmatched = "Unmatched";
    public const string Matched = "Matched";
    public const string Quoted = "Quoted";
    public const string Unavailable = "Unavailable";
}

[Index(nameof(RequestNumber), IsUnique = true)]
[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(UserId), nameof(Status), nameof(CreatedAtUtc))]
public sealed class BulkQuoteRequest
{
    public long Id { get; set; }

    [Required, StringLength(40)]
    public string RequestNumber { get; set; } = string.Empty;

    public int UserId { get; set; }

    [JsonIgnore]
    public User User { get; set; } = null!;

    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [Required, StringLength(20)]
    public string Status { get; set; } = BulkQuoteStatuses.Submitted;

    [Required, StringLength(100), JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64), JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? QuotedAtUtc { get; set; }
    public DateTime? QuoteValidUntilUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public ICollection<BulkQuoteLine> Lines { get; set; } = new List<BulkQuoteLine>();
}

[Index(nameof(BulkQuoteRequestId), nameof(LineNumber), IsUnique = true)]
public sealed class BulkQuoteLine
{
    public long Id { get; set; }
    public long BulkQuoteRequestId { get; set; }

    [JsonIgnore]
    public BulkQuoteRequest BulkQuoteRequest { get; set; } = null!;

    public int LineNumber { get; set; }

    [Required, StringLength(80, MinimumLength = 1)]
    public string RequestedIdentifier { get; set; } = string.Empty;

    [Required, StringLength(80, MinimumLength = 1)]
    public string NormalizedIdentifier { get; set; } = string.Empty;

    public int RequestedQuantity { get; set; }
    public int? ProductId { get; set; }

    [JsonIgnore]
    public Product? Product { get; set; }

    [Required, StringLength(20)]
    public string Status { get; set; } = BulkQuoteLineStatuses.Unmatched;

    public decimal? QuotedUnitPrice { get; set; }
    public int? AvailableQuantity { get; set; }
    public int? LeadTimeDays { get; set; }
}
