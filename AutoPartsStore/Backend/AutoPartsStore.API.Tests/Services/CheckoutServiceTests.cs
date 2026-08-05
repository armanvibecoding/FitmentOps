using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class CheckoutServiceTests
{
    [Fact]
    public async Task CreateOrder_UsesServerPriceAndDeductsStockAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Price = 149.90m;
        product.Stock = 5;
        database.Context.Users.Add(new User
        {
            Id = 42,
            Email = "user42@example.com",
            Password = "test-password-hash",
            FullName = "Test User",
            Role = "User"
        });
        await database.Context.SaveChangesAsync();

        var service = CreateCheckoutService(database.Context);
        var result = await service.CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-success-0001",
            userId: 42);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
        Assert.NotNull(result.Order);
        Assert.Equal(299.80m, result.Order.TotalAmount);
        Assert.Equal(42, result.Order.UserId);
        Assert.Equal(PaymentStatuses.Pending, result.Order.Payment?.Status);
        Assert.Equal(PaymentMethods.PayAtDelivery, result.Order.Payment?.Method);

        var serializedOrder = JsonSerializer.Serialize(
            result.Order,
            new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
        Assert.DoesNotContain("CheckoutIdempotencyKey", serializedOrder);
        Assert.DoesNotContain("IdempotencyKey", serializedOrder);
        Assert.DoesNotContain("ProviderPaymentId", serializedOrder);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(3, (await database.Context.Products.FindAsync(1))!.Stock);
    }

    [Fact]
    public async Task CreateOrder_RetryWithSameKeyDoesNotDeductStockTwice()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var service = CreateCheckoutService(database.Context);
        var request = CreateRequest((1, 2));

        var first = await service.CreateOrderAsync(request, "checkout-retry-00001", null);
        var replay = await service.CreateOrderAsync(request, "checkout-retry-00001", null);

        Assert.Equal(CheckoutOutcome.Created, first.Outcome);
        Assert.Equal(CheckoutOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Order?.Id, replay.Order?.Id);
        Assert.Equal(1, await database.Context.Orders.CountAsync());
        Assert.Equal(1, await database.Context.Payments.CountAsync());
        Assert.Equal(3, (await database.Context.Products.FindAsync(1))!.Stock);
    }

    [Fact]
    public async Task CreateOrder_SameKeyWithDifferentPayloadReturnsConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var service = CreateCheckoutService(database.Context);
        await service.CreateOrderAsync(
            CreateRequest((1, 1)),
            "checkout-conflict-01",
            null);

        var conflict = await service.CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-conflict-01",
            null);

        Assert.Equal(CheckoutOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Orders.CountAsync());
        Assert.Equal(4, (await database.Context.Products.FindAsync(1))!.Stock);
    }

    [Fact]
    public async Task CreateOrder_InsufficientSecondItemRollsBackFirstStockUpdate()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstProduct = await database.Context.Products.SingleAsync(item => item.Id == 1);
        var secondProduct = await database.Context.Products.SingleAsync(item => item.Id == 2);
        firstProduct.Stock = 5;
        secondProduct.Stock = 0;
        await database.Context.SaveChangesAsync();

        var service = CreateCheckoutService(database.Context);
        var result = await service.CreateOrderAsync(
            CreateRequest((1, 2), (2, 1)),
            "checkout-rollback-001",
            null);

        Assert.Equal(CheckoutOutcome.InventoryUnavailable, result.Outcome);
        Assert.Equal(0, await database.Context.Orders.CountAsync());
        Assert.Equal(0, await database.Context.Payments.CountAsync());
        Assert.Equal(5, (await database.Context.Products.FindAsync(1))!.Stock);
        Assert.Equal(0, (await database.Context.Products.FindAsync(2))!.Stock);
    }

    [Fact]
    public async Task CancelOrder_RestoresStockExactlyOnceAndCancelsPendingPayment()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var checkoutService = CreateCheckoutService(database.Context);
        var checkout = await checkoutService.CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-cancel-00001",
            null);

        var lifecycleService = new OrderLifecycleService(database.Context);
        var cancelled = await lifecycleService.UpdateOrderStatusAsync(
            checkout.Order!.Id,
            OrderStatuses.Cancelled);
        var repeatedCancellation = await lifecycleService.UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Cancelled);

        Assert.Equal(OrderLifecycleOutcome.Updated, cancelled.Outcome);
        Assert.Equal(OrderLifecycleOutcome.Unchanged, repeatedCancellation.Outcome);

        database.Context.ChangeTracker.Clear();
        var storedOrder = await database.Context.Orders
            .Include(order => order.Payment)
            .SingleAsync(order => order.Id == checkout.Order.Id);
        Assert.Equal(OrderStatuses.Cancelled, storedOrder.Status);
        Assert.Equal(PaymentStatuses.Cancelled, storedOrder.Payment?.Status);
        Assert.Equal(5, (await database.Context.Products.FindAsync(1))!.Stock);
    }

    [Fact]
    public async Task PaidOrder_CannotBeCancelledWithoutRefund()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var checkoutService = CreateCheckoutService(database.Context);
        var checkout = await checkoutService.CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-paid-000001",
            null);

        var lifecycleService = new OrderLifecycleService(database.Context);
        var paid = await lifecycleService.MarkManualPaymentPaidAsync(checkout.Order!.Payment!.Id);
        var repeatedPaid = await lifecycleService.MarkManualPaymentPaidAsync(checkout.Order.Payment.Id);
        var cancellation = await lifecycleService.UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Cancelled);

        Assert.Equal(PaymentLifecycleOutcome.Updated, paid.Outcome);
        Assert.Equal(PaymentLifecycleOutcome.Unchanged, repeatedPaid.Outcome);
        Assert.Equal(OrderLifecycleOutcome.InvalidTransition, cancellation.Outcome);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(3, (await database.Context.Products.FindAsync(1))!.Stock);
        Assert.Equal(
            OrderStatuses.Pending,
            (await database.Context.Orders.FindAsync(checkout.Order.Id))!.Status);
    }

    [Theory]
    [InlineData(PaymentStatuses.PartiallyRefunded)]
    [InlineData(PaymentStatuses.Refunded)]
    public async Task RefundedPaymentStates_BlockCancellationAndStockRestore(
        string paymentStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var checkout = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest((1, 2)),
            $"checkout-{paymentStatus.ToLowerInvariant()}-1",
            null);
        var payment = await database.Context.Payments.FindAsync(
            checkout.Order!.Payment!.Id);
        payment!.Status = paymentStatus;
        await database.Context.SaveChangesAsync();

        var result = await new OrderLifecycleService(database.Context).UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Cancelled);

        Assert.Equal(OrderLifecycleOutcome.InvalidTransition, result.Outcome);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(3, (await database.Context.Products.FindAsync(1))!.Stock);
        var storedOrder = await database.Context.Orders
            .Include(order => order.Payment)
            .SingleAsync(order => order.Id == checkout.Order.Id);
        Assert.Equal(OrderStatuses.Pending, storedOrder.Status);
        Assert.Equal(paymentStatus, storedOrder.Payment!.Status);
    }

    [Fact]
    public async Task DirectOrderShippingAndDeliveryTransitionsAreRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var checkout = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest((1, 1)),
            "checkout-direct-ship-1",
            null);
        var lifecycle = new OrderLifecycleService(database.Context);
        var processing = await lifecycle.UpdateOrderStatusAsync(
            checkout.Order!.Id,
            OrderStatuses.Processing);
        var directShipping = await lifecycle.UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Shipped);

        database.Context.ChangeTracker.Clear();
        var order = await database.Context.Orders.FindAsync(checkout.Order.Id);
        order!.Status = OrderStatuses.Shipped;
        await database.Context.SaveChangesAsync();
        var directDelivery = await lifecycle.UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Delivered);

        Assert.Equal(OrderLifecycleOutcome.Updated, processing.Outcome);
        Assert.Equal(OrderLifecycleOutcome.InvalidTransition, directShipping.Outcome);
        Assert.Equal(OrderLifecycleOutcome.InvalidTransition, directDelivery.Outcome);
    }

    [Fact]
    public async Task ConcurrentCancellation_RestoresStockOnlyOnce()
    {
        await using var database = await TestDatabase.CreateFileBackedAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();
        var checkout = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-concurrent-cancel-1",
            null);
        var orderId = checkout.Order!.Id;

        await using var firstContext = database.CreateSiblingContext();
        await using var secondContext = database.CreateSiblingContext();
        var results = await Task.WhenAll(
            new OrderLifecycleService(firstContext).UpdateOrderStatusAsync(
                orderId,
                OrderStatuses.Cancelled),
            new OrderLifecycleService(secondContext).UpdateOrderStatusAsync(
                orderId,
                OrderStatuses.Cancelled));

        Assert.Single(results, result => result.Outcome == OrderLifecycleOutcome.Updated);
        Assert.Single(results, result => result.Outcome == OrderLifecycleOutcome.Unchanged);
        await using var verification = database.CreateSiblingContext();
        Assert.Equal(5, (await verification.Products.FindAsync(1))!.Stock);
        Assert.Equal(
            OrderStatuses.Cancelled,
            (await verification.Orders.FindAsync(orderId))!.Status);
    }

    [Fact]
    public async Task ConcurrentCancellationAndShipmentCreation_CannotBothCommit()
    {
        await using var database = await TestDatabase.CreateFileBackedAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();
        var checkout = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-cancel-shipment-race-1",
            null);
        var orderId = checkout.Order!.Id;
        var orderItemId = checkout.Order.OrderItems.Single().Id;

        await using var cancellationContext = database.CreateSiblingContext();
        await using var fulfillmentContext = database.CreateSiblingContext();
        var cancellationTask = new OrderLifecycleService(cancellationContext)
            .UpdateOrderStatusAsync(orderId, OrderStatuses.Cancelled);
        var shipmentTask = new FulfillmentService(fulfillmentContext).CreateShipmentAsync(
            orderId,
            "shipment-cancel-race-1",
            [new ShipmentLineRequest(orderItemId, 2)],
            DateTimeOffset.UtcNow);
        await Task.WhenAll(cancellationTask, shipmentTask);

        var cancellationResult = await cancellationTask;
        var shipmentResult = await shipmentTask;
        var cancellationCommitted = cancellationResult.Outcome == OrderLifecycleOutcome.Updated;
        var shipmentCommitted = shipmentResult.Outcome == FulfillmentOutcome.Created;
        Assert.NotEqual(cancellationCommitted, shipmentCommitted);

        await using var verification = database.CreateSiblingContext();
        var storedOrder = await verification.Orders.FindAsync(orderId);
        var storedStock = (await verification.Products.FindAsync(1))!.Stock;
        var shipmentCount = await verification.Shipments.CountAsync(
            shipment => shipment.OrderId == orderId);
        if (cancellationCommitted)
        {
            Assert.Equal(OrderStatuses.Cancelled, storedOrder!.Status);
            Assert.Equal(5, storedStock);
            Assert.Equal(0, shipmentCount);
        }
        else
        {
            Assert.Equal(OrderStatuses.Processing, storedOrder!.Status);
            Assert.Equal(3, storedStock);
            Assert.Equal(1, shipmentCount);
        }
    }

    [Fact]
    public async Task ActiveShipment_BlocksOrderCancellationAndDoesNotRestoreStock()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.Context.Products.SingleAsync(item => item.Id == 1);
        product.Stock = 5;
        await database.Context.SaveChangesAsync();

        var checkoutService = CreateCheckoutService(database.Context);
        var checkout = await checkoutService.CreateOrderAsync(
            CreateRequest((1, 2)),
            "checkout-shipment-0001",
            null);
        var orderItemId = checkout.Order!.OrderItems.Single().Id;
        var fulfillmentService = new FulfillmentService(database.Context);
        var shipment = await fulfillmentService.CreateShipmentAsync(
            checkout.Order.Id,
            "shipment-cancel-gate-1",
            [new ShipmentLineRequest(orderItemId, 2)],
            DateTimeOffset.UtcNow);

        var lifecycleService = new OrderLifecycleService(database.Context);
        var cancellation = await lifecycleService.UpdateOrderStatusAsync(
            checkout.Order.Id,
            OrderStatuses.Cancelled);

        Assert.Equal(FulfillmentOutcome.Created, shipment.Outcome);
        Assert.Equal(OrderLifecycleOutcome.InvalidTransition, cancellation.Outcome);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(3, (await database.Context.Products.FindAsync(1))!.Stock);
        Assert.Equal(
            OrderStatuses.Processing,
            (await database.Context.Orders.FindAsync(checkout.Order.Id))!.Status);
    }

    private static CreateOrderDto CreateRequest(params (int ProductId, int Quantity)[] items)
    {
        return new CreateOrderDto
        {
            CustomerName = "Checkout Test",
            CustomerEmail = "checkout@example.com",
            CustomerPhone = "+905551112233",
            ShippingAddress = "Test Mahallesi Test Sokak No 1",
            City = "İstanbul",
            PostalCode = "34000",
            PaymentMethod = PaymentMethods.PayAtDelivery,
            Items = items.Select(item => new OrderItemDto
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity
            }).ToList(),
            LegalAcceptances = CreateLegalAcceptances()
        };
    }

    private static CheckoutService CreateCheckoutService(AutoPartsDbContext context) =>
        new(context, new LegalConsentService(context, new LegalCheckoutOptions()));

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
                DateTime.UtcNow);
            context.LegalDocumentVersions.Add(document);
            await context.SaveChangesAsync();
            document.Publish(1, DateTime.UtcNow);
            await context.SaveChangesAsync();
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly string? _databasePath;

        private TestDatabase(
            AutoPartsDbContext context,
            SqliteConnection? connection = null,
            string? databasePath = null)
        {
            Context = context;
            _connection = connection;
            _databasePath = databasePath;
        }

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
            await SeedLegalDocumentsAsync(context);

            return new TestDatabase(context, connection);
        }

        public static async Task<TestDatabase> CreateFileBackedAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"checkout-{Guid.NewGuid():N}.db");
            var context = CreateContext(path);
            await context.Database.EnsureCreatedAsync();
            await SeedLegalDocumentsAsync(context);
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            return new TestDatabase(context, databasePath: path);
        }

        public AutoPartsDbContext CreateSiblingContext()
        {
            return _databasePath == null
                ? throw new InvalidOperationException("Sibling contexts require a file-backed database.")
                : CreateContext(_databasePath);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }

            if (_databasePath != null && File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        private static AutoPartsDbContext CreateContext(string path)
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=15;Pooling=False")
                .Options;
            return new AutoPartsDbContext(options);
        }
    }
}
