namespace AutoPartsStore.API.Services;

public sealed class OutboxWorkerOptions
{
    /// <summary>The fail-closed default prevents claiming messages without an explicitly configured handler.</summary>
    public bool Enabled { get; init; }
    public int BatchSize { get; init; } = 20;
    public int MaxBatchesPerPoll { get; init; } = 4;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (BatchSize is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize),
                "Batch size must be between 1 and 100.");
        }

        if (MaxBatchesPerPoll is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBatchesPerPoll),
                "Maximum batches per poll must be between 1 and 100.");
        }

        if (LeaseDuration <= TimeSpan.Zero || LeaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LeaseDuration),
                "Lease duration must be positive and cannot exceed one hour.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) ||
            PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                "Poll interval must be between 100 milliseconds and one hour.");
        }
    }
}

public readonly record struct OutboxDispatchResult
{
    private OutboxDispatchResult(bool succeeded, string? failureCode)
    {
        Succeeded = succeeded;
        FailureCode = failureCode;
    }

    public bool Succeeded { get; }
    public string? FailureCode { get; }

    public static OutboxDispatchResult Success() => new(true, null);

    public static OutboxDispatchResult Failed(string? failureCode = null) =>
        new(false, SafeFailureCode(failureCode));

    private static string SafeFailureCode(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return "dispatch-failed";
        }

        var normalized = failureCode.Trim();
        return normalized.Length <= 100 && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-')
                ? normalized
                : "dispatch-failed";
    }
}

/// <summary>
/// A dispatcher must only return success after the real, idempotent handler has
/// completed. Implementations must never log the message payload.
/// </summary>
public interface IOutboxMessageDispatcher
{
    bool IsEnabled { get; }

    Task<OutboxDispatchResult> DispatchAsync(
        ClaimedOutboxMessage message,
        CancellationToken cancellationToken);
}

public sealed class DisabledOutboxMessageDispatcher : IOutboxMessageDispatcher
{
    public bool IsEnabled => false;

    public Task<OutboxDispatchResult> DispatchAsync(
        ClaimedOutboxMessage message,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(OutboxDispatchResult.Failed("dispatcher-disabled"));
    }
}

public sealed class OutboxBatchProcessor
{
    private readonly IOutboxLeaseStore _leaseStore;
    private readonly IOutboxMessageDispatcher _dispatcher;
    private readonly OutboxWorkerOptions _options;
    private readonly ILogger<OutboxBatchProcessor> _logger;

    public OutboxBatchProcessor(
        IOutboxLeaseStore leaseStore,
        IOutboxMessageDispatcher dispatcher,
        OutboxWorkerOptions options,
        ILogger<OutboxBatchProcessor> logger)
    {
        _leaseStore = leaseStore;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
        _options.Validate();
    }

    public bool IsEnabled => _options.Enabled && _dispatcher.IsEnabled;

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return 0;
        }

        var claims = await _leaseStore.ClaimDueAsync(
            _options.BatchSize,
            _options.LeaseDuration,
            cancellationToken);

        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            OutboxDispatchResult dispatchResult;
            try
            {
                dispatchResult = await _dispatcher.DispatchAsync(claim, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leave the lease untouched. It becomes claimable after expiration.
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "Outbox dispatch threw. MessageId: {MessageId}, EventId: {EventId}, ExceptionType: {ExceptionType}",
                    claim.Id,
                    claim.EventId,
                    exception.GetType().Name);
                await FailAsync(claim, "dispatch-exception", cancellationToken);
                continue;
            }

            if (dispatchResult.Succeeded)
            {
                var completion = await _leaseStore.CompleteAsync(
                    claim.Id,
                    claim.ClaimToken,
                    cancellationToken);
                LogUnexpectedTransition("complete", claim, completion);
                continue;
            }

            await FailAsync(
                claim,
                dispatchResult.FailureCode ?? "dispatch-failed",
                cancellationToken);
        }

        return claims.Count;
    }

    private async Task FailAsync(
        ClaimedOutboxMessage claim,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var failure = await _leaseStore.FailAsync(
            claim.Id,
            claim.ClaimToken,
            failureCode,
            cancellationToken);
        LogUnexpectedTransition("fail", claim, failure);
    }

    private void LogUnexpectedTransition(
        string operation,
        ClaimedOutboxMessage claim,
        OutboxTransitionResult transition)
    {
        if (transition.Outcome is OutboxTransitionOutcome.Updated or OutboxTransitionOutcome.Replayed)
        {
            return;
        }

        _logger.LogWarning(
            "Outbox lease transition was rejected. Operation: {Operation}, MessageId: {MessageId}, EventId: {EventId}, Outcome: {Outcome}",
            operation,
            claim.Id,
            claim.EventId,
            transition.Outcome);
    }
}

public sealed class OutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxWorkerOptions _options;
    private readonly ILogger<OutboxWorker> _logger;

    public OutboxWorker(
        IServiceScopeFactory scopeFactory,
        OutboxWorkerOptions options,
        ILogger<OutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Outbox worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatcherEnabled = await ProcessBoundedPollAsync(stoppingToken);
                if (!dispatcherEnabled)
                {
                    _logger.LogCritical(
                        "Outbox worker is enabled but no enabled dispatcher is configured; no messages were claimed.");
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Outbox worker poll failed. ExceptionType: {ExceptionType}",
                    exception.GetType().Name);
            }

            await Task.Delay(_options.PollInterval, stoppingToken);
        }
    }

    private async Task<bool> ProcessBoundedPollAsync(CancellationToken cancellationToken)
    {
        for (var batch = 0; batch < _options.MaxBatchesPerPoll; batch++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>();
            if (!processor.IsEnabled)
            {
                return false;
            }

            var claimed = await processor.ProcessBatchAsync(cancellationToken);
            if (claimed < _options.BatchSize)
            {
                break;
            }
        }

        return true;
    }
}
