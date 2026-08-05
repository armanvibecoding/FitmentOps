using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class SalesChannelCodes
{
    public const string Trendyol = "Trendyol";
    public const string Hepsiburada = "Hepsiburada";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Trendyol, Hepsiburada],
        StringComparer.Ordinal);
}

public static class SalesChannelModes
{
    public const string Disabled = "Disabled";
    public const string Sandbox = "Sandbox";
    public const string Production = "Production";
}

public static class ChannelListingStatuses
{
    public const string Blocked = "Blocked";
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Error = "Error";
}

public static class ChannelInboxStatuses
{
    public const string Processed = "Processed";
    public const string Failed = "Failed";
}

[Index(nameof(Code), IsUnique = true)]
public sealed class SalesChannel
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string DisplayName { get; set; } = string.Empty;

    public bool RequestedEnabled { get; set; }

    [Required, StringLength(20)]
    public string Mode { get; set; } = SalesChannelModes.Disabled;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    [JsonIgnore]
    public ICollection<ChannelListing> Listings { get; set; } = new List<ChannelListing>();

    [JsonIgnore]
    public ICollection<ChannelOrderLink> Orders { get; set; } = new List<ChannelOrderLink>();

    [JsonIgnore]
    public ICollection<ChannelInboxEvent> InboxEvents { get; set; } = new List<ChannelInboxEvent>();
}

[Index(nameof(SalesChannelId), nameof(ProductId), IsUnique = true)]
[Index(nameof(SalesChannelId), nameof(ExternalListingId), IsUnique = true)]
public sealed class ChannelListing
{
    public long Id { get; set; }
    public int SalesChannelId { get; set; }

    [JsonIgnore]
    public SalesChannel SalesChannel { get; set; } = null!;

    public int ProductId { get; set; }

    [JsonIgnore]
    public Product Product { get; set; } = null!;

    [StringLength(100)]
    public string? ExternalListingId { get; set; }

    [Required, StringLength(20)]
    public string Status { get; set; } = ChannelListingStatuses.Blocked;

    public decimal DesiredPrice { get; set; }
    public int DesiredStock { get; set; }
    public decimal? ObservedPrice { get; set; }
    public int? ObservedStock { get; set; }
    public DateTime DesiredAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }

    [StringLength(100)]
    public string? LastFailureCode { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}

[Index(nameof(SalesChannelId), nameof(ExternalOrderId), IsUnique = true)]
[Index(nameof(OrderId), IsUnique = true)]
public sealed class ChannelOrderLink
{
    public long Id { get; set; }
    public int SalesChannelId { get; set; }

    [JsonIgnore]
    public SalesChannel SalesChannel { get; set; } = null!;

    [Required, StringLength(100), JsonIgnore]
    public string ExternalOrderId { get; set; } = string.Empty;

    public int OrderId { get; set; }

    [JsonIgnore]
    public Order Order { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; }

    [JsonIgnore]
    public ICollection<ChannelInboxEvent> InboxEvents { get; set; } = new List<ChannelInboxEvent>();
}

[Index(nameof(SalesChannelId), nameof(ExternalEventId), IsUnique = true)]
[Index(nameof(Status), nameof(ReceivedAtUtc))]
public sealed class ChannelInboxEvent
{
    public long Id { get; set; }
    public int SalesChannelId { get; set; }

    [JsonIgnore]
    public SalesChannel SalesChannel { get; set; } = null!;

    [Required, StringLength(100), JsonIgnore]
    public string ExternalEventId { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64), JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    public long? ChannelOrderLinkId { get; set; }

    [JsonIgnore]
    public ChannelOrderLink? ChannelOrderLink { get; set; }

    [Required, StringLength(20)]
    public string Status { get; set; } = ChannelInboxStatuses.Processed;

    [StringLength(100)]
    public string? FailureCode { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
