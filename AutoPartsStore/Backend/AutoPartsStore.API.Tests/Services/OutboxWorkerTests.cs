using AutoPartsStore.API.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class OutboxWorkerTests
{
    [Fact]
    public async Task FailedDispatch_RecordsFailureAndNeverCompletesClaim()
    {
        var store = new RecordingLeaseStore(CreateClaim());
        var dispatcher = new DelegateDispatcher(
            (_, _) => Task.FromResult(OutboxDispatchResult.Failed("consumer-unavailable")));
        var processor = CreateProcessor(store, dispatcher);

        var count = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Empty(store.Completed);
        var failure = Assert.Single(store.Failed);
        Assert.Equal("consumer-unavailable", failure.FailureCode);
        Assert.Equal(store.Claims[0].ClaimToken, failure.ClaimToken);
    }

    [Fact]
    public async Task ExceptionDuringDispatch_StoresOnlyStableFailureCode()
    {
        var store = new RecordingLeaseStore(CreateClaim());
        var dispatcher = new DelegateDispatcher(
            (_, _) => throw new InvalidOperationException("payload or secret must not be persisted"));
        var processor = CreateProcessor(store, dispatcher);

        await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(store.Completed);
        Assert.Equal("dispatch-exception", Assert.Single(store.Failed).FailureCode);
    }

    [Fact]
    public async Task CancellationDuringDispatch_LeavesLeaseForExpiryAndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingLeaseStore(CreateClaim());
        var dispatcher = new DelegateDispatcher(
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<OutboxDispatchResult>(token);
            });
        var processor = CreateProcessor(store, dispatcher);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessBatchAsync(cancellation.Token));

        Assert.Empty(store.Completed);
        Assert.Empty(store.Failed);
    }

    [Fact]
    public async Task DisabledDispatcher_DoesNotClaimOrFabricateSuccess()
    {
        var store = new RecordingLeaseStore(CreateClaim());
        var options = EnabledOptions();
        var processor = new OutboxBatchProcessor(
            store,
            new DisabledOutboxMessageDispatcher(),
            options,
            NullLogger<OutboxBatchProcessor>.Instance);

        var count = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Empty(store.Completed);
        Assert.Empty(store.Failed);
    }

    private static OutboxBatchProcessor CreateProcessor(
        RecordingLeaseStore store,
        IOutboxMessageDispatcher dispatcher)
    {
        return new OutboxBatchProcessor(
            store,
            dispatcher,
            EnabledOptions(),
            NullLogger<OutboxBatchProcessor>.Instance);
    }

    private static OutboxWorkerOptions EnabledOptions() => new()
    {
        Enabled = true,
        BatchSize = 10,
        MaxBatchesPerPoll = 2,
        LeaseDuration = TimeSpan.FromMinutes(1),
        PollInterval = TimeSpan.FromSeconds(1)
    };

    private static ClaimedOutboxMessage CreateClaim()
    {
        var token = new DateTime(2026, 8, 5, 12, 1, 0, DateTimeKind.Utc);
        return new ClaimedOutboxMessage(
            42,
            Guid.Parse("d9fb7388-3e27-4b42-b834-b6047d800d08"),
            "order.created",
            "order-42",
            "{\"customer\":\"sensitive\"}",
            1,
            token,
            token);
    }

    private sealed class DelegateDispatcher(
        Func<ClaimedOutboxMessage, CancellationToken, Task<OutboxDispatchResult>> dispatch)
        : IOutboxMessageDispatcher
    {
        public bool IsEnabled => true;

        public Task<OutboxDispatchResult> DispatchAsync(
            ClaimedOutboxMessage message,
            CancellationToken cancellationToken) => dispatch(message, cancellationToken);
    }

    private sealed class RecordingLeaseStore(params ClaimedOutboxMessage[] claims)
        : IOutboxLeaseStore
    {
        public IReadOnlyList<ClaimedOutboxMessage> Claims { get; } = claims;
        public List<(long MessageId, DateTime ClaimToken)> Completed { get; } = [];
        public List<(long MessageId, DateTime ClaimToken, string FailureCode)> Failed { get; } = [];
        public int ClaimCalls { get; private set; }

        public Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimDueAsync(
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            return Task.FromResult(Claims);
        }

        public Task<OutboxTransitionResult> CompleteAsync(
            long messageId,
            DateTime claimToken,
            CancellationToken cancellationToken = default)
        {
            Completed.Add((messageId, claimToken));
            return Task.FromResult(new OutboxTransitionResult(
                OutboxTransitionOutcome.Updated,
                OutboxMessageState.Completed));
        }

        public Task<OutboxTransitionResult> FailAsync(
            long messageId,
            DateTime claimToken,
            string failureCode,
            CancellationToken cancellationToken = default)
        {
            Failed.Add((messageId, claimToken, failureCode));
            return Task.FromResult(new OutboxTransitionResult(
                OutboxTransitionOutcome.Updated,
                OutboxMessageState.Pending));
        }
    }
}
