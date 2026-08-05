using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(ReturnRequestId), nameof(OrderItemId), IsUnique = true)]
public sealed class ReturnItem
{
    public long Id { get; set; }

    public long ReturnRequestId { get; set; }

    [JsonIgnore]
    public ReturnRequest ReturnRequest { get; set; } = null!;

    public int OrderItemId { get; set; }

    [JsonIgnore]
    public OrderItem OrderItem { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }

    [Required]
    [StringLength(40)]
    public string ReasonCode { get; set; } = string.Empty;
}
