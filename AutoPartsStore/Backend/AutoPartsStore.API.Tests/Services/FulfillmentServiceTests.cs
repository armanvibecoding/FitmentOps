using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class FulfillmentServiceTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateShipment_AllowsMultiplePartialShipmentsWithoutOvershipping()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 5);
        var service = new FulfillmentService(database.Context);

        var first = await service.CreateShipmentAsync(
            orderId,
            "shipment-first",
            [new ShipmentLineRequest(orderItemId, 2)],
            CreatedAt);
        var second = await service.CreateShipmentAsync(
            orderId,
            "shipment-second",
            [new ShipmentLineRequest(orderItemId, 3)],
            CreatedAt.AddMinutes(1));
        var exceeding = await service.CreateShipmentAsync(
            orderId,
            "shipment-third",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt.AddMinutes(2));

        Assert.Equal(FulfillmentOutcome.Created, first.Outcome);
        Assert.Equal(FulfillmentOutcome.Created, second.Outcome);
        Assert.Equal(FulfillmentOutcome.Conflict, exceeding.Outcome);
        Assert.Equal(5, await database.Context.Set<ShipmentItem>().SumAsync(item => item.Quantity));
    }

    [Fact]
    public async Task CreateShipment_SameKeyAndCanonicalPayloadIsReplay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, firstItemId, secondItemId) = await database.AddOrderWithTwoItemsAsync();
        var service = new FulfillmentService(database.Context);

        var first = await service.CreateShipmentAsync(
            orderId,
            " shipment-replay ",
            [
                new ShipmentLineRequest(secondItemId, 1),
                new ShipmentLineRequest(firstItemId, 2)
            ],
            CreatedAt);
        var replay = await service.CreateShipmentAsync(
            orderId,
            "shipment-replay",
            [
                new ShipmentLineRequest(firstItemId, 2),
                new ShipmentLineRequest(secondItemId, 1)
            ],
            CreatedAt.AddHours(1));

        Assert.Equal(FulfillmentOutcome.Created, first.Outcome);
        Assert.Equal(FulfillmentOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Shipment!.Id, replay.Shipment!.Id);
        Assert.Equal(1, await database.Context.Set<Shipment>().CountAsync());
    }

    [Fact]
    public async Task CreateShipment_SameKeyWithDifferentPayloadIsConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 3);
        var service = new FulfillmentService(database.Context);
        await service.CreateShipmentAsync(
            orderId,
            "shipment-conflict",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);

        var conflict = await service.CreateShipmentAsync(
            orderId,
            "shipment-conflict",
            [new ShipmentLineRequest(orderItemId, 2)],
            CreatedAt);

        Assert.Equal(FulfillmentOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<Shipment>().CountAsync());
    }

    [Fact]
    public async Task CreateShipment_OrderItemFromAnotherOrderIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (firstOrderId, _) = await database.AddOrderAsync(quantity: 1);
        var (_, otherOrderItemId) = await database.AddOrderAsync(quantity: 1);

        var result = await new FulfillmentService(database.Context).CreateShipmentAsync(
            firstOrderId,
            "shipment-wrong-order",
            [new ShipmentLineRequest(otherOrderItemId, 1)],
            CreatedAt);

        Assert.Equal(FulfillmentOutcome.NotFound, result.Outcome);
        Assert.Empty(await database.Context.Set<Shipment>().ToListAsync());
    }

    [Fact]
    public async Task CreateShipment_CancelledOrderIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 1);
        var order = await database.Context.Orders.FindAsync(orderId);
        order!.Status = OrderStatuses.Cancelled;
        await database.Context.SaveChangesAsync();

        var result = await new FulfillmentService(database.Context).CreateShipmentAsync(
            orderId,
            "shipment-cancelled-order",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);

        Assert.Equal(FulfillmentOutcome.Conflict, result.Outcome);
        Assert.Empty(await database.Context.Set<Shipment>().ToListAsync());
    }

    [Fact]
    public async Task CreateShipment_CancelledReservationCanBeAllocatedAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 1);
        var service = new FulfillmentService(database.Context);
        var first = await service.CreateShipmentAsync(
            orderId,
            "shipment-cancelled",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);
        var cancelled = await service.TransitionAsync(
            first.Shipment!.Id,
            ShipmentStatuses.Cancelled,
            CreatedAt.AddMinutes(1));

        var replacement = await service.CreateShipmentAsync(
            orderId,
            "shipment-replacement",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt.AddMinutes(2));

        Assert.Equal(FulfillmentOutcome.Updated, cancelled.Outcome);
        Assert.Equal(FulfillmentOutcome.Created, replacement.Outcome);
    }

    [Fact]
    public async Task CreateShipment_AdvancesPendingOrderToProcessing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 1);

        var result = await new FulfillmentService(database.Context).CreateShipmentAsync(
            orderId,
            "shipment-processing-order",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);

        Assert.Equal(FulfillmentOutcome.Created, result.Outcome);
        Assert.Equal(
            OrderStatuses.Processing,
            (await database.Context.Orders.FindAsync(orderId))!.Status);
    }

    [Fact]
    public async Task ShipmentAggregate_AdvancesOrderOnlyAfterEveryQuantityCompletes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 2);
        var service = new FulfillmentService(database.Context);
        var first = await service.CreateShipmentAsync(
            orderId,
            "shipment-aggregate-first",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);
        var second = await service.CreateShipmentAsync(
            orderId,
            "shipment-aggregate-second",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);
        await service.TransitionAsync(
            first.Shipment!.Id,
            ShipmentStatuses.ReadyToShip,
            CreatedAt.AddMinutes(1));
        await service.TransitionAsync(
            second.Shipment!.Id,
            ShipmentStatuses.ReadyToShip,
            CreatedAt.AddMinutes(1));

        await service.TransitionAsync(
            first.Shipment.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            "ARAS",
            "AGG-1");
        Assert.Equal(
            OrderStatuses.Processing,
            (await database.Context.Orders.FindAsync(orderId))!.Status);

        await service.TransitionAsync(
            second.Shipment.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            "ARAS",
            "AGG-2");
        Assert.Equal(
            OrderStatuses.Shipped,
            (await database.Context.Orders.FindAsync(orderId))!.Status);

        await service.TransitionAsync(
            first.Shipment.Id,
            ShipmentStatuses.Delivered,
            CreatedAt.AddMinutes(3));
        Assert.Equal(
            OrderStatuses.Shipped,
            (await database.Context.Orders.FindAsync(orderId))!.Status);

        await service.TransitionAsync(
            second.Shipment.Id,
            ShipmentStatuses.Delivered,
            CreatedAt.AddMinutes(3));
        Assert.Equal(
            OrderStatuses.Delivered,
            (await database.Context.Orders.FindAsync(orderId))!.Status);
    }

    [Fact]
    public async Task Transition_RequiresTrackingToShipAndDeliveredIsTerminal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var shipment = await database.AddShipmentAsync();
        var service = new FulfillmentService(database.Context);
        await service.TransitionAsync(
            shipment.Id,
            ShipmentStatuses.ReadyToShip,
            CreatedAt.AddMinutes(1));

        var withoutTracking = await service.TransitionAsync(
            shipment.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2));
        var shipped = await service.TransitionAsync(
            shipment.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            " aras ",
            " tr-123 ");
        var delivered = await service.TransitionAsync(
            shipment.Id,
            ShipmentStatuses.Delivered,
            CreatedAt.AddMinutes(3));
        var regression = await service.TransitionAsync(
            shipment.Id,
            ShipmentStatuses.ReadyToShip,
            CreatedAt.AddMinutes(4));

        Assert.Equal(FulfillmentOutcome.InvalidRequest, withoutTracking.Outcome);
        Assert.Equal(FulfillmentOutcome.Updated, shipped.Outcome);
        Assert.Equal("ARAS", shipped.Shipment!.Carrier);
        Assert.Equal("TR-123", shipped.Shipment.TrackingNumber);
        Assert.Equal(FulfillmentOutcome.Updated, delivered.Outcome);
        Assert.Equal(FulfillmentOutcome.Conflict, regression.Outcome);
        Assert.Equal(ShipmentStatuses.Delivered, regression.Shipment!.Status);
    }

    [Fact]
    public async Task Transition_DuplicateTrackingWithinCarrierConflictsButOtherCarrierSucceeds()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = await database.AddShipmentAsync("tracking-first");
        var second = await database.AddShipmentAsync("tracking-second");
        var third = await database.AddShipmentAsync("tracking-third");
        var service = new FulfillmentService(database.Context);
        foreach (var shipment in new[] { first, second, third })
        {
            await service.TransitionAsync(
                shipment.Id,
                ShipmentStatuses.ReadyToShip,
                CreatedAt.AddMinutes(1));
        }

        var firstShipped = await service.TransitionAsync(
            first.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            "MNG",
            "ABC123");
        var duplicate = await service.TransitionAsync(
            second.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            "mng",
            "abc123");
        var otherCarrier = await service.TransitionAsync(
            third.Id,
            ShipmentStatuses.Shipped,
            CreatedAt.AddMinutes(2),
            "PTT",
            "ABC123");

        Assert.Equal(FulfillmentOutcome.Updated, firstShipped.Outcome);
        Assert.Equal(FulfillmentOutcome.Conflict, duplicate.Outcome);
        Assert.Equal(ShipmentStatuses.ReadyToShip, duplicate.Shipment!.Status);
        Assert.Equal(FulfillmentOutcome.Updated, otherCarrier.Outcome);
    }

    [Fact]
    public async Task ConcurrentContexts_CannotAllocateMoreThanOrderedQuantity()
    {
        await using var database = await TestDatabase.CreateFileBackedAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 1);
        await using var firstContext = database.CreateSiblingContext();
        await using var secondContext = database.CreateSiblingContext();

        var results = await Task.WhenAll(
            new FulfillmentService(firstContext).CreateShipmentAsync(
                orderId,
                "race-first",
                [new ShipmentLineRequest(orderItemId, 1)],
                CreatedAt),
            new FulfillmentService(secondContext).CreateShipmentAsync(
                orderId,
                "race-second",
                [new ShipmentLineRequest(orderItemId, 1)],
                CreatedAt));

        await using var verification = database.CreateSiblingContext();
        var allocated = await verification.Set<ShipmentItem>().SumAsync(item => item.Quantity);
        Assert.Equal(1, allocated);
        Assert.Single(results, result => result.Outcome == FulfillmentOutcome.Created);
        Assert.Single(results, result => result.Outcome == FulfillmentOutcome.Conflict);
    }

    [Fact]
    public async Task ConcurrentStaleTransition_PreservesFirstWriter()
    {
        await using var database = await TestDatabase.CreateFileBackedAsync();
        var shipment = await database.AddShipmentAsync();
        await using var firstContext = database.CreateSiblingContext();
        await using var staleContext = database.CreateSiblingContext();
        await firstContext.Set<Shipment>().SingleAsync(item => item.Id == shipment.Id);
        await staleContext.Set<Shipment>().SingleAsync(item => item.Id == shipment.Id);

        var first = await new FulfillmentService(firstContext).TransitionAsync(
            shipment.Id,
            ShipmentStatuses.LabelPending,
            CreatedAt.AddMinutes(1));
        var stale = await new FulfillmentService(staleContext).TransitionAsync(
            shipment.Id,
            ShipmentStatuses.Cancelled,
            CreatedAt.AddMinutes(2));

        Assert.Equal(FulfillmentOutcome.Updated, first.Outcome);
        Assert.Equal(FulfillmentOutcome.Conflict, stale.Outcome);
        Assert.Equal(ShipmentStatuses.LabelPending, stale.Shipment!.Status);
    }

    [Fact]
    public async Task CreateShipment_JoinsAmbientTransaction_AndCallerOwnsRollback()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (orderId, orderItemId) = await database.AddOrderAsync(quantity: 1);
        await using var transaction = await database.Context.Database.BeginTransactionAsync();

        var result = await new FulfillmentService(database.Context).CreateShipmentAsync(
            orderId,
            "ambient-shipment",
            [new ShipmentLineRequest(orderItemId, 1)],
            CreatedAt);

        Assert.Equal(FulfillmentOutcome.Created, result.Outcome);
        Assert.Same(transaction, database.Context.Database.CurrentTransaction);

        await transaction.RollbackAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Empty(await database.Context.Shipments.ToListAsync());
        Assert.Equal(
            OrderStatuses.Pending,
            await database.Context.Orders
                .Where(order => order.Id == orderId)
                .Select(order => order.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task Transition_JoinsAmbientTransaction_AndCallerOwnsRollback()
    {
        await using var database = await TestDatabase.CreateAsync();
        var shipment = await database.AddShipmentAsync();
        await using var transaction = await database.Context.Database.BeginTransactionAsync();

        var result = await new FulfillmentService(database.Context).TransitionAsync(
            shipment.Id,
            ShipmentStatuses.LabelPending,
            CreatedAt.AddMinutes(1));

        Assert.Equal(FulfillmentOutcome.Updated, result.Outcome);
        Assert.Same(transaction, database.Context.Database.CurrentTransaction);

        await transaction.RollbackAsync();
        database.Context.ChangeTracker.Clear();

        Assert.Equal(
            ShipmentStatuses.Created,
            await database.Context.Shipments
                .Where(candidate => candidate.Id == shipment.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync());
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly string? _databasePath;

        private TestDatabase(
            FulfillmentTestDbContext context,
            SqliteConnection? connection = null,
            string? databasePath = null)
        {
            Context = context;
            _connection = connection;
            _databasePath = databasePath;
        }

        public FulfillmentTestDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = CreateContext(connection);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public static async Task<TestDatabase> CreateFileBackedAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"fulfillment-{Guid.NewGuid():N}.db");
            var context = CreateContext(path);
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            return new TestDatabase(context, databasePath: path);
        }

        public FulfillmentTestDbContext CreateSiblingContext()
        {
            return _databasePath == null
                ? throw new InvalidOperationException("Sibling contexts require a file-backed database.")
                : CreateContext(_databasePath);
        }

        public async Task<(int OrderId, int OrderItemId)> AddOrderAsync(int quantity)
        {
            var order = BuildOrder();
            var item = new OrderItem
            {
                ProductId = 1,
                Quantity = quantity,
                Price = 100m
            };
            order.OrderItems.Add(item);
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return (order.Id, item.Id);
        }

        public async Task<(int OrderId, int FirstItemId, int SecondItemId)> AddOrderWithTwoItemsAsync()
        {
            var order = BuildOrder();
            var first = new OrderItem { ProductId = 1, Quantity = 3, Price = 100m };
            var second = new OrderItem { ProductId = 2, Quantity = 2, Price = 50m };
            order.OrderItems.Add(first);
            order.OrderItems.Add(second);
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return (order.Id, first.Id, second.Id);
        }

        public async Task<Shipment> AddShipmentAsync(string? key = null)
        {
            var (orderId, orderItemId) = await AddOrderAsync(quantity: 1);
            var result = await new FulfillmentService(Context).CreateShipmentAsync(
                orderId,
                key ?? $"shipment-{Guid.NewGuid():N}",
                [new ShipmentLineRequest(orderItemId, 1)],
                CreatedAt);
            return result.Shipment!;
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

        private static FulfillmentTestDbContext CreateContext(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            return new FulfillmentTestDbContext(options);
        }

        private static FulfillmentTestDbContext CreateContext(string path)
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=15;Pooling=False")
                .Options;
            return new FulfillmentTestDbContext(options);
        }

        private static Order BuildOrder()
        {
            return new Order
            {
                OrderNumber = $"FUL-{Guid.NewGuid():N}",
                CustomerName = "Fulfillment Test",
                CustomerEmail = "fulfillment@example.com",
                CustomerPhone = "+905551112233",
                ShippingAddress = "Test Mahallesi Test Sokak No 1",
                City = "İstanbul",
                PostalCode = "34000",
                TotalAmount = 100m,
                Status = OrderStatuses.Pending,
                OrderDate = CreatedAt.UtcDateTime
            };
        }
    }

    private sealed class FulfillmentTestDbContext : AutoPartsDbContext
    {
        public FulfillmentTestDbContext(DbContextOptions<AutoPartsDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Shipment>();
            modelBuilder.Entity<ShipmentItem>();
        }
    }
}
