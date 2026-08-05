using System.ComponentModel.DataAnnotations;
using AutoPartsStore.API.Models;

namespace AutoPartsStore.API.Contracts;

public sealed class CreateOrderDto
{
    [Required]
    [StringLength(200, MinimumLength = 2)]
    public string CustomerName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(200)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required]
    [Phone]
    [StringLength(20)]
    public string CustomerPhone { get; set; } = string.Empty;

    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string ShippingAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string City { get; set; } = string.Empty;

    [Required]
    [StringLength(10)]
    public string PostalCode { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^PayAtDelivery$")]
    public string PaymentMethod { get; set; } = PaymentMethods.PayAtDelivery;

    [Required]
    [MinLength(1)]
    public List<OrderItemDto> Items { get; set; } = new();

    [Required, MinLength(1), MaxLength(10)]
    public List<LegalAcceptanceDto> LegalAcceptances { get; set; } = new();
}

public sealed class LegalAcceptanceDto
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, StringLength(40, MinimumLength = 1)]
    public string Version { get; set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    [RegularExpression("^[a-fA-F0-9]{64}$")]
    public string ContentSha256 { get; set; } = string.Empty;

    [Range(typeof(bool), "true", "true")]
    public bool Accepted { get; set; }
}

public sealed class OrderItemDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, 100)]
    public int Quantity { get; set; }
}

public sealed class CheckoutResponseDto
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string PaymentMethod { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public bool Replayed { get; set; }
}
