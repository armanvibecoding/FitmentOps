using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum OutboxEnqueueOutcome
{
    Enqueued,
    Replayed,
    Conflict,
    InvalidRequest
}

public enum OutboxTransitionOutcome
{
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public enum OutboxMessageState
{
    Pending,
    Processing,
    Completed,
    Failed
}

public sealed record OutboxEnqueueResult(
    OutboxEnqueueOutcome Outcome,
    long? MessageId = null,
    string? Message = null);

public sealed record ClaimedOutboxMessage(
    long Id,
    Guid EventId,
    string Type,
    string AggregateId,
    [property: JsonIgnore] string Payload,
    int AttemptCount,
    DateTime ClaimToken,
    DateTime ClaimExpiresAt);

public sealed record OutboxTransitionResult(
    OutboxTransitionOutcome Outcome,
    OutboxMessageState? State = null,
    DateTime? NextAttemptAt = null,
    string? Message = null);

public sealed class OutboxDispatchOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(30);
}

public interface IOutboxLeaseStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimDueAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<OutboxTransitionResult> CompleteAsync(
        long messageId,
        DateTime claimToken,
        CancellationToken cancellationToken = default);

    Task<OutboxTransitionResult> FailAsync(
        long messageId,
        DateTime claimToken,
        string failureCode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates durable outbox state without logging payloads or failure details.
/// The current schema has no dedicated claim token, so the exact lease expiry in
/// NextAttemptAt is also used as the claim token for compare-and-set transitions.
/// </summary>
public sealed class OutboxService : IOutboxLeaseStore
{
    private const int MaxTypeLength = 200;
    private const int MaxAggregateIdLength = 200;
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxFailureCodeLength = 100;
    private const int MaxBatchSize = 100;
    private const string ExhaustedFailureCode = "max-attempts-exhausted";

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxDispatchOptions _options;

    public OutboxService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null,
        OutboxDispatchOptions? options = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new OutboxDispatchOptions();

        if (_options.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum attempt count must be positive.");
        }

        if (_options.BaseRetryDelay <= TimeSpan.Zero ||
            _options.MaxRetryDelay < _options.BaseRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Retry delays must be positive and the maximum cannot be below the base delay.");
        }
    }

    public async Task<OutboxEnqueueResult> EnqueueAsync(
        Guid eventId,
        string type,
        string aggregateId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateEnvelope(eventId, type, aggregateId, payload);
        if (validationError != null)
        {
            return new OutboxEnqueueResult(
                OutboxEnqueueOutcome.InvalidRequest,
                Message: validationError);
        }

        var normalizedType = type.Trim();
        var normalizedAggregateId = aggregateId.Trim();
        var existing = await FindByEventIdAsync(eventId, cancellationToken);
        if (existing != null)
        {
            return ResolveExisting(
                existing,
                normalizedType,
                normalizedAggregateId,
                payload);
        }

        var message = new OutboxMessage
        {
            EventId = eventId,
            Type = normalizedType,
            AggregateId = normalizedAggregateId,
            Payload = payload,
            CreatedAt = UtcNow()
        };
        _context.OutboxMessages.Add(message);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new OutboxEnqueueResult(OutboxEnqueueOutcome.Enqueued, message.Id);
        }
        catch (DbUpdateException)
        {
            _context.Entry(message).State = EntityState.Detached;

            // The unique EventId index is the final guard for concurrent producers.
            existing = await FindByEventIdAsync(eventId, cancellationToken);
            if (existing != null)
            {
                return ResolveExisting(
                    existing,
                    normalizedType,
                    normalizedAggregateId,
                    payload);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimDueAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is <= 0 or > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"Batch size must be between 1 and {MaxBatchSize}.");
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                "Lease duration must be positive and cannot exceed one hour.");
        }

        var now = UtcNow();

        // A worker can disappear on its final lease. Once that lease expires, make
        // the exhausted message terminal instead of leaving it pending forever.
        await _context.OutboxMessages
            .Where(message =>
                message.ProcessedAt == null &&
                message.AttemptCount >= _options.MaxAttempts &&
                (message.NextAttemptAt == null || message.NextAttemptAt <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.ProcessedAt, now)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)null)
                    .SetProperty(message => message.LastError, ExhaustedFailureCode),
                cancellationToken);

        var candidateIds = await _context.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.ProcessedAt == null &&
                message.AttemptCount < _options.MaxAttempts &&
                (message.NextAttemptAt == null || message.NextAttemptAt <= now))
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var claims = new List<ClaimedOutboxMessage>(candidateIds.Count);
        foreach (var candidateId in candidateIds)
        {
            // Each candidate gets a distinct token, even under a fixed test clock.
            // This also prevents a stale claim from matching a later lease by chance.
            var claimToken = now
                .Add(leaseDuration)
                .AddTicks(claims.Count + 1L);

            var updated = await _context.OutboxMessages
                .Where(message =>
                    message.Id == candidateId &&
                    message.ProcessedAt == null &&
                    message.AttemptCount < _options.MaxAttempts &&
                    (message.NextAttemptAt == null || message.NextAttemptAt <= now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            message => message.AttemptCount,
                            message => message.AttemptCount + 1)
                        .SetProperty(message => message.NextAttemptAt, claimToken)
                        .SetProperty(message => message.LastError, (string?)null),
                    cancellationToken);

            if (updated == 0)
            {
                continue;
            }

            var claimed = await _context.OutboxMessages
                .AsNoTracking()
                .SingleAsync(
                    message =>
                        message.Id == candidateId &&
                        message.ProcessedAt == null &&
                        message.NextAttemptAt == claimToken,
                    cancellationToken);

            claims.Add(new ClaimedOutboxMessage(
                claimed.Id,
                claimed.EventId,
                claimed.Type,
                claimed.AggregateId,
                claimed.Payload,
                claimed.AttemptCount,
                claimToken,
                claimToken));
        }

        return claims;
    }

    public async Task<OutboxTransitionResult> CompleteAsync(
        long messageId,
        DateTime claimToken,
        CancellationToken cancellationToken = default)
    {
        if (messageId <= 0 || claimToken == default)
        {
            return InvalidTransition("Message id and claim token are required.");
        }

        var now = UtcNow();
        var updated = await _context.OutboxMessages
            .Where(message =>
                message.Id == messageId &&
                message.ProcessedAt == null &&
                message.NextAttemptAt == claimToken &&
                message.NextAttemptAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.ProcessedAt, now)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)null)
                    .SetProperty(message => message.LastError, (string?)null),
                cancellationToken);

        if (updated == 1)
        {
            return new OutboxTransitionResult(
                OutboxTransitionOutcome.Updated,
                OutboxMessageState.Completed);
        }

        var current = await FindByIdAsync(messageId, cancellationToken);
        if (current == null)
        {
            return new OutboxTransitionResult(OutboxTransitionOutcome.NotFound);
        }

        if (current.ProcessedAt != null && current.LastError == null)
        {
            return new OutboxTransitionResult(
                OutboxTransitionOutcome.Replayed,
                OutboxMessageState.Completed);
        }

        return new OutboxTransitionResult(
            OutboxTransitionOutcome.Conflict,
            StateOf(current, now),
            current.NextAttemptAt,
            "The claim is stale or the message is already terminal.");
    }

    public async Task<OutboxTransitionResult> FailAsync(
        long messageId,
        DateTime claimToken,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        if (messageId <= 0 || claimToken == default || !IsSafeFailureCode(failureCode))
        {
            return InvalidTransition(
                "Message id, claim token and a safe machine-readable failure code are required.");
        }

        var normalizedFailureCode = failureCode.Trim();
        var now = UtcNow();
        var currentClaim = await _context.OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                message =>
                    message.Id == messageId &&
                    message.ProcessedAt == null &&
                    message.NextAttemptAt == claimToken &&
                    message.NextAttemptAt > now,
                cancellationToken);

        if (currentClaim == null)
        {
            return await ResolveFailedCompareAndSetAsync(
                messageId,
                normalizedFailureCode,
                cancellationToken);
        }

        var isTerminal = currentClaim.AttemptCount >= _options.MaxAttempts;
        var nextAttemptAt = isTerminal
            ? (DateTime?)null
            : now.Add(CalculateRetryDelay(currentClaim.AttemptCount));

        var updated = await _context.OutboxMessages
            .Where(message =>
                message.Id == messageId &&
                message.ProcessedAt == null &&
                message.NextAttemptAt == claimToken &&
                message.NextAttemptAt > now &&
                message.AttemptCount == currentClaim.AttemptCount)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        message => message.ProcessedAt,
                        isTerminal ? now : (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAt, nextAttemptAt)
                    .SetProperty(message => message.LastError, normalizedFailureCode),
                cancellationToken);

        if (updated == 0)
        {
            return await ResolveFailedCompareAndSetAsync(
                messageId,
                normalizedFailureCode,
                cancellationToken);
        }

        return new OutboxTransitionResult(
            OutboxTransitionOutcome.Updated,
            isTerminal ? OutboxMessageState.Failed : OutboxMessageState.Pending,
            nextAttemptAt);
    }

    private async Task<OutboxTransitionResult> ResolveFailedCompareAndSetAsync(
        long messageId,
        string normalizedFailureCode,
        CancellationToken cancellationToken)
    {
        var current = await FindByIdAsync(messageId, cancellationToken);
        if (current == null)
        {
            return new OutboxTransitionResult(OutboxTransitionOutcome.NotFound);
        }

        // Claiming clears LastError, so an identical stored code proves this failure
        // was already applied and makes worker retries idempotent.
        if (string.Equals(
                current.LastError,
                normalizedFailureCode,
                StringComparison.Ordinal))
        {
            return new OutboxTransitionResult(
                OutboxTransitionOutcome.Replayed,
                StateOf(current, UtcNow()),
                current.NextAttemptAt);
        }

        return new OutboxTransitionResult(
            OutboxTransitionOutcome.Conflict,
            StateOf(current, UtcNow()),
            current.NextAttemptAt,
            "The claim is stale or another worker has already changed the message.");
    }

    private Task<OutboxMessage?> FindByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        return _context.OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.EventId == eventId, cancellationToken);
    }

    private Task<OutboxMessage?> FindByIdAsync(
        long messageId,
        CancellationToken cancellationToken)
    {
        return _context.OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(message => message.Id == messageId, cancellationToken);
    }

    private static OutboxEnqueueResult ResolveExisting(
        OutboxMessage existing,
        string type,
        string aggregateId,
        string payload)
    {
        if (string.Equals(existing.Type, type, StringComparison.Ordinal) &&
            string.Equals(existing.AggregateId, aggregateId, StringComparison.Ordinal) &&
            string.Equals(existing.Payload, payload, StringComparison.Ordinal))
        {
            return new OutboxEnqueueResult(
                OutboxEnqueueOutcome.Replayed,
                existing.Id);
        }

        return new OutboxEnqueueResult(
            OutboxEnqueueOutcome.Conflict,
            existing.Id,
            "The event id is already associated with a different immutable envelope.");
    }

    private static string? ValidateEnvelope(
        Guid eventId,
        string type,
        string aggregateId,
        string payload)
    {
        if (eventId == Guid.Empty)
        {
            return "Event id is required.";
        }

        if (string.IsNullOrWhiteSpace(type) || type.Trim().Length > MaxTypeLength)
        {
            return $"Event type must be between 1 and {MaxTypeLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(aggregateId) ||
            aggregateId.Trim().Length > MaxAggregateIdLength)
        {
            return $"Aggregate id must be between 1 and {MaxAggregateIdLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(payload) ||
            Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            return $"Payload must be valid JSON up to {MaxPayloadBytes} UTF-8 bytes.";
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "Payload must be a JSON object envelope.";
            }
        }
        catch (JsonException)
        {
            return "Payload must be a valid JSON object envelope.";
        }

        return null;
    }

    private static bool IsSafeFailureCode(string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return false;
        }

        var normalized = failureCode.Trim();
        return normalized.Length <= MaxFailureCodeLength &&
               normalized.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or ':' or '-');
    }

    private TimeSpan CalculateRetryDelay(int attemptCount)
    {
        var delay = _options.BaseRetryDelay;
        for (var attempt = 1;
             attempt < attemptCount && delay < _options.MaxRetryDelay && attempt < 63;
             attempt++)
        {
            if (delay.Ticks > _options.MaxRetryDelay.Ticks / 2)
            {
                return _options.MaxRetryDelay;
            }

            delay += delay;
        }

        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    private static OutboxMessageState StateOf(OutboxMessage message, DateTime now)
    {
        if (message.ProcessedAt != null)
        {
            return message.LastError == null
                ? OutboxMessageState.Completed
                : OutboxMessageState.Failed;
        }

        return message.NextAttemptAt > now && message.LastError == null
            ? OutboxMessageState.Processing
            : OutboxMessageState.Pending;
    }

    private static OutboxTransitionResult InvalidTransition(string message)
    {
        return new OutboxTransitionResult(
            OutboxTransitionOutcome.InvalidRequest,
            Message: message);
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
}
