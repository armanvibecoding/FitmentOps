using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(ShipmentId), nameof(OrderItemId), IsUnique = true)]
public sealed class ShipmentItem
{
    public int Id { get; set; }

    public int ShipmentId { get; set; }

    [JsonIgnore]
    public Shipment Shipment { get; set; } = null!;

    public int OrderItemId { get; set; }

    [JsonIgnore]
    public OrderItem OrderItem { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}
