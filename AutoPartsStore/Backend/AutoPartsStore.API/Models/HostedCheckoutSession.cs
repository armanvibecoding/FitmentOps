using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models;

/// <summary>
/// Durable local identity for one hosted checkout. It intentionally stores only
/// an irreversible request fingerprint and local aggregate references.
/// </summary>
public sealed class HostedCheckoutSession
{
    public long Id { get; set; }

    [Required, StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    [JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    public long InventoryReservationId { get; set; }

    [JsonIgnore]
    public InventoryReservation InventoryReservation { get; set; } = null!;

    public int OrderId { get; set; }

    [JsonIgnore]
    public Order Order { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
