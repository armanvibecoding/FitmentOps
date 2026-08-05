using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class RefundServiceTests
{
    private static readonly DateTimeOffset CompletedAt =
        new(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PartialRefundUpdatesTransactionAndPaymentAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);
        var requested = await service.RequestRefundAsync(
            seeded.Payment.Id,
            seeded.Transaction.Id,
            40m,
            "TRY",
            "partial-refund",
            "iyzico");

        var processing = await service.MarkProcessingAsync(requested.Refund!.Id);
        var succeeded = await service.MarkSucceededAsync(
            requested.Refund.Id,
            "refund-partial-provider-id",
            CompletedAt);

        Assert.Equal(RefundTransitionOutcome.Updated, processing.Outcome);
        Assert.Equal(RefundTransitionOutcome.Updated, succeeded.Outcome);
        Assert.Equal(RefundStatuses.Succeeded, requested.Refund.Status);
        Assert.Equal(40m, seeded.Transaction.RefundedAmount);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, seeded.Payment.Status);
        Assert.Equal(CompletedAt.UtcDateTime, requested.Refund.CompletedAt);
    }

    [Fact]
    public async Task FullRefundTransitionsPaymentToRefunded()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);
        var requested = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            seeded.Payment.Amount,
            seeded.Payment.Currency,
            "full-refund",
            "iyzico");

        var succeeded = await service.MarkSucceededAsync(
            requested.Refund!.Id,
            "refund-full-provider-id",
            CompletedAt);

        Assert.Equal(RefundTransitionOutcome.Updated, succeeded.Outcome);
        Assert.Equal(PaymentStatuses.Refunded, seeded.Payment.Status);
    }

    [Fact]
    public async Task ReservedRefundsCannotExceedPaymentAmount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);

        var first = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            70m,
            "TRY",
            "over-refund-first",
            "iyzico");
        var excessive = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            30.01m,
            "TRY",
            "over-refund-second",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.Created, first.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, excessive.Outcome);
        Assert.Single(await database.Context.Refunds.ToListAsync());
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyReplaysOnlyIdenticalPayload()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);

        var first = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            25m,
            "TRY",
            "same-idempotency-key",
            " IYZICO ");
        var replay = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            25m,
            "TRY",
            "same-idempotency-key",
            "iyzico");
        var conflict = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            25.01m,
            "TRY",
            "same-idempotency-key",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.Created, first.Outcome);
        Assert.Equal(RefundTransitionOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Refund!.Id, replay.Refund!.Id);
        Assert.Equal(RefundTransitionOutcome.Conflict, conflict.Outcome);
        Assert.Single(await database.Context.Refunds.ToListAsync());
    }

    [Fact]
    public async Task SucceededRefundIsReplayableButNeverRegresses()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);
        var requested = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            10m,
            "TRY",
            "terminal-refund",
            "iyzico");
        var firstSuccess = await service.MarkSucceededAsync(
            requested.Refund!.Id,
            "terminal-provider-id",
            CompletedAt);

        var replay = await service.MarkSucceededAsync(
            requested.Refund.Id,
            "terminal-provider-id",
            CompletedAt.AddHours(1));
        var differentProviderId = await service.MarkSucceededAsync(
            requested.Refund.Id,
            "different-provider-id",
            CompletedAt.AddHours(1));
        var failed = await service.MarkFailedAsync(requested.Refund.Id, "late-failure");
        var unknown = await service.MarkUnknownAsync(requested.Refund.Id, "late-unknown");
        var processing = await service.MarkProcessingAsync(requested.Refund.Id);

        Assert.Equal(RefundTransitionOutcome.Updated, firstSuccess.Outcome);
        Assert.Equal(RefundTransitionOutcome.Replayed, replay.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, differentProviderId.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, failed.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, unknown.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, processing.Outcome);
        Assert.Equal(RefundStatuses.Succeeded, requested.Refund.Status);
        Assert.Equal("terminal-provider-id", requested.Refund.ProviderRefundId);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, seeded.Payment.Status);
        Assert.Equal(100m, seeded.Payment.Amount);
    }

    [Fact]
    public async Task ItemRefundReservationsCannotExceedTransactionPaidAmount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);

        var first = await service.RequestRefundAsync(
            seeded.Payment.Id,
            seeded.Transaction.Id,
            50m,
            "TRY",
            "item-refund-first",
            "iyzico");
        var excessive = await service.RequestRefundAsync(
            seeded.Payment.Id,
            seeded.Transaction.Id,
            10.01m,
            "TRY",
            "item-refund-second",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.Created, first.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, excessive.Outcome);
        Assert.Equal(60m, seeded.Transaction.PaidAmount);
        Assert.Single(await database.Context.Refunds.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentContextsCannotReserveMoreThanPaymentAmount()
    {
        var connectionString =
            $"Data Source=refund-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchorConnection = new SqliteConnection(connectionString);
        await anchorConnection.OpenAsync();

        var plainOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        int paymentId;
        await using (var setupContext = new AutoPartsDbContext(plainOptions))
        {
            await setupContext.Database.EnsureCreatedAsync();
            await using var setupDatabase = new TestDatabase(setupContext);
            var seeded = await setupDatabase.AddPaidPaymentAsync();
            paymentId = seeded.Payment.Id;
        }

        RefundTransitionResult? competingResult = null;
        var interceptor = new BeforeFirstSaveInterceptor(async cancellationToken =>
        {
            await using var competingContext = new AutoPartsDbContext(plainOptions);
            competingResult = await new RefundService(competingContext).RequestRefundAsync(
                paymentId,
                null,
                60m,
                "TRY",
                "competing-reservation",
                "iyzico",
                cancellationToken);
        });
        var racingOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var racingContext = new AutoPartsDbContext(racingOptions);
        var racingResult = await new RefundService(racingContext).RequestRefundAsync(
            paymentId,
            null,
            60m,
            "TRY",
            "stale-reservation",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.Created, competingResult!.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, racingResult.Outcome);

        await using var verificationContext = new AutoPartsDbContext(plainOptions);
        var storedRefund = Assert.Single(await verificationContext.Refunds.ToListAsync());
        Assert.Equal("competing-reservation", storedRefund.IdempotencyKey);
        Assert.Equal(60m, storedRefund.Amount);
    }

    [Fact]
    public async Task ConcurrentTerminalCallbacksCannotOverwriteSuccessfulRefund()
    {
        var connectionString =
            $"Data Source=refund-terminal-race-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchorConnection = new SqliteConnection(connectionString);
        await anchorConnection.OpenAsync();
        var plainOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        long refundId;
        await using (var setupContext = new AutoPartsDbContext(plainOptions))
        {
            await setupContext.Database.EnsureCreatedAsync();
            await using var setupDatabase = new TestDatabase(setupContext);
            var seeded = await setupDatabase.AddPaidPaymentAsync();
            var requested = await new RefundService(setupContext).RequestRefundAsync(
                seeded.Payment.Id,
                null,
                10m,
                "TRY",
                "terminal-race-refund",
                "iyzico");
            refundId = requested.Refund!.Id;
        }

        RefundTransitionResult? successfulCallback = null;
        var interceptor = new BeforeFirstSaveInterceptor(async cancellationToken =>
        {
            await using var successfulContext = new AutoPartsDbContext(plainOptions);
            successfulCallback = await new RefundService(successfulContext).MarkSucceededAsync(
                refundId,
                "terminal-race-provider-id",
                CompletedAt,
                cancellationToken);
        });
        var staleOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var staleContext = new AutoPartsDbContext(staleOptions);
        var staleFailure = await new RefundService(staleContext).MarkFailedAsync(
            refundId,
            "provider-timeout");

        Assert.Equal(RefundTransitionOutcome.Updated, successfulCallback!.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, staleFailure.Outcome);
        await using var verificationContext = new AutoPartsDbContext(plainOptions);
        var storedRefund = await verificationContext.Refunds.SingleAsync();
        var storedPayment = await verificationContext.Payments.SingleAsync();
        Assert.Equal(RefundStatuses.Succeeded, storedRefund.Status);
        Assert.Equal("terminal-race-provider-id", storedRefund.ProviderRefundId);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, storedPayment.Status);
    }

    [Fact]
    public async Task UnknownProviderOutcomeReservesCapacityUntilReconciled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);
        var requested = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            70m,
            "TRY",
            "unknown-reservation",
            "iyzico");

        var unknown = await service.MarkUnknownAsync(
            requested.Refund!.Id,
            "provider-outcome-uncertain");
        var excessive = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            30.01m,
            "TRY",
            "refund-during-unknown",
            "iyzico");
        var reconciled = await service.MarkSucceededAsync(
            requested.Refund.Id,
            "reconciled-provider-refund",
            CompletedAt);

        Assert.Equal(RefundTransitionOutcome.Updated, unknown.Outcome);
        Assert.Equal(RefundTransitionOutcome.Conflict, excessive.Outcome);
        Assert.Equal(RefundTransitionOutcome.Updated, reconciled.Outcome);
        Assert.Equal(RefundStatuses.Succeeded, requested.Refund.Status);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, seeded.Payment.Status);
    }

    [Fact]
    public async Task ReconciledUnknownFailureReleasesReservedCapacity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var service = new RefundService(database.Context);
        var first = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            100m,
            "TRY",
            "unknown-then-failed",
            "iyzico");

        await service.MarkUnknownAsync(first.Refund!.Id, "provider-outcome-uncertain");
        var failed = await service.MarkFailedAsync(first.Refund.Id, "provider-confirmed-failure");
        var replacement = await service.RequestRefundAsync(
            seeded.Payment.Id,
            null,
            100m,
            "TRY",
            "replacement-after-reconciliation",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.Updated, failed.Outcome);
        Assert.Equal(RefundTransitionOutcome.Created, replacement.Outcome);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    public async Task RefundAmountMustFitDecimal18Scale2(string amountText)
    {
        await using var database = await TestDatabase.CreateAsync();
        var seeded = await database.AddPaidPaymentAsync();
        var result = await new RefundService(database.Context).RequestRefundAsync(
            seeded.Payment.Id,
            null,
            decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture),
            "TRY",
            $"invalid-money-{amountText}",
            "iyzico");

        Assert.Equal(RefundTransitionOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(await database.Context.Refunds.ToListAsync());
    }

    [Fact]
    public async Task UnrelatedDatabaseFailureIsNotMisreportedAsProviderConflict()
    {
        var connectionString =
            $"Data Source=refund-db-failure-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        await using var anchorConnection = new SqliteConnection(connectionString);
        await anchorConnection.OpenAsync();
        var plainOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        long refundId;
        await using (var setupContext = new AutoPartsDbContext(plainOptions))
        {
            await setupContext.Database.EnsureCreatedAsync();
            await using var setupDatabase = new TestDatabase(setupContext);
            var seeded = await setupDatabase.AddPaidPaymentAsync();
            var requested = await new RefundService(setupContext).RequestRefundAsync(
                seeded.Payment.Id,
                null,
                10m,
                "TRY",
                "database-failure-refund",
                "iyzico");
            refundId = requested.Refund!.Id;
        }

        var failingOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ThrowingSaveInterceptor())
            .Options;
        await using var failingContext = new AutoPartsDbContext(failingOptions);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new RefundService(failingContext).MarkSucceededAsync(
                refundId,
                "provider-id-during-db-failure",
                CompletedAt));

        Assert.Equal("simulated-database-failure", exception.Message);
        await using var verificationContext = new AutoPartsDbContext(plainOptions);
        Assert.Equal(RefundStatuses.Requested, (await verificationContext.Refunds.SingleAsync()).Status);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        public TestDatabase(
            AutoPartsDbContext context,
            SqliteConnection? connection = null)
        {
            Context = context;
            Connection = connection;
        }

        public AutoPartsDbContext Context { get; }
        private SqliteConnection? Connection { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async Task<SeededPayment> AddPaidPaymentAsync()
        {
            var order = new Order
            {
                OrderNumber = $"REFUND-{Guid.NewGuid():N}",
                CustomerName = "Refund Test",
                CustomerEmail = "refund-test@example.com",
                CustomerPhone = "+905551112233",
                ShippingAddress = "Test Mahallesi Test Sokak No 1",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 100m,
                Status = OrderStatuses.Delivered,
                OrderItems =
                {
                    new OrderItem
                    {
                        ProductId = 1,
                        Quantity = 1,
                        Price = 100m
                    }
                }
            };
            var payment = new Payment
            {
                Order = order,
                Provider = "iyzico",
                Method = "Card",
                Status = PaymentStatuses.Paid,
                Amount = 100m,
                Currency = "TRY",
                IdempotencyKey = $"payment-{Guid.NewGuid():N}",
                PaidAt = new DateTime(2026, 8, 5, 15, 0, 0, DateTimeKind.Utc)
            };
            Context.Payments.Add(payment);
            await Context.SaveChangesAsync();

            var transaction = new PaymentTransaction
            {
                PaymentId = payment.Id,
                OrderItemId = order.OrderItems.Single().Id,
                Provider = "iyzico",
                ProviderTransactionId = $"transaction-{Guid.NewGuid():N}",
                PaidAmount = 60m,
                RefundedAmount = 0m,
                Currency = "TRY"
            };
            Context.PaymentTransactions.Add(transaction);
            await Context.SaveChangesAsync();

            return new SeededPayment(payment, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (Connection != null)
            {
                await Connection.DisposeAsync();
            }
        }
    }

    private sealed record SeededPayment(Payment Payment, PaymentTransaction Transaction);

    private sealed class BeforeFirstSaveInterceptor : SaveChangesInterceptor
    {
        private readonly Func<CancellationToken, Task> _beforeSave;
        private int _invoked;

        public BeforeFirstSaveInterceptor(Func<CancellationToken, Task> beforeSave)
        {
            _beforeSave = beforeSave;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0)
            {
                await _beforeSave(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ThrowingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("simulated-database-failure");
        }
    }
}
