using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class PaymentStateServiceTests
{
    private static readonly DateTimeOffset PaidAt =
        new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmPaid_ExactAmountAndCurrencyTransitionsPendingPayment()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            " IYZICO ",
            " pay_exact_123 ",
            249.90m,
            "TRY",
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Updated, result.Outcome);
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Equal("pay_exact_123", payment.ProviderPaymentId);
        Assert.Equal(PaidAt.UtcDateTime, payment.PaidAt);
        Assert.Equal(PaidAt.UtcDateTime, payment.UpdatedAt);
    }

    [Fact]
    public async Task ConfirmPaid_SameProviderIdentityAndAmountIsReplayWithoutMutation()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var first = await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            "pay_replay_123",
            payment.Amount,
            payment.Currency,
            PaidAt);
        var replay = await service.ConfirmPaidAsync(
            payment.Id,
            "Iyzico",
            "pay_replay_123",
            payment.Amount,
            payment.Currency,
            PaidAt.AddMinutes(15));

        Assert.Equal(PaymentStateTransitionOutcome.Updated, first.Outcome);
        Assert.Equal(PaymentStateTransitionOutcome.Replayed, replay.Outcome);
        Assert.Equal(PaidAt.UtcDateTime, payment.PaidAt);
        Assert.Equal(PaidAt.UtcDateTime, payment.UpdatedAt);
    }

    [Theory]
    [InlineData("pay_original", 249.91, "TRY")]
    [InlineData("pay_original", 249.90, "USD")]
    [InlineData("pay_different", 249.90, "TRY")]
    public async Task ConfirmPaid_ReplayMismatchReturnsConflict(
        string providerPaymentId,
        decimal amount,
        string currency)
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);
        await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            "pay_original",
            payment.Amount,
            payment.Currency,
            PaidAt);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            providerPaymentId,
            amount,
            currency,
            PaidAt.AddMinutes(1));

        Assert.Equal(PaymentStateTransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Equal("pay_original", payment.ProviderPaymentId);
        Assert.Equal(249.90m, payment.Amount);
        Assert.Equal("TRY", payment.Currency);
    }

    [Fact]
    public async Task MarkFailed_AfterPaidReturnsConflictAndPreservesPaidState()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);
        await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            "pay_paid_first",
            payment.Amount,
            payment.Currency,
            PaidAt);

        var result = await service.MarkFailedAsync(
            payment.Id,
            "iyzico",
            "late_failure",
            PaidAt.AddMinutes(1));

        Assert.Equal(PaymentStateTransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Null(payment.FailureCode);
        Assert.Equal(PaidAt.UtcDateTime, payment.PaidAt);
    }

    [Fact]
    public async Task MarkFailed_PendingPaymentTransitionsOnce()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var result = await service.MarkFailedAsync(
            payment.Id,
            "IYZICO",
            " declined ",
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Updated, result.Outcome);
        Assert.Equal(PaymentStatuses.Failed, payment.Status);
        Assert.Equal("declined", payment.FailureCode);
        Assert.Equal(PaidAt.UtcDateTime, payment.UpdatedAt);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public async Task ConfirmPaid_UnexpectedProviderReturnsConflict()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            "paytr",
            "pay_wrong_provider",
            payment.Amount,
            payment.Currency,
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(PaymentStatuses.Pending, payment.Status);
        Assert.Null(payment.ProviderPaymentId);
    }

    [Fact]
    public async Task ConfirmPaid_AfterFailureReturnsConflictAndPreservesFailedState()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);
        var failedAt = PaidAt.AddMinutes(-1);
        var failed = await service.MarkFailedAsync(
            payment.Id,
            "iyzico",
            "declined",
            failedAt);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            "pay_too_late",
            payment.Amount,
            payment.Currency,
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Updated, failed.Outcome);
        Assert.Equal(PaymentStateTransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(PaymentStatuses.Failed, payment.Status);
        Assert.Equal("declined", payment.FailureCode);
        Assert.Null(payment.PaidAt);
        Assert.Null(payment.ProviderPaymentId);
    }

    [Theory]
    [InlineData("", 249.90, "TRY")]
    [InlineData("pay_123", 0, "TRY")]
    [InlineData("pay_123", -1, "TRY")]
    [InlineData("pay_123", 249.90, "try")]
    [InlineData("pay_123", 249.90, "TR")]
    [InlineData("pay_123", 249.90, "TR1")]
    public async Task ConfirmPaid_InvalidProviderDataDoesNotChangePayment(
        string providerPaymentId,
        decimal amount,
        string currency)
    {
        await using var database = TestDatabase.CreateInMemory();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            providerPaymentId,
            amount,
            currency,
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(PaymentStatuses.Pending, payment.Status);
        Assert.Null(payment.ProviderPaymentId);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public async Task ConfirmPaid_OverlongProviderPaymentIdIsInvalid()
    {
        await using var database = TestDatabase.CreateInMemory();
        var payment = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var result = await service.ConfirmPaidAsync(
            payment.Id,
            "iyzico",
            new string('p', 201),
            payment.Amount,
            payment.Currency,
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(PaymentStatuses.Pending, payment.Status);
    }

    [Theory]
    [InlineData("Paid")]
    [InlineData("PartiallyRefunded")]
    [InlineData("Refunded")]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public async Task MarkFailed_NonPendingStateIsNeverRegressed(string initialStatus)
    {
        await using var database = TestDatabase.CreateInMemory();
        var payment = await database.AddPaymentAsync(initialStatus);
        var service = new PaymentStateService(database.Context);

        var result = await service.MarkFailedAsync(
            payment.Id,
            "iyzico",
            "out_of_order",
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Conflict, result.Outcome);
        Assert.Equal(initialStatus, payment.Status);
    }

    [Fact]
    public async Task ConcurrentStaleTransitionReturnsConflictAndPreservesFirstWriter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connection)
            .Options;

        int paymentId;
        await using (var setupContext = new AutoPartsDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            var database = new TestDatabase(setupContext);
            paymentId = (await database.AddPaymentAsync()).Id;
        }

        await using var paidContext = new AutoPartsDbContext(options);
        await using var failedContext = new AutoPartsDbContext(options);
        await paidContext.Payments.SingleAsync(payment => payment.Id == paymentId);
        await failedContext.Payments.SingleAsync(payment => payment.Id == paymentId);

        var paidResult = await new PaymentStateService(paidContext).ConfirmPaidAsync(
            paymentId,
            "iyzico",
            "pay_first_writer",
            249.90m,
            "TRY",
            PaidAt);
        var staleFailureResult = await new PaymentStateService(failedContext).MarkFailedAsync(
            paymentId,
            "iyzico",
            "late_failure",
            PaidAt.AddSeconds(1));

        Assert.Equal(PaymentStateTransitionOutcome.Updated, paidResult.Outcome);
        Assert.Equal(PaymentStateTransitionOutcome.Conflict, staleFailureResult.Outcome);

        await using var verificationContext = new AutoPartsDbContext(options);
        var storedPayment = await verificationContext.Payments.FindAsync(paymentId);
        Assert.Equal(PaymentStatuses.Paid, storedPayment!.Status);
        Assert.Equal("pay_first_writer", storedPayment.ProviderPaymentId);
    }

    [Fact]
    public async Task ProviderPaymentIdCannotPayTwoLocalPayments()
    {
        await using var database = await TestDatabase.CreateSqliteAsync();
        var first = await database.AddPaymentAsync();
        var second = await database.AddPaymentAsync();
        var service = new PaymentStateService(database.Context);

        var firstResult = await service.ConfirmPaidAsync(
            first.Id,
            "iyzico",
            "provider-payment-shared",
            first.Amount,
            first.Currency,
            PaidAt);
        var secondResult = await service.ConfirmPaidAsync(
            second.Id,
            "iyzico",
            "provider-payment-shared",
            second.Amount,
            second.Currency,
            PaidAt);

        Assert.Equal(PaymentStateTransitionOutcome.Updated, firstResult.Outcome);
        Assert.Equal(PaymentStateTransitionOutcome.Conflict, secondResult.Outcome);
        database.Context.ChangeTracker.Clear();
        var stored = await database.Context.Payments.OrderBy(payment => payment.Id).ToListAsync();
        Assert.Equal(PaymentStatuses.Paid, stored[0].Status);
        Assert.Equal(PaymentStatuses.Pending, stored[1].Status);
        Assert.Null(stored[1].ProviderPaymentId);
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

        public static async Task<TestDatabase> CreateSqliteAsync()
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

        public static TestDatabase CreateInMemory()
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseInMemoryDatabase($"payment-state-{Guid.NewGuid():N}")
                .Options;
            return new TestDatabase(new AutoPartsDbContext(options));
        }

        public async Task<Payment> AddPaymentAsync(
            string status = PaymentStatuses.Pending)
        {
            var payment = new Payment
            {
                Order = new Order
                {
                    OrderNumber = $"TEST-{Guid.NewGuid():N}",
                    CustomerName = "Payment State Test",
                    CustomerEmail = "payment-state@example.com",
                    CustomerPhone = "+905551112233",
                    ShippingAddress = "Test Mahallesi Test Sokak No 1",
                    City = "İstanbul",
                    PostalCode = "34000",
                    TotalAmount = 249.90m,
                    Status = OrderStatuses.Pending
                },
                Provider = "iyzico",
                Method = "Card",
                Status = status,
                Amount = 249.90m,
                Currency = "TRY",
                IdempotencyKey = $"payment-state-{Guid.NewGuid():N}",
                CreatedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc)
            };

            Context.Payments.Add(payment);
            await Context.SaveChangesAsync();
            return payment;
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
}
