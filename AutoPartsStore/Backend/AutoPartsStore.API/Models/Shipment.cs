using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class ShipmentStatuses
{
    public const string Created = "Created";
    public const string LabelPending = "LabelPending";
    public const string ReadyToShip = "ReadyToShip";
    public const string Shipped = "Shipped";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(Carrier), nameof(TrackingNumber), IsUnique = true)]
public sealed class Shipment
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    [JsonIgnore]
    public Order Order { get; set; } = null!;

    [Required]
    [StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    [JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string Status { get; set; } = ShipmentStatuses.Created;

    [StringLength(50)]
    public string? Carrier { get; set; }

    [StringLength(100)]
    public string? TrackingNumber { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    [ConcurrencyCheck]
    [JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public ICollection<ShipmentItem> Items { get; set; } = new List<ShipmentItem>();
}
