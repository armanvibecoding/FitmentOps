using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class InventoryReservationStatuses
{
    public const string Active = "Active";
    public const string Committed = "Committed";
    public const string Released = "Released";
    public const string Expired = "Expired";
}

[Index(nameof(IdempotencyKey), IsUnique = true)]
[Index(nameof(CommittedOrderId), IsUnique = true)]
public sealed class InventoryReservation
{
    public long Id { get; set; }

    [Required, StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    [JsonIgnore]
    public string PayloadHash { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string Status { get; set; } = InventoryReservationStatuses.Active;

    public int? CommittedOrderId { get; set; }

    [JsonIgnore]
    public Order? CommittedOrder { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [ConcurrencyCheck]
    [JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public ICollection<InventoryReservationItem> Items { get; set; } =
        new List<InventoryReservationItem>();
}
