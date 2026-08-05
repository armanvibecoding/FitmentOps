using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(EventId), IsUnique = true)]
[Index(nameof(ProcessedAt), nameof(NextAttemptAt))]
public sealed class OutboxMessage
{
    public long Id { get; set; }

    public Guid EventId { get; set; } = Guid.NewGuid();

    [Required]
    [StringLength(200)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string AggregateId { get; set; } = string.Empty;

    /// <summary>
    /// Application-owned JSON envelope. Producers must include only the minimum
    /// references and event data required by the consumer, never raw payment or
    /// unnecessary customer data.
    /// </summary>
    [Required]
    [JsonIgnore]
    public string Payload { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    [Range(0, int.MaxValue)]
    public int AttemptCount { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    [StringLength(2000)]
    [JsonIgnore]
    public string? LastError { get; set; }
}
