using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(OperationId), IsUnique = true)]
[Index(nameof(Status), nameof(NextAttemptAtUtc))]
[Index(nameof(Status), nameof(LeaseExpiresAtUtc))]
public sealed class AdminAuditIntent
{
    private AdminAuditIntent()
    {
    }

    public long Id { get; private set; }

    public Guid OperationId { get; private set; }

    public int ActorUserId { get; private set; }

    [Required, StringLength(20)]
    public string ActorRole { get; private set; } = string.Empty;

    [Required, StringLength(50)]
    public string Action { get; private set; } = string.Empty;

    [Required, StringLength(30)]
    public string AggregateType { get; private set; } = string.Empty;

    public long AggregateId { get; private set; }

    [Required, StringLength(64, MinimumLength = 64)]
    public string CorrelationIdSha256 { get; private set; } = string.Empty;

    [Required, StringLength(20)]
    public string Outcome { get; private set; } = string.Empty;

    [Required, StringLength(20)]
    public string Status { get; private set; } = AdminAuditIntentStatuses.Pending;

    public int AttemptCount { get; private set; }

    public DateTime NextAttemptAtUtc { get; private set; }

    public Guid? LeaseId { get; private set; }

    public DateTime? LeaseExpiresAtUtc { get; private set; }

    [StringLength(64)]
    public string? LastErrorCode { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    [ConcurrencyCheck]
    public Guid ConcurrencyToken { get; private set; }

    internal static AdminAuditIntent Create(
        Guid operationId,
        int actorUserId,
        string actorRole,
        string action,
        string aggregateType,
        long aggregateId,
        string correlationIdSha256,
        string outcome,
        DateTime createdAtUtc)
    {
        return new AdminAuditIntent
        {
            OperationId = operationId,
            ActorUserId = actorUserId,
            ActorRole = actorRole,
            Action = action,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            CorrelationIdSha256 = correlationIdSha256,
            Outcome = outcome,
            Status = AdminAuditIntentStatuses.Pending,
            AttemptCount = 0,
            NextAttemptAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            ConcurrencyToken = Guid.NewGuid()
        };
    }

    internal void Claim(Guid leaseId, DateTime nowUtc, DateTime leaseExpiresAtUtc)
    {
        Status = AdminAuditIntentStatuses.Processing;
        AttemptCount = checked(AttemptCount + 1);
        LeaseId = leaseId;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        LastErrorCode = null;
        UpdatedAtUtc = nowUtc;
        ConcurrencyToken = Guid.NewGuid();
    }

    internal void MarkSucceeded(DateTime nowUtc)
    {
        Status = AdminAuditIntentStatuses.Succeeded;
        LeaseId = null;
        LeaseExpiresAtUtc = null;
        LastErrorCode = null;
        CompletedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        ConcurrencyToken = Guid.NewGuid();
    }

    internal void ScheduleRetry(DateTime nowUtc, DateTime nextAttemptAtUtc, string errorCode)
    {
        Status = AdminAuditIntentStatuses.Pending;
        LeaseId = null;
        LeaseExpiresAtUtc = null;
        LastErrorCode = errorCode;
        NextAttemptAtUtc = nextAttemptAtUtc;
        UpdatedAtUtc = nowUtc;
        ConcurrencyToken = Guid.NewGuid();
    }

    internal void MarkFailed(DateTime nowUtc, string errorCode)
    {
        Status = AdminAuditIntentStatuses.Failed;
        LeaseId = null;
        LeaseExpiresAtUtc = null;
        LastErrorCode = errorCode;
        CompletedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        ConcurrencyToken = Guid.NewGuid();
    }
}

public static class AdminAuditIntentStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        Pending,
        Processing,
        Succeeded,
        Failed
    ]);
}
