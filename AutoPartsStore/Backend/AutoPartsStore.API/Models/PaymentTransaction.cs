using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(Provider), nameof(ProviderTransactionId), IsUnique = true)]
[Index(nameof(PaymentId), nameof(OrderItemId))]
public sealed class PaymentTransaction
{
    public long Id { get; set; }

    public int PaymentId { get; set; }

    [JsonIgnore]
    public Payment Payment { get; set; } = null!;

    public int OrderItemId { get; set; }

    [JsonIgnore]
    public OrderItem OrderItem { get; set; } = null!;

    [Required]
    [StringLength(50)]
    public string Provider { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [JsonIgnore]
    public string ProviderTransactionId { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue)]
    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; }

    [Range(0, double.MaxValue)]
    [Column(TypeName = "decimal(18,2)")]
    public decimal RefundedAmount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
