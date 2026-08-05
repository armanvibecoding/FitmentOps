using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(Provider), nameof(ProviderEventId), IsUnique = true)]
public sealed class PaymentEvent
{
    private PaymentEvent()
    {
    }

    public long Id { get; private set; }

    public int? PaymentId { get; private set; }
    public Payment? Payment { get; private set; }

    [Required]
    [StringLength(50)]
    public string Provider { get; private set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string ProviderEventId { get; private set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string EventType { get; private set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 64)]
    public string PayloadSha256 { get; private set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string ProcessingStatus { get; private set; } = PaymentEventProcessingStatuses.Received;

    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }

    [StringLength(100)]
    public string? ErrorCode { get; private set; }

    internal static PaymentEvent CreateReceived(
        string provider,
        string providerEventId,
        string eventType,
        string payloadSha256,
        int? paymentId,
        DateTime receivedAt)
    {
        return new PaymentEvent
        {
            Provider = provider,
            ProviderEventId = providerEventId,
            EventType = eventType,
            PayloadSha256 = payloadSha256,
            PaymentId = paymentId,
            ProcessingStatus = PaymentEventProcessingStatuses.Received,
            ReceivedAt = receivedAt
        };
    }
}

public static class PaymentEventProcessingStatuses
{
    public const string Received = "Received";
    public const string Processed = "Processed";
    public const string Failed = "Failed";
}
