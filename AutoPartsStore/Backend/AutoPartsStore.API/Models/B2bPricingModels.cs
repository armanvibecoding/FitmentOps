using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class DealerApplicationStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Suspended = "Suspended";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Pending, Approved, Rejected, Suspended],
        StringComparer.Ordinal);
}

[Index(nameof(UserId), IsUnique = true)]
[Index(nameof(IdempotencyKey), IsUnique = true)]
public sealed class DealerApplication
{
    public long Id { get; set; }
    public int UserId { get; set; }

    [JsonIgnore]
    public User User { get; set; } = null!;

    [Required, StringLength(160, MinimumLength = 2)]
    public string CompanyName { get; set; } = string.Empty;

    [Required, StringLength(32, MinimumLength = 5), JsonIgnore]
    public string TaxNumber { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 2), JsonIgnore]
    public string ContactName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200), JsonIgnore]
    public string ContactEmail { get; set; } = string.Empty;

    [Required, Phone, StringLength(20), JsonIgnore]
    public string ContactPhone { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string Status { get; set; } = DealerApplicationStatuses.Pending;

    public long? CustomerGroupId { get; set; }

    [JsonIgnore]
    public CustomerGroup? CustomerGroup { get; set; }

    [Required, StringLength(100), JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64), JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}

[Index(nameof(Code), IsUnique = true)]
public sealed class CustomerGroup
{
    public long Id { get; set; }

    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public int Priority { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    [JsonIgnore]
    public ICollection<PriceList> PriceLists { get; set; } = new List<PriceList>();
}

[Index(nameof(Code), IsUnique = true)]
public sealed class PriceList
{
    public long Id { get; set; }

    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public long CustomerGroupId { get; set; }

    [JsonIgnore]
    public CustomerGroup CustomerGroup { get; set; } = null!;

    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    public bool IsActive { get; set; } = true;
    public DateTime ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    [JsonIgnore]
    public ICollection<PriceRule> Rules { get; set; } = new List<PriceRule>();
}

[Index(nameof(PriceListId), nameof(Priority), nameof(ValidFromUtc))]
public sealed class PriceRule
{
    public long Id { get; set; }
    public long PriceListId { get; set; }

    [JsonIgnore]
    public PriceList PriceList { get; set; } = null!;

    public int? ProductId { get; set; }

    [JsonIgnore]
    public Product? Product { get; set; }

    public int? BrandId { get; set; }

    [JsonIgnore]
    public Brand? Brand { get; set; }

    public int? CategoryId { get; set; }

    [JsonIgnore]
    public Category? Category { get; set; }

    public int MinimumQuantity { get; set; } = 1;
    public decimal MinimumPeriodRevenue { get; set; }
    public int Priority { get; set; }
    public decimal? DiscountPercentage { get; set; }
    public decimal? FixedUnitPrice { get; set; }
    public DateTime ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }
    public bool IsActive { get; set; } = true;

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}
