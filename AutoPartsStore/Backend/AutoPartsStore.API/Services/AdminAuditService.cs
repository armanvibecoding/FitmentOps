using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum AdminAuditAppendOutcome
{
    Appended,
    Replayed,
    Conflict,
    InvalidRequest
}

public static class AdminAuditErrorCodes
{
    public const string InvalidActorUserId = "invalid_actor_user_id";
    public const string InvalidActorRole = "invalid_actor_role";
    public const string InvalidAction = "invalid_action";
    public const string InvalidAggregateType = "invalid_aggregate_type";
    public const string InvalidActionAggregate = "invalid_action_aggregate";
    public const string InvalidAggregateId = "invalid_aggregate_id";
    public const string InvalidCorrelationId = "invalid_correlation_id";
    public const string InvalidIdempotencyKey = "invalid_idempotency_key";
    public const string InvalidOutcome = "invalid_outcome";
    public const string IdempotencyConflict = "idempotency_conflict";
}

public sealed record AdminAuditAppendRequest(
    int ActorUserId,
    string ActorRole,
    string Action,
    string AggregateType,
    long AggregateId,
    string CorrelationId,
    string IdempotencyKey,
    string Outcome);

public sealed record AdminAuditEventMetadata(
    long Id,
    long Sequence,
    int ActorUserId,
    string ActorRole,
    string Action,
    string AggregateType,
    long AggregateId,
    DateTime OccurredAtUtc,
    string CorrelationIdSha256,
    string IdempotencyKeySha256,
    string Outcome,
    string PreviousEventHashSha256,
    string EventHashSha256);

public sealed record AdminAuditAppendResult(
    AdminAuditAppendOutcome Outcome,
    AdminAuditEventMetadata? Event = null,
    string? ErrorCode = null);

public sealed record AdminAuditChainVerificationResult(
    bool IsValid,
    long VerifiedEventCount,
    long? FailedSequence = null,
    string? FailureCode = null);

public static class AdminAuditVerificationFailureCodes
{
    public const string SequenceGap = "sequence_gap";
    public const string InvalidPreviousHash = "invalid_previous_hash";
    public const string PreviousHashMismatch = "previous_hash_mismatch";
    public const string InvalidEventHash = "invalid_event_hash";
    public const string EventHashMismatch = "event_hash_mismatch";
}

public sealed class AdminAuditService
{
    public const int MaxOpaqueIdentifierLength = 128;
    public const int MaxQueryPageSize = 200;
    public const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private const int MaxAppendAttempts = 3;
    private const string HashSchemaVersion = "admin-audit-v1";
    private static readonly SemaphoreSlim AppendGate = new(1, 1);

    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public AdminAuditService(DbContext context, TimeProvider? timeProvider = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AdminAuditAppendResult> AppendAsync(
        AdminAuditAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);
        if (normalized.ErrorCode != null)
        {
            return new AdminAuditAppendResult(
                AdminAuditAppendOutcome.InvalidRequest,
                ErrorCode: normalized.ErrorCode);
        }

        await AppendGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindByIdempotencyHashAsync(
                normalized.IdempotencyKeySha256!,
                cancellationToken);
            if (existing != null)
            {
                return ResolveExisting(existing, normalized);
            }

            DbUpdateException? lastUpdateError = null;
            for (var attempt = 1; attempt <= MaxAppendAttempts; attempt++)
            {
                AdminAuditEvent? candidate = null;
                try
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    existing = await FindByIdempotencyHashAsync(
                        normalized.IdempotencyKeySha256!,
                        cancellationToken);
                    if (existing != null)
                    {
                        return ResolveExisting(existing, normalized);
                    }

                    var previous = await _context.Set<AdminAuditEvent>()
                        .AsNoTracking()
                        .OrderByDescending(auditEvent => auditEvent.Sequence)
                        .Select(auditEvent => new
                        {
                            auditEvent.Sequence,
                            auditEvent.EventHashSha256
                        })
                        .FirstOrDefaultAsync(cancellationToken);

                    var sequence = checked((previous?.Sequence ?? 0) + 1);
                    var previousHash = previous?.EventHashSha256 ?? GenesisHash;
                    var occurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    var eventHash = ComputeEventHash(
                        sequence,
                        normalized.ActorUserId,
                        normalized.ActorRole!,
                        normalized.Action!,
                        normalized.AggregateType!,
                        normalized.AggregateId,
                        occurredAtUtc,
                        normalized.CorrelationIdSha256!,
                        normalized.IdempotencyKeySha256!,
                        normalized.Outcome!,
                        previousHash);

                    candidate = AdminAuditEvent.Create(
                        sequence,
                        normalized.ActorUserId,
                        normalized.ActorRole!,
                        normalized.Action!,
                        normalized.AggregateType!,
                        normalized.AggregateId,
                        occurredAtUtc,
                        normalized.CorrelationIdSha256!,
                        normalized.IdempotencyKeySha256!,
                        normalized.Outcome!,
                        previousHash,
                        eventHash);

                    _context.Set<AdminAuditEvent>().Add(candidate);
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return new AdminAuditAppendResult(
                        AdminAuditAppendOutcome.Appended,
                        ToMetadata(candidate));
                }
                catch (DbUpdateException exception)
                {
                    lastUpdateError = exception;
                    if (candidate != null)
                    {
                        _context.Entry(candidate).State = EntityState.Detached;
                    }
                }

                existing = await FindByIdempotencyHashAsync(
                    normalized.IdempotencyKeySha256!,
                    cancellationToken);
                if (existing != null)
                {
                    return ResolveExisting(existing, normalized);
                }
            }

