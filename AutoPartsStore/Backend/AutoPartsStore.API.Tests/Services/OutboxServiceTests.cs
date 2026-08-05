using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class OutboxServiceTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enqueue_SameEventAndEnvelopeIsIdempotent_ButMutationConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);
        var eventId = Guid.NewGuid();

        var first = await service.EnqueueAsync(
            eventId,
            " payment.paid ",
            " order-42 ",
            "{\"paymentId\":42}");
        var replay = await service.EnqueueAsync(
            eventId,
            "payment.paid",
            "order-42",
            "{\"paymentId\":42}");
        var conflict = await service.EnqueueAsync(
            eventId,
            "payment.paid",
            "order-42",
            "{\"paymentId\":99}");

        Assert.Equal(OutboxEnqueueOutcome.Enqueued, first.Outcome);
        Assert.Equal(OutboxEnqueueOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.MessageId, replay.MessageId);
        Assert.Equal(OutboxEnqueueOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ClaimDue_ReturnsBoundedBatch_AndDoesNotDoubleClaimActiveLease()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);

        for (var index = 0; index < 3; index++)
        {
            var result = await service.EnqueueAsync(
                Guid.NewGuid(),
                "stock.reserved",
                $"order-{index}",
                JsonSerializer.Serialize(new { orderId = index }));
            Assert.Equal(OutboxEnqueueOutcome.Enqueued, result.Outcome);
        }

        var firstBatch = await service.ClaimDueAsync(2, TimeSpan.FromMinutes(2));
        var secondBatch = await service.ClaimDueAsync(2, TimeSpan.FromMinutes(2));

        Assert.Equal(2, firstBatch.Count);
        Assert.Single(secondBatch);
        Assert.Empty(firstBatch.Select(message => message.Id)
            .Intersect(secondBatch.Select(message => message.Id)));
        Assert.All(firstBatch, message => Assert.Equal(1, message.AttemptCount));
        Assert.All(firstBatch, message => Assert.Equal(message.ClaimToken, message.ClaimExpiresAt));
    }

    [Fact]
    public async Task Complete_IsMonotonicAndIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);
        var enqueued = await service.EnqueueAsync(
            Guid.NewGuid(),
            "order.created",
            "order-1",
            "{\"orderId\":1}");
        var claim = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromMinutes(1)));

        var completed = await service.CompleteAsync(claim.Id, claim.ClaimToken);
        var replay = await service.CompleteAsync(claim.Id, claim.ClaimToken);
        var failAfterComplete = await service.FailAsync(
            claim.Id,
            claim.ClaimToken,
            "consumer-timeout");

        Assert.NotNull(enqueued.MessageId);
        Assert.Equal(OutboxTransitionOutcome.Updated, completed.Outcome);
        Assert.Equal(OutboxMessageState.Completed, completed.State);
        Assert.Equal(OutboxTransitionOutcome.Replayed, replay.Outcome);
        Assert.Equal(OutboxTransitionOutcome.Conflict, failAfterComplete.Outcome);

        var stored = await database.Context.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == claim.Id);
        Assert.NotNull(stored.ProcessedAt);
        Assert.Null(stored.LastError);
        Assert.Null(stored.NextAttemptAt);
    }

    [Fact]
    public async Task StaleWorkerCannotCompleteANewerClaim()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "shipment.requested",
            "order-7",
            "{\"orderId\":7}");

        var staleClaim = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(2));
        var currentClaim = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromMinutes(1)));

        var staleCompletion = await service.CompleteAsync(
            staleClaim.Id,
            staleClaim.ClaimToken);
        var currentCompletion = await service.CompleteAsync(
            currentClaim.Id,
            currentClaim.ClaimToken);

        Assert.Equal(OutboxTransitionOutcome.Conflict, staleCompletion.Outcome);
        Assert.Equal(OutboxMessageState.Processing, staleCompletion.State);
        Assert.Equal(OutboxTransitionOutcome.Updated, currentCompletion.Outcome);
    }

    [Fact]
    public async Task ExpiredLeaseCannotCompleteOrFailBeforeReclaim()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "shipment.complete",
            "order-expired-complete",
            "{\"orderId\":1}");
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "shipment.fail",
            "order-expired-fail",
            "{\"orderId\":2}");
        var claims = await service.ClaimDueAsync(2, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(2));

        var expiredComplete = await service.CompleteAsync(
            claims[0].Id,
            claims[0].ClaimToken);
        var expiredFail = await service.FailAsync(
            claims[1].Id,
            claims[1].ClaimToken,
            "worker-timeout");

        Assert.Equal(OutboxTransitionOutcome.Conflict, expiredComplete.Outcome);
        Assert.Equal(OutboxTransitionOutcome.Conflict, expiredFail.Outcome);
        Assert.Equal(OutboxMessageState.Pending, expiredComplete.State);
        Assert.Equal(OutboxMessageState.Pending, expiredFail.State);
        Assert.Equal(2, (await service.ClaimDueAsync(2, TimeSpan.FromMinutes(1))).Count);
    }

    [Fact]
    public async Task Fail_UsesExponentialCappedBackoff_ThenBecomesTerminal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(
            database.Context,
            clock,
            new OutboxDispatchOptions
            {
                MaxAttempts = 4,
                BaseRetryDelay = TimeSpan.FromSeconds(10),
                MaxRetryDelay = TimeSpan.FromSeconds(25)
            });
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "catalog.sync",
            "product-5",
            "{\"productId\":5}");

        var expectedDelays = new[] { 10, 20, 25 };
        for (var attempt = 0; attempt < expectedDelays.Length; attempt++)
        {
            var claim = Assert.Single(
                await service.ClaimDueAsync(1, TimeSpan.FromSeconds(5)));
            var failedAt = clock.GetUtcNow();
            var failed = await service.FailAsync(
                claim.Id,
                claim.ClaimToken,
                "consumer-unavailable");

            Assert.Equal(OutboxTransitionOutcome.Updated, failed.Outcome);
            Assert.Equal(OutboxMessageState.Pending, failed.State);
            Assert.Equal(
                failedAt.AddSeconds(expectedDelays[attempt]).UtcDateTime,
                failed.NextAttemptAt);
            clock.Advance(TimeSpan.FromSeconds(expectedDelays[attempt]));
        }

        var finalClaim = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromSeconds(5)));
        var terminal = await service.FailAsync(
            finalClaim.Id,
            finalClaim.ClaimToken,
            "consumer-unavailable");
        var replay = await service.FailAsync(
            finalClaim.Id,
            finalClaim.ClaimToken,
            "consumer-unavailable");

        Assert.Equal(4, finalClaim.AttemptCount);
        Assert.Equal(OutboxTransitionOutcome.Updated, terminal.Outcome);
        Assert.Equal(OutboxMessageState.Failed, terminal.State);
        Assert.Null(terminal.NextAttemptAt);
        Assert.Equal(OutboxTransitionOutcome.Replayed, replay.Outcome);
        Assert.Empty(await service.ClaimDueAsync(1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExpiredFinalLeaseBecomesTerminalWithoutAnotherDelivery()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(
            database.Context,
            clock,
            new OutboxDispatchOptions { MaxAttempts = 1 });
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "email.requested",
            "order-8",
            "{\"template\":\"order-created\"}");

        var abandoned = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Empty(await service.ClaimDueAsync(1, TimeSpan.FromSeconds(5)));
        var stored = await database.Context.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == abandoned.Id);
        Assert.NotNull(stored.ProcessedAt);
        Assert.Equal("max-attempts-exhausted", stored.LastError);
    }

    [Theory]
    [InlineData("customer@example.com failed")]
    [InlineData("payload={\"email\":\"customer@example.com\"}")]
    [InlineData("contains whitespace")]
    public async Task Fail_RejectsFreeFormOrSensitiveErrorDetails(string unsafeFailure)
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new MutableTimeProvider(InitialTime);
        var service = CreateService(database.Context, clock);
        await service.EnqueueAsync(
            Guid.NewGuid(),
            "notification.requested",
            "order-3",
            "{\"orderId\":3}");
        var claim = Assert.Single(
            await service.ClaimDueAsync(1, TimeSpan.FromMinutes(1)));

        var result = await service.FailAsync(claim.Id, claim.ClaimToken, unsafeFailure);

        Assert.Equal(OutboxTransitionOutcome.InvalidRequest, result.Outcome);
        var stored = await database.Context.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.Id == claim.Id);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task Enqueue_RejectsNonJsonAndOversizedPayloads()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context, new MutableTimeProvider(InitialTime));

        var nonJson = await service.EnqueueAsync(
            Guid.NewGuid(),
            "event",
            "aggregate",
            "customer@example.com");
        var oversized = await service.EnqueueAsync(
            Guid.NewGuid(),
            "event",
            "aggregate",
            "{\"value\":\"" + new string('x', 64 * 1024) + "\"}");

        Assert.Equal(OutboxEnqueueOutcome.InvalidRequest, nonJson.Outcome);
        Assert.Equal(OutboxEnqueueOutcome.InvalidRequest, oversized.Outcome);
        Assert.Equal(0, await database.Context.OutboxMessages.CountAsync());
    }

    private static OutboxService CreateService(
        AutoPartsDbContext context,
        MutableTimeProvider clock,
        OutboxDispatchOptions? options = null)
    {
        return new OutboxService(context, clock, options);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, AutoPartsDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();

            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
