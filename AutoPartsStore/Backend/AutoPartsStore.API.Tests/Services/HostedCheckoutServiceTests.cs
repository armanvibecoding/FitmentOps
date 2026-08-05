using System.Text.Json;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class HostedCheckoutServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledGateway_FailsBeforeInventoryOrOrderMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 125.50m);
        var service = database.CreateService(new DisabledPaymentGateway());

        var result = await service.StartAsync(Command("hosted-disabled-0001"));

        Assert.Equal(HostedCheckoutOutcome.ProviderDisabled, result.Outcome);
        Assert.Equal(3, await database.StockAsync(1));
        Assert.Empty(await database.Context.Orders.ToListAsync());
        Assert.Empty(await database.Context.InventoryReservations.ToListAsync());
        Assert.Empty(await database.Context.Set<HostedCheckoutSession>().ToListAsync());
    }

    [Fact]
    public async Task SuccessfulInitialization_CommitsOneOrderAndCallsProviderOutsideTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 125.50m);
        var gateway = new FakePaymentGateway(
            new PaymentInitializationResult(
                PaymentGatewayOutcome.Succeeded,
                PaymentGatewayErrorCode.None,
                HostedPaymentPageUri: new Uri("https://pay.example.test/checkout/42"),
                HostedPaymentToken: "secret-hosted-token",
                ProviderPaymentId: "provider-payment-42",
                ExpiresAtUtc: Now.AddMinutes(10)))
        {
            OnInitialize = command =>
            {
                Assert.Null(database.Context.Database.CurrentTransaction);
                Assert.Equal(12_550, command.Expected.AmountMinor);
                var line = Assert.Single(command.Expected.BasketItems);
                Assert.Equal(12_550, line.UnitPriceMinor);
                Assert.Equal(12_550, line.LineTotalMinor);
            }
        };
        var service = database.CreateService(gateway);

        var result = await service.StartAsync(Command("hosted-success-0001"));

        Assert.Equal(HostedCheckoutOutcome.RequiresCustomerAction, result.Outcome);
        Assert.False(result.Replayed);
        Assert.Equal(new Uri("https://pay.example.test/checkout/42"), result.RedirectUri);
        Assert.Equal(1, gateway.InitializeCalls);
        Assert.Equal(2, await database.StockAsync(1));
        database.Context.ChangeTracker.Clear();
        var session = await database.SessionQuery().SingleAsync();
        Assert.Equal(InventoryReservationStatuses.Committed, session.InventoryReservation.Status);
        Assert.Equal(PaymentStatuses.Pending, session.Order.Payment!.Status);
        var attempt = Assert.Single(session.Order.Payment.Attempts);
        Assert.Equal(PaymentAttemptStatuses.RequiresCustomerAction, attempt.Status);
        Assert.Equal("secret-hosted-token", attempt.HostedPaymentToken);
        Assert.Equal(2, await database.Context.LegalAcceptances.CountAsync());
        Assert.DoesNotContain("secret-hosted-token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("secret-hosted-token", JsonSerializer.Serialize(attempt));
        Assert.DoesNotContain(
            typeof(HostedCheckoutCommand).GetProperties(),
            property => property.Name.Contains("Pan", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Cvv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingPublishedLegalDocumentFailsBeforeReservationOrProviderCall()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 125.50m);
        var published = await database.Context.LegalDocumentVersions.ToListAsync();
        foreach (var document in published)
        {
            document.Retire(Now.UtcDateTime);
        }
        await database.Context.SaveChangesAsync();
        var gateway = FakePaymentGateway.Success();

        var result = await database.CreateService(gateway)
            .StartAsync(Command("hosted-legal-missing-01"));

        Assert.Equal(HostedCheckoutOutcome.ConfigurationUnavailable, result.Outcome);
        Assert.Equal(3, await database.StockAsync(1));
        Assert.Equal(0, gateway.InitializeCalls);
        Assert.Empty(await database.Context.InventoryReservations.ToListAsync());
        Assert.Empty(await database.Context.Orders.ToListAsync());
    }

    [Fact]
    public async Task ReplayWithNewServerExpiry_ReturnsSameOrderWithoutExtendingOrReducingStockAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 50m);
        var clock = new MutableTimeProvider(Now);
        var gateway = FakePaymentGateway.Success();
        var service = database.CreateService(gateway, clock);
        var command = Command("hosted-replay-00001");

        var first = await service.StartAsync(command);
        var originalExpiry = await database.Context.InventoryReservations
            .Select(reservation => reservation.ExpiresAt)
            .SingleAsync();
        clock.Now = Now.AddMinutes(5);
        var replay = await service.StartAsync(command);

        Assert.Equal(HostedCheckoutOutcome.RequiresCustomerAction, first.Outcome);
        Assert.Equal(HostedCheckoutOutcome.RequiresCustomerAction, replay.Outcome);
        Assert.True(replay.Replayed);
        Assert.Equal(first.OrderId, replay.OrderId);
        Assert.Equal(1, gateway.InitializeCalls);
        Assert.Equal(2, await database.StockAsync(1));
        Assert.Equal(1, await database.Context.Orders.CountAsync());
        Assert.Equal(
            originalExpiry,
            await database.Context.InventoryReservations
                .Select(reservation => reservation.ExpiresAt)
                .SingleAsync());
    }

    [Fact]
    public async Task ReplayWithDifferentPayload_ConflictsWithoutNewStockEffect()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 4, price: 50m);
        var gateway = FakePaymentGateway.Success();
        var service = database.CreateService(gateway);
        var key = "hosted-conflict-0001";
        await service.StartAsync(Command(key));

        var conflict = await service.StartAsync(
            Command(key) with
            {
                Lines = [new InventoryReservationLine(1, 2)]
            });

        Assert.Equal(HostedCheckoutOutcome.Conflict, conflict.Outcome);
        Assert.Equal(3, await database.StockAsync(1));
        Assert.Equal(1, await database.Context.Orders.CountAsync());
        Assert.Equal(1, await database.Context.InventoryReservations.CountAsync());
        Assert.Equal(1, gateway.InitializeCalls);
    }

    [Fact]
    public async Task DifferentIdempotencyKeys_WithSamePayloadCreateIndependentAttempts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 4, price: 50m);
        var gateway = FakePaymentGateway.Success();
        var service = database.CreateService(gateway);

        var first = await service.StartAsync(Command("hosted-independent-01"));
        var second = await service.StartAsync(Command("hosted-independent-02"));

        Assert.Equal(HostedCheckoutOutcome.RequiresCustomerAction, first.Outcome);
        Assert.Equal(HostedCheckoutOutcome.RequiresCustomerAction, second.Outcome);
        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(2, await database.Context.Orders.CountAsync());
        Assert.Equal(2, await database.Context.PaymentAttempts.CountAsync());
        Assert.Equal(
            2,
            await database.Context.PaymentAttempts
                .Select(attempt => attempt.ConversationId)
                .Distinct()
                .CountAsync());
        Assert.Equal(2, await database.StockAsync(1));
    }

    [Fact]
    public async Task DefiniteInitializationFailure_CancelsOrderAndRestoresStock()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 50m);
        var gateway = new FakePaymentGateway(
            new PaymentInitializationResult(
                PaymentGatewayOutcome.Failed,
                PaymentGatewayErrorCode.Declined));
        var service = database.CreateService(gateway);

        var result = await service.StartAsync(Command("hosted-declined-0001"));

        Assert.Equal(HostedCheckoutOutcome.Declined, result.Outcome);
        Assert.Equal(OrderStatuses.Cancelled, result.OrderStatus);
        Assert.Equal(PaymentStatuses.Failed, result.PaymentStatus);
        Assert.Equal(PaymentAttemptStatuses.Failed, result.AttemptStatus);
        Assert.Equal(3, await database.StockAsync(1));
        database.Context.ChangeTracker.Clear();
        var session = await database.SessionQuery().SingleAsync();
        Assert.Equal(InventoryReservationStatuses.Committed, session.InventoryReservation.Status);
        Assert.Equal(OrderStatuses.Cancelled, session.Order.Status);
        Assert.Equal(PaymentStatuses.Failed, session.Order.Payment!.Status);
    }

    [Fact]
    public async Task AmbiguousProviderFailure_LeavesPendingOrderAndUnknownAttemptForReconciliation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 50m);
        var gateway = new FakePaymentGateway(
            new PaymentInitializationResult(
                PaymentGatewayOutcome.Failed,
                PaymentGatewayErrorCode.ProviderUnavailable));
        var service = database.CreateService(gateway);

        var result = await service.StartAsync(Command("hosted-unknown-00001"));

        Assert.Equal(HostedCheckoutOutcome.PendingReconciliation, result.Outcome);
        Assert.Equal(OrderStatuses.Pending, result.OrderStatus);
        Assert.Equal(PaymentStatuses.Pending, result.PaymentStatus);
        Assert.Equal(PaymentAttemptStatuses.Unknown, result.AttemptStatus);
        Assert.Equal(2, await database.StockAsync(1));
        Assert.Equal(1, gateway.InitializeCalls);

        var replay = await service.StartAsync(Command("hosted-unknown-00001"));
        Assert.True(replay.Replayed);
        Assert.Equal(HostedCheckoutOutcome.PendingReconciliation, replay.Outcome);
        Assert.Equal(1, gateway.InitializeCalls);
        Assert.Equal(2, await database.StockAsync(1));
    }

    [Fact]
    public async Task ConcurrentReplay_CreatesOneOrderAndOneStockEffect()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetProductAsync(1, stock: 3, price: 50m);
        var gateway = FakePaymentGateway.Success();
        var command = Command("hosted-race-replay-01");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<HostedCheckoutResult> Start()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await CreateService(context, gateway, new MutableTimeProvider(Now))
                .StartAsync(command);
        }

        var first = Start();
        var second = Start();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(
            results,
            result => Assert.Equal(
                HostedCheckoutOutcome.RequiresCustomerAction,
                result.Outcome));
        Assert.Contains(results, result => result.Replayed);
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Orders.CountAsync());
        Assert.Equal(1, await verification.InventoryReservations.CountAsync());
        Assert.Equal(1, await verification.Set<HostedCheckoutSession>().CountAsync());
        Assert.Equal(
            2,
            await verification.Products
                .Where(product => product.Id == 1)
                .Select(product => product.Stock)
                .SingleAsync());
    }

    private static HostedCheckoutCommand Command(string key) => new(
        key,
        [new InventoryReservationLine(1, 1)],
        new HostedCheckoutCustomer(
            "Test Customer",
            "customer@example.test",
            "+905550000000",
            "Test Mahallesi Test Sokak No 1",
            "Istanbul",
            "34000"),
        new Uri("https://api.example.test/payment/callback"),
        new Uri("https://shop.example.test/payment/return"),
        LegalAcceptances: CreateLegalAcceptances());

    private static HostedCheckoutService CreateService(
        AutoPartsDbContext context,
        IPaymentGateway gateway,
        TimeProvider clock)
    {
        return new HostedCheckoutService(
            context,
            new InventoryReservationService(context, clock),
            new OrderLifecycleService(context),
            gateway,
            new LegalConsentService(context, new LegalCheckoutOptions()),
            new HostedCheckoutOptions { ReservationLifetime = TimeSpan.FromMinutes(15) },
            clock);
    }

    private static List<LegalAcceptanceDto> CreateLegalAcceptances() =>
    [
        CreateLegalAcceptance(
            LegalDocumentTypes.PreliminaryInformation,
            "test-v1",
            "Test preliminary information"),
        CreateLegalAcceptance(
            LegalDocumentTypes.DistanceSalesAgreement,
            "test-v1",
            "Test distance sales agreement")
    ];

    private static LegalAcceptanceDto CreateLegalAcceptance(
        string documentType,
        string version,
        string content) => new()
        {
            DocumentType = documentType,
            Version = version,
            ContentSha256 = LegalDocumentVersion.ComputeContentHash(
                LegalDocumentVersion.CanonicalizeContent(content)),
            Accepted = true
        };

    private static async Task SeedLegalDocumentsAsync(AutoPartsDbContext context)
    {
        foreach (var (type, title, content) in new[]
                 {
                     (LegalDocumentTypes.PreliminaryInformation, "Preliminary", "Test preliminary information"),
                     (LegalDocumentTypes.DistanceSalesAgreement, "Distance sales", "Test distance sales agreement")
                 })
        {
            var document = LegalDocumentVersion.CreateDraft(
                type,
                "test-v1",
                title,
                content,
                1,
                Now.UtcDateTime);
            context.LegalDocumentVersions.Add(document);
            await context.SaveChangesAsync();
            document.Publish(1, Now.UtcDateTime);
            await context.SaveChangesAsync();
        }
    }

    private sealed class FakePaymentGateway(PaymentInitializationResult result) : IPaymentGateway
    {
        private int _initializeCalls;

        public string ProviderName => "TestHosted";
        public bool IsEnabled => true;
        public int InitializeCalls => Volatile.Read(ref _initializeCalls);
        public Action<InitializePaymentCommand>? OnInitialize { get; init; }

        public static FakePaymentGateway Success() => new(
            new PaymentInitializationResult(
                PaymentGatewayOutcome.Succeeded,
                PaymentGatewayErrorCode.None,
                HostedPaymentPageUri: new Uri("https://pay.example.test/checkout/replay"),
                HostedPaymentToken: "opaque-token"));

        public Task<PaymentInitializationResult> InitializeAsync(
            InitializePaymentCommand command,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _initializeCalls);
            OnInitialize?.Invoke(command);
            return Task.FromResult(result);
        }

        public Task<PaymentConfirmationResult> ConfirmAsync(
            ConfirmPaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentRetrievalResult> RetrieveAsync(
            RetrievePaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentWebhookVerificationResult> VerifyWebhookAsync(
            VerifyPaymentWebhookCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentRefundResult> RefundAsync(
            RefundPaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PaymentInquiryResult> InquireAsync(
            InquirePaymentCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HostedCheckoutTestDbContext(
        DbContextOptions<AutoPartsDbContext> options) : AutoPartsDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureHostedCheckout();
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(HostedCheckoutTestDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public HostedCheckoutTestDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new HostedCheckoutTestDbContext(options);
            await context.Database.EnsureCreatedAsync();
            await SeedLegalDocumentsAsync(context);
            return new TestDatabase(context, connection);
        }

        public HostedCheckoutService CreateService(
            IPaymentGateway gateway,
            TimeProvider? clock = null) =>
            HostedCheckoutServiceTests.CreateService(
                Context,
                gateway,
                clock ?? new MutableTimeProvider(Now));

        public async Task SetProductAsync(int productId, int stock, decimal price)
        {
            var product = await Context.Products.FindAsync(productId) ??
                throw new InvalidOperationException("Seed product not found.");
            product.Stock = stock;
            product.Price = price;
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public Task<int> StockAsync(int productId) => Context.Products
            .AsNoTracking()
            .Where(product => product.Id == productId)
            .Select(product => product.Stock)
            .SingleAsync();

        public IQueryable<HostedCheckoutSession> SessionQuery() =>
            Context.Set<HostedCheckoutSession>()
                .Include(session => session.InventoryReservation)
                .Include(session => session.Order)
                    .ThenInclude(order => order.Payment)
                        .ThenInclude(payment => payment!.Attempts);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class SharedTestDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _keeper;

        private SharedTestDatabase(string connectionString, SqliteConnection keeper)
        {
            _connectionString = connectionString;
            _keeper = keeper;
        }

        public static async Task<SharedTestDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=file:hosted-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=5;Pooling=False";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            await using var context = CreateContext(connectionString, keeper);
            await context.Database.EnsureCreatedAsync();
            await SeedLegalDocumentsAsync(context);
            return new SharedTestDatabase(connectionString, keeper);
        }

        public HostedCheckoutTestDbContext CreateContext() =>
            CreateContext(_connectionString, new SqliteConnection(_connectionString));

        public async Task SetProductAsync(int productId, int stock, decimal price)
        {
            await using var context = CreateContext();
            var product = await context.Products.FindAsync(productId) ??
                throw new InvalidOperationException("Seed product not found.");
            product.Stock = stock;
            product.Price = price;
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();

        private static HostedCheckoutTestDbContext CreateContext(
            string connectionString,
            SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            return new HostedCheckoutTestDbContext(options);
        }
    }
}