            throw new InvalidOperationException(
                "Audit append could not obtain a unique chain position.",
                lastUpdateError);
        }
        finally
        {
            AppendGate.Release();
        }
    }

    public async Task<IReadOnlyList<AdminAuditEventMetadata>> GetMetadataAsync(
        int pageSize = 100,
        long? beforeSequence = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > MaxQueryPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (beforeSequence is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        }

        var query = _context.Set<AdminAuditEvent>().AsNoTracking();
        if (beforeSequence.HasValue)
        {
            query = query.Where(auditEvent => auditEvent.Sequence < beforeSequence.Value);
        }

        var events = await query
            .OrderByDescending(auditEvent => auditEvent.Sequence)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return events.Select(ToMetadata).ToList();
    }

    public async Task<AdminAuditChainVerificationResult> VerifyChainAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await _context.Set<AdminAuditEvent>()
            .AsNoTracking()
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync(cancellationToken);

        var expectedSequence = 1L;
        var expectedPreviousHash = GenesisHash;
        var verifiedCount = 0L;

        foreach (var auditEvent in events)
        {
            if (auditEvent.Sequence != expectedSequence)
            {
                return Invalid(
                    verifiedCount,
                    auditEvent.Sequence,
                    AdminAuditVerificationFailureCodes.SequenceGap);
            }

            if (!IsSha256(auditEvent.PreviousEventHashSha256))
            {
                return Invalid(
                    verifiedCount,
                    auditEvent.Sequence,
                    AdminAuditVerificationFailureCodes.InvalidPreviousHash);
            }

            if (!HashEquals(auditEvent.PreviousEventHashSha256, expectedPreviousHash))
            {
                return Invalid(
                    verifiedCount,
                    auditEvent.Sequence,
                    AdminAuditVerificationFailureCodes.PreviousHashMismatch);
            }

            if (!IsSha256(auditEvent.EventHashSha256))
            {
                return Invalid(
                    verifiedCount,
                    auditEvent.Sequence,
                    AdminAuditVerificationFailureCodes.InvalidEventHash);
            }

            var calculatedHash = ComputeEventHash(
                auditEvent.Sequence,
                auditEvent.ActorUserId,
                auditEvent.ActorRole,
                auditEvent.Action,
                auditEvent.AggregateType,
                auditEvent.AggregateId,
                auditEvent.OccurredAtUtc,
                auditEvent.CorrelationIdSha256,
                auditEvent.IdempotencyKeySha256,
                auditEvent.Outcome,
                auditEvent.PreviousEventHashSha256);

            if (!HashEquals(auditEvent.EventHashSha256, calculatedHash))
            {
                return Invalid(
                    verifiedCount,
                    auditEvent.Sequence,
                    AdminAuditVerificationFailureCodes.EventHashMismatch);
            }

            verifiedCount++;
            expectedSequence++;
            expectedPreviousHash = auditEvent.EventHashSha256;
        }

        return new AdminAuditChainVerificationResult(true, verifiedCount);
    }

    private static AdminAuditChainVerificationResult Invalid(
        long verifiedEventCount,
        long failedSequence,
        string failureCode)
    {
        return new AdminAuditChainVerificationResult(
            false,
            verifiedEventCount,
            failedSequence,
            failureCode);
    }

    private Task<AdminAuditEvent?> FindByIdempotencyHashAsync(
        string idempotencyKeySha256,
        CancellationToken cancellationToken)
    {
        return _context.Set<AdminAuditEvent>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                auditEvent => auditEvent.IdempotencyKeySha256 == idempotencyKeySha256,
                cancellationToken);
    }

    private static AdminAuditAppendResult ResolveExisting(
        AdminAuditEvent existing,
        NormalizedRequest request)
    {
        var isReplay = existing.ActorUserId == request.ActorUserId &&
                       existing.ActorRole == request.ActorRole &&
                       existing.Action == request.Action &&
                       existing.AggregateType == request.AggregateType &&
                       existing.AggregateId == request.AggregateId &&
                       existing.CorrelationIdSha256 == request.CorrelationIdSha256 &&
                       existing.Outcome == request.Outcome;

        return isReplay
            ? new AdminAuditAppendResult(
                AdminAuditAppendOutcome.Replayed,
                ToMetadata(existing))
            : new AdminAuditAppendResult(
                AdminAuditAppendOutcome.Conflict,
                ToMetadata(existing),
                AdminAuditErrorCodes.IdempotencyConflict);
    }

    private static NormalizedRequest Normalize(AdminAuditAppendRequest? request)
    {
        if (request == null || request.ActorUserId <= 0)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidActorUserId);
        }

        var actorRole = NormalizeAllowlisted(request.ActorRole, AdminAuditRoles.All);
        if (actorRole == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidActorRole);
        }

        var action = NormalizeAllowlisted(request.Action, AdminAuditActions.All);
        if (action == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidAction);
        }

        var aggregateType = NormalizeAllowlisted(
            request.AggregateType,
            AdminAuditAggregateTypes.All);
        if (aggregateType == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidAggregateType);
        }

        if (!IsValidActionAggregate(action, aggregateType))
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidActionAggregate);
        }

        if (request.AggregateId <= 0)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidAggregateId);
        }

        var correlationId = NormalizeOpaqueIdentifier(request.CorrelationId);
        if (correlationId == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidCorrelationId);
        }

        var idempotencyKey = NormalizeOpaqueIdentifier(request.IdempotencyKey);
        if (idempotencyKey == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidIdempotencyKey);
        }

        var outcome = NormalizeAllowlisted(request.Outcome, AdminAuditOutcomes.All);
        if (outcome == null)
        {
            return NormalizedRequest.Invalid(AdminAuditErrorCodes.InvalidOutcome);
        }

        return new NormalizedRequest(
            request.ActorUserId,
            actorRole,
            action,
            aggregateType,
            request.AggregateId,
            Sha256(correlationId),
            Sha256(idempotencyKey),
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

    private static bool IsValidActionAggregate(string action, string aggregateType)
    {
        return action switch
        {
            AdminAuditActions.ProductCreated or
            AdminAuditActions.ProductUpdated or
            AdminAuditActions.ProductDeleted => aggregateType == AdminAuditAggregateTypes.Product,
            AdminAuditActions.CategoryUpdated => aggregateType == AdminAuditAggregateTypes.Category,
            AdminAuditActions.BrandUpdated =>
                aggregateType is AdminAuditAggregateTypes.Brand or AdminAuditAggregateTypes.PartBrand,
            AdminAuditActions.OrderStatusChanged or
            AdminAuditActions.OrderProcessing or
            AdminAuditActions.OrderCancelled => aggregateType == AdminAuditAggregateTypes.Order,
            AdminAuditActions.PaymentMarkedPaid => aggregateType == AdminAuditAggregateTypes.Payment,
            AdminAuditActions.ShipmentCreated or
            AdminAuditActions.ShipmentStatusChanged or
            AdminAuditActions.ShipmentLabelPending or
            AdminAuditActions.ShipmentReadyToShip or
            AdminAuditActions.ShipmentShipped or
            AdminAuditActions.ShipmentDelivered or
            AdminAuditActions.ShipmentFailed or
            AdminAuditActions.ShipmentCancelled => aggregateType == AdminAuditAggregateTypes.Shipment,
            AdminAuditActions.ReturnCreated or
            AdminAuditActions.ReturnStatusChanged or
            AdminAuditActions.ReturnApproved or
            AdminAuditActions.ReturnRejected or
            AdminAuditActions.ReturnReceived or
            AdminAuditActions.ReturnInspected or
            AdminAuditActions.ReturnCancelled or
            AdminAuditActions.ReturnClosed => aggregateType == AdminAuditAggregateTypes.Return,
            AdminAuditActions.RefundRequested or
            AdminAuditActions.RefundStatusChanged => aggregateType == AdminAuditAggregateTypes.Refund,
            AdminAuditActions.UserRoleChanged => aggregateType == AdminAuditAggregateTypes.User,
            AdminAuditActions.VehicleUpserted => aggregateType == AdminAuditAggregateTypes.Vehicle,
            AdminAuditActions.ProductFitmentUpserted => aggregateType == AdminAuditAggregateTypes.ProductFitment,
            AdminAuditActions.ProductIdentifierUpserted => aggregateType == AdminAuditAggregateTypes.ProductIdentifier,
            AdminAuditActions.DealerApplicationReviewed => aggregateType == AdminAuditAggregateTypes.DealerApplication,
            AdminAuditActions.CustomerGroupUpserted => aggregateType == AdminAuditAggregateTypes.CustomerGroup,
            AdminAuditActions.PriceListUpserted => aggregateType == AdminAuditAggregateTypes.PriceList,
            AdminAuditActions.PriceRuleUpserted => aggregateType == AdminAuditAggregateTypes.PriceRule,
            AdminAuditActions.BulkQuotePrepared => aggregateType == AdminAuditAggregateTypes.BulkQuote,
            AdminAuditActions.SupplierUpserted => aggregateType == AdminAuditAggregateTypes.Supplier,
            AdminAuditActions.SupplierOfferRegistered => aggregateType == AdminAuditAggregateTypes.SupplierOffer,
            AdminAuditActions.SupplierOfferStatusChanged => aggregateType == AdminAuditAggregateTypes.SupplierOffer,
            AdminAuditActions.SalesChannelStateChanged => aggregateType == AdminAuditAggregateTypes.SalesChannel,
            AdminAuditActions.ChannelListingSyncRequested => aggregateType == AdminAuditAggregateTypes.ChannelListing,
            AdminAuditActions.LegalDocumentCreated or
            AdminAuditActions.LegalDocumentPublished or
            AdminAuditActions.LegalDocumentRetired => aggregateType == AdminAuditAggregateTypes.LegalDocument,
            _ => false
        };
    }

    private static string? NormalizeOpaqueIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= MaxOpaqueIdentifierLength
            ? normalized
            : null;
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ComputeEventHash(
        long sequence,
        int actorUserId,
        string actorRole,
        string action,
        string aggregateType,
        long aggregateId,
        DateTime occurredAtUtc,
        string correlationIdSha256,
        string idempotencyKeySha256,
        string outcome,
        string previousEventHashSha256)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, HashSchemaVersion);
        Append(hash, sequence);
        Append(hash, actorUserId);
        Append(hash, actorRole);
        Append(hash, action);
        Append(hash, aggregateType);
        Append(hash, aggregateId);
        // EF's SQLite provider round-trips DateTime ticks but not DateTimeKind.
        // OccurredAtUtc is produced by TimeProvider in UTC; hashing the stored
        // ticks keeps verification deterministic across providers and time zones.
        Append(hash, occurredAtUtc.Ticks);
        Append(hash, correlationIdSha256);
        Append(hash, idempotencyKeySha256);
        Append(hash, outcome);
        Append(hash, previousEventHashSha256);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool HashEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static AdminAuditEventMetadata ToMetadata(AdminAuditEvent auditEvent)
    {
        return new AdminAuditEventMetadata(
            auditEvent.Id,
            auditEvent.Sequence,
            auditEvent.ActorUserId,
            auditEvent.ActorRole,
            auditEvent.Action,
            auditEvent.AggregateType,
            auditEvent.AggregateId,
            DateTime.SpecifyKind(auditEvent.OccurredAtUtc, DateTimeKind.Utc),
            auditEvent.CorrelationIdSha256,
            auditEvent.IdempotencyKeySha256,
            auditEvent.Outcome,
            auditEvent.PreviousEventHashSha256,
            auditEvent.EventHashSha256);
    }

    private sealed record NormalizedRequest(
        int ActorUserId,
        string? ActorRole,
        string? Action,
        string? AggregateType,
        long AggregateId,
        string? CorrelationIdSha256,
        string? IdempotencyKeySha256,
        string? Outcome,
        string? ErrorCode)
    {
        public static NormalizedRequest Invalid(string errorCode)
        {
            return new NormalizedRequest(0, null, null, null, 0, null, null, null, errorCode);
        }
    }
}
