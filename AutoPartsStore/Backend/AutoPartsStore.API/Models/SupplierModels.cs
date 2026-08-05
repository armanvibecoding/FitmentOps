using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class SupplierHealthStatuses
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Healthy, Degraded, Unhealthy],
        StringComparer.Ordinal);
}

[Index(nameof(Code), IsUnique = true)]
public sealed class Supplier
{
    public long Id { get; set; }

    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    [Required, StringLength(20)]
    public string HealthStatus { get; set; } = SupplierHealthStatuses.Healthy;

    /// <summary>Lower values have higher sourcing preference.</summary>
    public int Priority { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    [JsonIgnore]
    public ICollection<SupplierOffer> Offers { get; set; } = new List<SupplierOffer>();
}

[Index(nameof(SupplierId), nameof(ExternalOfferId), IsUnique = true)]
[Index(nameof(ProductId), nameof(OemNumber), nameof(Currency), nameof(IsActive), nameof(ValidUntilUtc))]
public sealed class SupplierOffer
{
    public long Id { get; set; }

    public long SupplierId { get; set; }

    [JsonIgnore]
    public Supplier Supplier { get; set; } = null!;

    public int ProductId { get; set; }

    [JsonIgnore]
    public Product Product { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1), JsonIgnore]
    public string ExternalOfferId { get; set; } = string.Empty;

    [Required, StringLength(80, MinimumLength = 1)]
    public string OemNumber { get; set; } = string.Empty;

    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = string.Empty;

    public decimal UnitCost { get; set; }
    public decimal ShippingCost { get; set; }
    public int AvailableQuantity { get; set; }
    public int LeadTimeDays { get; set; }
    public int MinimumOrderQuantity { get; set; } = 1;
    public DateTime ValidUntilUtc { get; set; }
    public bool CanDropship { get; set; }
    public bool CanSupplyWarehouse { get; set; }
    public bool IsActive { get; set; } = true;

    [Required, StringLength(64, MinimumLength = 64), JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}
