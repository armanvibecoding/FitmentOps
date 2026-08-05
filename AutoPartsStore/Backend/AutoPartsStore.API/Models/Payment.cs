using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models;

public sealed class Payment
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    [Required]
    [StringLength(50)]
    public string Provider { get; set; } = PaymentProviders.Manual;

    [Required]
    [StringLength(50)]
    public string Method { get; set; } = PaymentMethods.PayAtDelivery;

    [Required]
    [StringLength(50)]
    public string Status { get; set; } = PaymentStatuses.Pending;

    [Required]
    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "TRY";

    [Required]
    [StringLength(100)]
    [JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    [StringLength(200)]
    [JsonIgnore]
    public string? ProviderPaymentId { get; set; }

    [StringLength(100)]
    public string? FailureCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }

    [ConcurrencyCheck]
    [JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public ICollection<PaymentEvent> Events { get; } = new List<PaymentEvent>();
    public ICollection<PaymentAttempt> Attempts { get; } = new List<PaymentAttempt>();
    public ICollection<PaymentTransaction> Transactions { get; } = new List<PaymentTransaction>();
    public ICollection<Refund> Refunds { get; } = new List<Refund>();
}
