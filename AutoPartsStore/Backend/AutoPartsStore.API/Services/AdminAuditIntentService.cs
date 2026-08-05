using System.Data;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum AdminAuditIntentStageOutcome
{
    Staged,
    InvalidRequest
}

public enum AdminAuditIntentFailureDisposition
{
    RetryScheduled,
    TerminalFailure,
    LeaseLost
}

public static class AdminAuditIntentErrorCodes
{
    public const string InvalidOperationId = "invalid_operation_id";
    public const string InvalidActorUserId = "invalid_actor_user_id";
    public const string InvalidActorRole = "invalid_actor_role";
    public const string InvalidAction = "invalid_action";
    public const string InvalidAggregateType = "invalid_aggregate_type";
    public const string InvalidAggregateId = "invalid_aggregate_id";
    public const string InvalidCorrelationId = "invalid_correlation_id";
    public const string InvalidOutcome = "invalid_outcome";
    public const string AuditConflict = "audit_conflict";
    public const string AuditInvalidRequest = "audit_invalid_request";
    public const string DispatchException = "dispatch_exception";
    public const string AttemptsExhausted = "attempts_exhausted";
}

public sealed record AdminAuditIntentStageRequest(
    Guid OperationId,
    int ActorUserId,
    string ActorRole,
    string Action,
    string AggregateType,
    long AggregateId,
    string CorrelationId,
    string Outcome);

public sealed record AdminAuditIntentStageResult(
    AdminAuditIntentStageOutcome Outcome,
    AdminAuditIntent? Intent = null,
    string? ErrorCode = null);

public sealed record AdminAuditIntentLease(
    long IntentId,
    Guid LeaseId,
    Guid OperationId,
    int ActorUserId,
    string ActorRole,
    string Action,
    string AggregateType,
    long AggregateId,
    string CorrelationIdSha256,
    string Outcome,
    int AttemptCount);

public sealed record AdminAuditIntentDispatchSummary(
    int Claimed,
    int Succeeded,
    int RetriesScheduled,
    int Failed);

public sealed record AdminAuditIntentOptions
{
    public const int AbsoluteMaxBatchSize = 100;
    public const int AbsoluteMaxAttempts = 20;

    public int MaxBatchSize { get; init; } = 25;
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MaxBatchSize is < 1 or > AbsoluteMaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize));
        }

        if (MaxAttempts is < 1 or > AbsoluteMaxAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        }

        if (LeaseDuration < TimeSpan.FromSeconds(1) ||
            LeaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        }

        if (RetryDelay < TimeSpan.FromMilliseconds(100) ||
            RetryDelay > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) ||
            PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }
    }
}

public sealed class AdminAuditIntentService
{
    public const int MaxCorrelationIdLength = 128;

