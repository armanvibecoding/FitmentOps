using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(InventoryReservationId), nameof(ProductId), IsUnique = true)]
public sealed class InventoryReservationItem
{
    public long Id { get; set; }
    public long InventoryReservationId { get; set; }

    [JsonIgnore]
    public InventoryReservation InventoryReservation { get; set; } = null!;

    public int ProductId { get; set; }

    [JsonIgnore]
    public Product Product { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}
