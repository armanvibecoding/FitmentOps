using System.ComponentModel.DataAnnotations;

namespace AutoPartsStore.API.Contracts;

public sealed class PaymentCallbackHttpRequest
{
    [Range(1, int.MaxValue)]
    public int PaymentId { get; set; }

    [Required, StringLength(500, MinimumLength = 1)]
    public string HostedPaymentToken { get; set; } = string.Empty;

    public override string ToString() => $"{nameof(PaymentCallbackHttpRequest)} {{ Sensitive = true }}";
}

public sealed class PaymentReconciliationResponseDto
{
    public string Outcome { get; set; } = string.Empty;
    public string? PaymentStatus { get; set; }
    public string? AttemptStatus { get; set; }
    public string? Message { get; set; }
}
