using System.Collections.Immutable;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class PaymentCallbackReconciliationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledProvider_FailsBeforeCommandInspectionOrDatabaseMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = database.CreateService(new DisabledPaymentGateway());

        var result = await service.ConfirmCallbackAsync(null!);

        Assert.Equal(PaymentReconciliationOutcome.ProviderDisabled, result.Outcome);
        Assert.Empty(await database.Context.PaymentEvents.ToListAsync());
    }

    [Fact]
    public async Task ConfirmCallback_ReconcilesExactSnapshotAndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var local = await database.AddPendingPaymentAsync(stock: 8, quantity: 2);
        var snapshot = ProviderSnapshot(local, GatewayPaymentStatus.Paid, "provider-payment-1");
        var gateway = new StubPaymentGateway
        {
            Confirmation = new PaymentConfirmationResult(
                PaymentGatewayOutcome.Succeeded,
                PaymentGatewayErrorCode.None,
                Payment: snapshot)
        };
        var service = database.CreateService(gateway);

        var first = await service.ConfirmCallbackAsync(
            new PaymentCallbackCommand(local.PaymentId, local.Token));
        var replay = await service.ConfirmCallbackAsync(
            new PaymentCallbackCommand(local.PaymentId, local.Token));

        Assert.Equal(PaymentReconciliationOutcome.Succeeded, first.Outcome);
        Assert.Equal(PaymentReconciliationOutcome.Replayed, replay.Outcome);
        database.Context.ChangeTracker.Clear();
        var payment = await database.Context.Payments
            .Include(candidate => candidate.Attempts)
            .SingleAsync(candidate => candidate.Id == local.PaymentId);
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Equal(PaymentAttemptStatuses.Succeeded, Assert.Single(payment.Attempts).Status);
        Assert.DoesNotContain(local.Token, first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaidSnapshotMismatch_NeverMarksPaymentPaid()
    {
        await using var database = await TestDatabase.CreateAsync();
        var local = await database.AddPendingPaymentAsync(stock: 8, quantity: 2);
        var mismatch = ProviderSnapshot(local, GatewayPaymentStatus.Paid, "provider-payment-2") with
        {
            AmountMinor = local.AmountMinor + 1
        };
        var gateway = new StubPaymentGateway
        {
            Confirmation = new PaymentConfirmationResult(
                PaymentGatewayOutcome.Succeeded,
                PaymentGatewayErrorCode.None,
                Payment: mismatch)
        };

        var result = await database.CreateService(gateway).ConfirmCallbackAsync(
            new PaymentCallbackCommand(local.PaymentId, local.Token));

        Assert.Equal(PaymentReconciliationOutcome.Conflict, result.Outcome);
        database.Context.ChangeTracker.Clear();
        var payment = await database.Context.Payments.SingleAsync(
            candidate => candidate.Id == local.PaymentId);
        Assert.Equal(PaymentStatuses.Pending, payment.Status);
    }

    [Fact]
    public async Task DefiniteFailure_AtomicallyCancelsOrderAndRestoresStock()
    {
        await using var database = await TestDatabase.CreateAsync();
        var local = await database.AddPendingPaymentAsync(stock: 8, quantity: 2);
        var gateway = new StubPaymentGateway
        {
            Confirmation = new PaymentConfirmationResult(
                PaymentGatewayOutcome.Failed,
                PaymentGatewayErrorCode.Declined,
                Payment: ProviderSnapshot(local, GatewayPaymentStatus.Failed, null))
        };

        var result = await database.CreateService(gateway).ConfirmCallbackAsync(
            new PaymentCallbackCommand(local.PaymentId, local.Token));

        Assert.Equal(PaymentReconciliationOutcome.Failed, result.Outcome);
        database.Context.ChangeTracker.Clear();
        var order = await database.Context.Orders
            .Include(candidate => candidate.Payment)
                .ThenInclude(payment => payment!.Attempts)
            .SingleAsync(candidate => candidate.Id == local.OrderId);
        var product = await database.Context.Products.SingleAsync(candidate => candidate.Id == 1);
        Assert.Equal(OrderStatuses.Cancelled, order.Status);
        Assert.Equal(PaymentStatuses.Failed, order.Payment!.Status);
        Assert.Equal(PaymentAttemptStatuses.Failed, Assert.Single(order.Payment.Attempts).Status);
        Assert.Equal(10, product.Stock);
    }

    [Fact]
    public async Task VerifiedWebhook_IsDurableAndDuplicateDoesNotCreateSecondEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var local = await database.AddPendingPaymentAsync(stock: 8, quantity: 2);
        var gateway = new StubPaymentGateway
        {
            Webhook = new PaymentWebhookVerificationResult(
                PaymentGatewayOutcome.Succeeded,
                PaymentGatewayErrorCode.None,
                ProviderEventId: "event-1",
                EventType: "payment.paid",
                Payment: ProviderSnapshot(local, GatewayPaymentStatus.Paid, "provider-payment-3"))
        };
        var service = database.CreateService(gateway);
        var body = "{}"u8.ToArray();
        var headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty;

        var first = await service.HandleWebhookAsync(body, headers);
        var replay = await service.HandleWebhookAsync(body, headers);

        Assert.Equal(PaymentReconciliationOutcome.Succeeded, first.Outcome);
        Assert.Equal(PaymentReconciliationOutcome.Replayed, replay.Outcome);
        Assert.Equal(1, await database.Context.PaymentEvents.CountAsync());
        Assert.DoesNotContain("{}", (await database.Context.PaymentEvents.SingleAsync()).PayloadSha256);
    }

    private static ProviderPaymentSnapshot ProviderSnapshot(
        LocalPayment local,
        GatewayPaymentStatus status,
        string? providerPaymentId) => new(
            providerPaymentId,
            local.OrderNumber,
            local.AmountMinor,
            "TRY",
            status);

    private sealed record LocalPayment(
        int OrderId,
        int PaymentId,
        string OrderNumber,
        long AmountMinor,
        string Token);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(AutoPartsDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AutoPartsDbContext(
                new DbContextOptionsBuilder<AutoPartsDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async Task<LocalPayment> AddPendingPaymentAsync(int stock, int quantity)
        {
            var product = await Context.Products.SingleAsync(candidate => candidate.Id == 1);
            product.Stock = stock;
            var orderNumber = $"ORDER-{Guid.NewGuid():N}";
            var token = $"token-{Guid.NewGuid():N}";
            var order = new Order
            {
                OrderNumber = orderNumber,
                CustomerName = "Test Customer",
                CustomerEmail = "customer@example.test",
                CustomerPhone = "+905551112233",
                ShippingAddress = "Test shipping address 1",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 100m,
                Status = OrderStatuses.Pending,
                OrderDate = Now.UtcDateTime,
                OrderItems =
                [
                    new OrderItem
                    {
                        ProductId = 1,
                        Quantity = quantity,
                        Price = 50m
                    }
                ]
            };
            var payment = new Payment
            {
                Provider = StubPaymentGateway.Name,
                Method = HostedCheckoutPaymentMethods.HostedCard,
                Status = PaymentStatuses.Pending,
                Amount = 100m,
                Currency = "TRY",
                IdempotencyKey = $"payment-{Guid.NewGuid():N}",
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
                ConcurrencyToken = Guid.NewGuid()
            };
            payment.Attempts.Add(new PaymentAttempt
            {
                Provider = StubPaymentGateway.Name,
                IdempotencyKey = $"attempt-{Guid.NewGuid():N}",
                ConversationId = $"conversation-{Guid.NewGuid():N}",
                HostedPaymentToken = token,
                Status = PaymentAttemptStatuses.RequiresCustomerAction,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime
            });
            order.Payment = payment;
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return new LocalPayment(order.Id, payment.Id, orderNumber, 10_000, token);
        }

        public PaymentCallbackReconciliationService CreateService(IPaymentGateway gateway) =>
            new(
                Context,
                gateway,
                new PaymentEventService(Context, new FixedTimeProvider(Now)),
                new PaymentStateService(Context),
                new OrderLifecycleService(Context),
                new FixedTimeProvider(Now));

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubPaymentGateway : IPaymentGateway
    {
        public const string Name = "testpay";
        public string ProviderName => Name;
        public bool IsEnabled => true;
        public PaymentConfirmationResult Confirmation { get; init; } = new(
            PaymentGatewayOutcome.Pending,
            PaymentGatewayErrorCode.ProviderUnavailable);
        public PaymentWebhookVerificationResult Webhook { get; init; } = new(
            PaymentGatewayOutcome.Pending,
            PaymentGatewayErrorCode.ProviderUnavailable);

        public Task<PaymentInitializationResult> InitializeAsync(
            InitializePaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentConfirmationResult> ConfirmAsync(
            ConfirmPaymentCommand command,
            CancellationToken cancellationToken = default) => Task.FromResult(Confirmation);

        public Task<PaymentRetrievalResult> RetrieveAsync(
            RetrievePaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentWebhookVerificationResult> VerifyWebhookAsync(
            VerifyPaymentWebhookCommand command,
            CancellationToken cancellationToken = default) => Task.FromResult(Webhook);

        public Task<PaymentRefundResult> RefundAsync(
            RefundPaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentInquiryResult> InquireAsync(
            InquirePaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