    private static readonly SemaphoreSlim ClaimGate = new(1, 1);
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public AdminAuditIntentService(DbContext context, TimeProvider? timeProvider = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Deliberately does not call SaveChanges or create a transaction. The caller
    // owns the unit of work that persists both the business mutation and intent.
    public AdminAuditIntentStageResult Stage(AdminAuditIntentStageRequest request)
    {
        var normalized = Normalize(request);
        if (normalized.ErrorCode != null)
        {
            return new AdminAuditIntentStageResult(
                AdminAuditIntentStageOutcome.InvalidRequest,
                ErrorCode: normalized.ErrorCode);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var intent = AdminAuditIntent.Create(
            normalized.OperationId,
            normalized.ActorUserId,
            normalized.ActorRole!,
            normalized.Action!,
            normalized.AggregateType!,
            normalized.AggregateId,
            normalized.CorrelationIdSha256!,
            normalized.Outcome!,
            nowUtc);
        _context.Set<AdminAuditIntent>().Add(intent);
        return new AdminAuditIntentStageResult(AdminAuditIntentStageOutcome.Staged, intent);
    }

    public async Task<IReadOnlyList<AdminAuditIntentLease>> ClaimBatchAsync(
        AdminAuditIntentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AdminAuditIntentOptions();
        options.Validate();

        await ClaimGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

            var exhaustedStale = await _context.Set<AdminAuditIntent>()
                .Where(intent =>
                    intent.Status == AdminAuditIntentStatuses.Processing &&
                    intent.LeaseExpiresAtUtc <= nowUtc &&
                    intent.AttemptCount >= options.MaxAttempts)
                .ToListAsync(cancellationToken);
            foreach (var intent in exhaustedStale)
            {
                intent.MarkFailed(nowUtc, AdminAuditIntentErrorCodes.AttemptsExhausted);
            }

            var candidates = await _context.Set<AdminAuditIntent>()
                .Where(intent =>
                    intent.AttemptCount < options.MaxAttempts &&
                    ((intent.Status == AdminAuditIntentStatuses.Pending &&
                      intent.NextAttemptAtUtc <= nowUtc) ||
                     (intent.Status == AdminAuditIntentStatuses.Processing &&
                      intent.LeaseExpiresAtUtc <= nowUtc)))
                .OrderBy(intent => intent.NextAttemptAtUtc)
                .ThenBy(intent => intent.Id)
                .Take(options.MaxBatchSize)
                .ToListAsync(cancellationToken);

            var leases = new List<AdminAuditIntentLease>(candidates.Count);
            foreach (var intent in candidates)
            {
                var leaseId = Guid.NewGuid();
                intent.Claim(leaseId, nowUtc, nowUtc + options.LeaseDuration);
                leases.Add(ToLease(intent, leaseId));
            }

            if (exhaustedStale.Count > 0 || candidates.Count > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return leases;
        }
        finally
        {
            ClaimGate.Release();
        }
    }

    public async Task<bool> MarkSucceededAsync(
        long intentId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        var intent = await FindOwnedLeaseAsync(intentId, leaseId, cancellationToken);
        if (intent == null)
        {
            return false;
        }

        intent.MarkSucceeded(_timeProvider.GetUtcNow().UtcDateTime);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AdminAuditIntentFailureDisposition> RecordFailureAsync(
        long intentId,
        Guid leaseId,
        string errorCode,
        AdminAuditIntentOptions? options = null,
        bool terminal = false,
        CancellationToken cancellationToken = default)
    {
        options ??= new AdminAuditIntentOptions();
        options.Validate();
        if (!AllowedFailureCodes.Contains(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        var intent = await FindOwnedLeaseAsync(intentId, leaseId, cancellationToken);
        if (intent == null)
        {
            return AdminAuditIntentFailureDisposition.LeaseLost;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (terminal || intent.AttemptCount >= options.MaxAttempts)
        {
            intent.MarkFailed(nowUtc, errorCode);
            await _context.SaveChangesAsync(cancellationToken);
            return AdminAuditIntentFailureDisposition.TerminalFailure;
        }

        intent.ScheduleRetry(nowUtc, nowUtc + options.RetryDelay, errorCode);
        await _context.SaveChangesAsync(cancellationToken);
        return AdminAuditIntentFailureDisposition.RetryScheduled;
    }

    public async Task<AdminAuditIntentDispatchSummary> DispatchBatchAsync(
        AdminAuditService auditService,
        AdminAuditIntentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        options ??= new AdminAuditIntentOptions();
        options.Validate();

        var leases = await ClaimBatchAsync(options, cancellationToken);
        var succeeded = 0;
        var retries = 0;
        var failed = 0;

        foreach (var lease in leases)
        {
            try
            {
                var appendResult = await auditService.AppendAsync(
                    new AdminAuditAppendRequest(
                        lease.ActorUserId,
                        lease.ActorRole,
                        lease.Action,
                        lease.AggregateType,
                        lease.AggregateId,
                        lease.CorrelationIdSha256,
                        lease.OperationId.ToString("N"),
                        lease.Outcome),
                    cancellationToken);

                if (appendResult.Outcome is
                    AdminAuditAppendOutcome.Appended or AdminAuditAppendOutcome.Replayed)
                {
                    if (await MarkSucceededAsync(lease.IntentId, lease.LeaseId, cancellationToken))
                    {
                        succeeded++;
                    }

                    continue;
                }

                var errorCode = appendResult.Outcome == AdminAuditAppendOutcome.Conflict
                    ? AdminAuditIntentErrorCodes.AuditConflict
                    : AdminAuditIntentErrorCodes.AuditInvalidRequest;
                var disposition = await RecordFailureAsync(
                    lease.IntentId,
                    lease.LeaseId,
                    errorCode,
                    options,
                    terminal: true,
                    cancellationToken);
                failed += disposition == AdminAuditIntentFailureDisposition.TerminalFailure ? 1 : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                var disposition = await RecordFailureAsync(
                    lease.IntentId,
                    lease.LeaseId,
                    AdminAuditIntentErrorCodes.DispatchException,
                    options,
                    cancellationToken: cancellationToken);
                retries += disposition == AdminAuditIntentFailureDisposition.RetryScheduled ? 1 : 0;
                failed += disposition == AdminAuditIntentFailureDisposition.TerminalFailure ? 1 : 0;
            }
        }

        return new AdminAuditIntentDispatchSummary(leases.Count, succeeded, retries, failed);
    }

    private Task<AdminAuditIntent?> FindOwnedLeaseAsync(
        long intentId,
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        return _context.Set<AdminAuditIntent>().SingleOrDefaultAsync(
            intent =>
                intent.Id == intentId &&
                intent.Status == AdminAuditIntentStatuses.Processing &&
                intent.LeaseId == leaseId,
            cancellationToken);
    }

    private static AdminAuditIntentLease ToLease(AdminAuditIntent intent, Guid leaseId)
    {
        return new AdminAuditIntentLease(
            intent.Id,
            leaseId,
            intent.OperationId,
            intent.ActorUserId,
            intent.ActorRole,
            intent.Action,
            intent.AggregateType,
            intent.AggregateId,
            intent.CorrelationIdSha256,
            intent.Outcome,
            intent.AttemptCount);
    }

    private static NormalizedRequest Normalize(AdminAuditIntentStageRequest? request)
    {
        if (request == null || request.OperationId == Guid.Empty)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidOperationId);
        }

        if (request.ActorUserId <= 0)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidActorUserId);
        }

        var actorRole = NormalizeAllowlisted(request.ActorRole, AdminAuditRoles.All);
        if (actorRole == null)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidActorRole);
        }

        var action = NormalizeAllowlisted(request.Action, AdminAuditActions.All);
        if (action == null)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidAction);
        }

        var aggregateType = NormalizeAllowlisted(
            request.AggregateType,
            AdminAuditAggregateTypes.All);
        if (aggregateType == null)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidAggregateType);
        }

        if (request.AggregateId <= 0)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidAggregateId);
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidCorrelationId);
        }

        var correlationId = request.CorrelationId.Trim();
        if (correlationId.Length > MaxCorrelationIdLength)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidCorrelationId);
        }

        var outcome = NormalizeAllowlisted(request.Outcome, AdminAuditOutcomes.All);
        if (outcome == null)
        {
            return NormalizedRequest.Invalid(AdminAuditIntentErrorCodes.InvalidOutcome);
        }

        return new NormalizedRequest(
            request.OperationId,
            request.ActorUserId,
            actorRole,
            action,
            aggregateType,
            request.AggregateId,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(correlationId))),
            outcome,
            null);
    }

    private static string? NormalizeAllowlisted(
        string? value,
        IReadOnlyList<string> allowlist)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return allowlist.FirstOrDefault(
            allowed => string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlySet<string> AllowedFailureCodes = new HashSet<string>(
    [
        AdminAuditIntentErrorCodes.AuditConflict,
        AdminAuditIntentErrorCodes.AuditInvalidRequest,
        AdminAuditIntentErrorCodes.DispatchException,
        AdminAuditIntentErrorCodes.AttemptsExhausted
    ],
    StringComparer.Ordinal);

    private sealed record NormalizedRequest(
        Guid OperationId,
        int ActorUserId,
        string? ActorRole,
        string? Action,
        string? AggregateType,
        long AggregateId,
        string? CorrelationIdSha256,
        string? Outcome,
        string? ErrorCode)
    {
        public static NormalizedRequest Invalid(string errorCode)
        {
            return new NormalizedRequest(
                Guid.Empty,
                0,
                null,
                null,
                null,
                0,
                null,
                null,
                errorCode);
        }
    }
}
