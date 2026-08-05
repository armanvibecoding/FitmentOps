using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class InventoryReservationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reserve_IsIdempotentAndChangedPayloadConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var service = database.CreateService();
        var expiry = Now.AddMinutes(15);

        var created = await service.ReserveAsync(
            "inventory-idempotent-1",
            [new InventoryReservationLine(1, 2)],
            expiry);
        var replay = await service.ReserveAsync(
            "inventory-idempotent-1",
            [new InventoryReservationLine(1, 2)],
            expiry);
        var conflict = await service.ReserveAsync(
            "inventory-idempotent-1",
            [new InventoryReservationLine(1, 1)],
            expiry);

        Assert.Equal(InventoryReservationOutcome.Created, created.Outcome);
        Assert.Equal(InventoryReservationOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.Reservation!.Id, replay.Reservation!.Id);
        Assert.Equal(InventoryReservationOutcome.Conflict, conflict.Outcome);
        Assert.Equal(3, await database.StockAsync(1));
    }

    [Fact]
    public async Task Reserve_RetryWithNewServerExpiry_ReplaysWithoutExtendingOriginalExpiry()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var service = database.CreateService();
        var originalExpiry = Now.AddMinutes(10);

        var created = await service.ReserveAsync(
            "inventory-expiry-retry",
            [new InventoryReservationLine(1, 2)],
            originalExpiry);
        var replay = await service.ReserveAsync(
            "inventory-expiry-retry",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(20));

        Assert.Equal(InventoryReservationOutcome.Created, created.Outcome);
        Assert.Equal(InventoryReservationOutcome.Replayed, replay.Outcome);
        Assert.Equal(originalExpiry.UtcDateTime, replay.Reservation!.ExpiresAt);
        Assert.Equal(3, await database.StockAsync(1));
    }

    [Fact]
    public async Task Reserve_SecondUnavailableLineRollsBackAllStock()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        await database.SetStockAsync(2, 0);

        var result = await database.CreateService().ReserveAsync(
            "inventory-rollback-1",
            [new InventoryReservationLine(1, 2), new InventoryReservationLine(2, 1)],
            Now.AddMinutes(15));

        Assert.Equal(InventoryReservationOutcome.InventoryUnavailable, result.Outcome);
        Assert.Equal(5, await database.StockAsync(1));
        Assert.Equal(0, await database.StockAsync(2));
        Assert.Equal(0, await database.Context.InventoryReservations.CountAsync());
    }

    [Fact]
    public async Task Release_RestoresStockExactlyOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var service = database.CreateService();
        var created = await service.ReserveAsync(
            "inventory-release-1",
            [new InventoryReservationLine(1, 3)],
            Now.AddMinutes(15));

        var released = await service.ReleaseAsync(created.Reservation!.Id);
        var replay = await service.ReleaseAsync(created.Reservation.Id);

        Assert.Equal(InventoryReservationOutcome.Updated, released.Outcome);
        Assert.Equal(InventoryReservationOutcome.Replayed, replay.Outcome);
        Assert.Equal(4, await database.StockAsync(1));
    }

    [Fact]
    public async Task Commit_KeepsReservedStockAndCannotBeReleased()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var order = await database.AddOrderAsync();
        var service = database.CreateService();
        var created = await service.ReserveAsync(
            "inventory-commit-1",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(15));

        var committed = await service.CommitAsync(created.Reservation!.Id, order.Id);
        var commitReplay = await service.CommitAsync(created.Reservation.Id, order.Id);
        var release = await service.ReleaseAsync(created.Reservation.Id);

        Assert.Equal(InventoryReservationOutcome.Updated, committed.Outcome);
        Assert.Equal(InventoryReservationOutcome.Replayed, commitReplay.Outcome);
        Assert.Equal(InventoryReservationOutcome.Conflict, release.Outcome);
        Assert.Equal(2, await database.StockAsync(1));
    }

    [Fact]
    public async Task Commit_JoinsAmbientTransaction_AndOuterRollbackRevertsOrderAndTransition()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var service = database.CreateService();
        var created = await service.ReserveAsync(
            "inventory-ambient-rollback",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(15));

        await using (var transaction = await database.Context.Database.BeginTransactionAsync())
        {
            var order = BuildOrder();
            database.Context.Orders.Add(order);
            await database.Context.SaveChangesAsync();

            var committed = await service.CommitAsync(created.Reservation!.Id, order.Id);

            Assert.Equal(InventoryReservationOutcome.Updated, committed.Outcome);
            Assert.Same(transaction, database.Context.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.Orders
            .Where(order => order.OrderNumber.StartsWith("RES-"))
            .ToListAsync());
        Assert.Equal(
            InventoryReservationStatuses.Active,
            await database.Context.InventoryReservations
                .Where(reservation => reservation.Id == created.Reservation!.Id)
                .Select(reservation => reservation.Status)
                .SingleAsync());
        Assert.Equal(2, await database.StockAsync(1));
    }

    [Fact]
    public async Task Commit_JoinsAmbientTransaction_AndOuterCommitPersistsOrderAndTransition()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var service = database.CreateService();
        var created = await service.ReserveAsync(
            "inventory-ambient-commit",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(15));
        int orderId;

        await using (var transaction = await database.Context.Database.BeginTransactionAsync())
        {
            var order = BuildOrder();
            database.Context.Orders.Add(order);
            await database.Context.SaveChangesAsync();
            orderId = order.Id;

            var committed = await service.CommitAsync(created.Reservation!.Id, order.Id);

            Assert.Equal(InventoryReservationOutcome.Updated, committed.Outcome);
            Assert.Same(transaction, database.Context.Database.CurrentTransaction);
            await transaction.CommitAsync();
        }

        database.Context.ChangeTracker.Clear();
        Assert.True(await database.Context.Orders.AnyAsync(order => order.Id == orderId));
        Assert.Equal(
            InventoryReservationStatuses.Committed,
            await database.Context.InventoryReservations
                .Where(reservation => reservation.Id == created.Reservation!.Id)
                .Select(reservation => reservation.Status)
                .SingleAsync());
        Assert.Equal(2, await database.StockAsync(1));
    }

    [Fact]
    public async Task ExpireDue_RestoresOnlyExpiredActiveReservations()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var clock = new MutableTimeProvider(Now);
        var service = new InventoryReservationService(database.Context, clock);
        await service.ReserveAsync(
            "inventory-expire-1",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(5));
        await service.ReserveAsync(
            "inventory-expire-2",
            [new InventoryReservationLine(1, 1)],
            Now.AddMinutes(30));

        clock.Now = Now.AddMinutes(10);
        var expired = await service.ExpireDueAsync();

        Assert.Equal(1, expired);
        Assert.Equal(4, await database.StockAsync(1));
        Assert.Equal(
            1,
            await database.Context.InventoryReservations.CountAsync(
                reservation => reservation.Status == InventoryReservationStatuses.Active));
    }

    [Fact]
    public async Task ConcurrentReservations_CannotSellTheSameLastUnitTwice()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Reserve(string key)
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(Now))
                .ReserveAsync(
                    key,
                    [new InventoryReservationLine(1, 1)],
                    Now.AddMinutes(15));
        }

        var first = Reserve("inventory-race-1");
        var second = Reserve("inventory-race-2");
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Created);
        Assert.Single(results, result => result.Outcome is
            InventoryReservationOutcome.InventoryUnavailable or
            InventoryReservationOutcome.Conflict);
        await using var verification = database.CreateContext();
        Assert.Equal(0, await verification.Products.Where(product => product.Id == 1).Select(product => product.Stock).SingleAsync());
        Assert.Equal(1, await verification.InventoryReservations.CountAsync());
    }

    [Fact]
    public async Task ConcurrentSameKey_ReloadsWinnerAsReplay()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Reserve()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(Now))
                .ReserveAsync(
                    "inventory-same-key-race",
                    [new InventoryReservationLine(1, 1)],
                    Now.AddMinutes(15));
        }

        var first = Reserve();
        var second = Reserve();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Created);
        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Replayed);
        Assert.Equal(results[0].Reservation!.Id, results[1].Reservation!.Id);
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Equal(1, await verification.InventoryReservations.CountAsync());
    }

    [Fact]
    public async Task SecondReservationForSameOrder_ReturnsConflictInsteadOfUniqueViolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 3);
        var order = await database.AddOrderAsync();
        var service = database.CreateService();
        var first = await service.ReserveAsync(
            "inventory-order-unique-1",
            [new InventoryReservationLine(1, 1)],
            Now.AddMinutes(15));
        var second = await service.ReserveAsync(
            "inventory-order-unique-2",
            [new InventoryReservationLine(1, 1)],
            Now.AddMinutes(15));

        var firstCommit = await service.CommitAsync(first.Reservation!.Id, order.Id);
        var conflictingCommit = await service.CommitAsync(second.Reservation!.Id, order.Id);

        Assert.Equal(InventoryReservationOutcome.Updated, firstCommit.Outcome);
        Assert.Equal(InventoryReservationOutcome.Conflict, conflictingCommit.Outcome);
        Assert.Equal(
            InventoryReservationStatuses.Active,
            conflictingCommit.Reservation!.Status);
        Assert.Equal(
            1,
            await database.Context.InventoryReservations.CountAsync(
                reservation => reservation.CommittedOrderId == order.Id));
    }

    [Fact]
    public async Task ConcurrentCommitsForSameOrder_HaveOneWinnerAndOneControlledConflict()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 3);
        var firstReservation = await ReserveAsync(database, "inventory-commit-race-1", 1);
        var secondReservation = await ReserveAsync(database, "inventory-commit-race-2", 1);
        var orderId = await database.AddOrderAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Commit(long reservationId)
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(Now))
                .CommitAsync(reservationId, orderId);
        }

        var first = Commit(firstReservation.Id);
        var second = Commit(secondReservation.Id);
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Updated);
        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Conflict);
        await using var verification = database.CreateContext();
        Assert.Equal(
            1,
            await verification.InventoryReservations.CountAsync(
                reservation => reservation.CommittedOrderId == orderId));
    }

    [Fact]
    public async Task Release_WhenStockRestoreWouldOverflow_FailsClosedWithoutMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 2);
        var service = database.CreateService();
        var created = await service.ReserveAsync(
            "inventory-overflow-1",
            [new InventoryReservationLine(1, 1)],
            Now.AddMinutes(15));
        await database.SetStockAsync(1, int.MaxValue);

        var result = await service.ReleaseAsync(created.Reservation!.Id);

        Assert.Equal(InventoryReservationOutcome.Conflict, result.Outcome);
        Assert.Equal(int.MaxValue, await database.StockAsync(1));
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            InventoryReservationStatuses.Active,
            await database.Context.InventoryReservations
                .Where(reservation => reservation.Id == created.Reservation.Id)
                .Select(reservation => reservation.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task ConcurrentRelease_RestoresStockExactlyOnce()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var reservation = await ReserveAsync(database, "inventory-release-race", 3);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Release()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(Now))
                .ReleaseAsync(reservation.Id);
        }

        var first = Release();
        var second = Release();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Updated);
        Assert.All(
            results,
            result => Assert.Contains(
                result.Outcome,
                new[]
                {
                    InventoryReservationOutcome.Updated,
                    InventoryReservationOutcome.Replayed,
                    InventoryReservationOutcome.Conflict
                }));
        await using var verification = database.CreateContext();
        Assert.Equal(4, await verification.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Equal(
            InventoryReservationStatuses.Released,
            await verification.InventoryReservations
                .Where(candidate => candidate.Id == reservation.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task CommitAndReleaseRace_LeavesStockConsistentWithWinningState()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var reservation = await ReserveAsync(database, "inventory-finish-race", 2);
        var orderId = await database.AddOrderAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Finish(bool commit)
        {
            await start.Task;
            await using var context = database.CreateContext();
            var service = new InventoryReservationService(
                context,
                new MutableTimeProvider(Now));
            return commit
                ? await service.CommitAsync(reservation.Id, orderId)
                : await service.ReleaseAsync(reservation.Id);
        }

        var commit = Finish(true);
        var release = Finish(false);
        start.SetResult();
        var results = await Task.WhenAll(commit, release);

        Assert.Single(results, result => result.Outcome == InventoryReservationOutcome.Updated);
        await using var verification = database.CreateContext();
        var status = await verification.InventoryReservations
            .Where(candidate => candidate.Id == reservation.Id)
            .Select(candidate => candidate.Status)
            .SingleAsync();
        var stock = await verification.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync();
        Assert.True(
            (status == InventoryReservationStatuses.Committed && stock == 3) ||
            (status == InventoryReservationStatuses.Released && stock == 5));
    }

    [Fact]
    public async Task CommitAndExpireRace_AfterDeadlineCannotCommitOrDoubleRestore()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var reservation = await ReserveAsync(
            database,
            "inventory-expire-commit-race",
            2,
            Now.AddMinutes(1));
        var orderId = await database.AddOrderAsync();
        var afterExpiry = Now.AddMinutes(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Commit()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(afterExpiry))
                .CommitAsync(reservation.Id, orderId);
        }

        async Task<int> Expire()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(afterExpiry))
                .ExpireDueAsync();
        }

        var commit = Commit();
        var expire = Expire();
        start.SetResult();
        var commitResult = await commit;
        var expiredCount = await expire;

        Assert.Equal(InventoryReservationOutcome.Conflict, commitResult.Outcome);
        Assert.Equal(1, expiredCount);
        await using var verification = database.CreateContext();
        Assert.Equal(4, await verification.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Equal(
            InventoryReservationStatuses.Expired,
            await verification.InventoryReservations
                .Where(candidate => candidate.Id == reservation.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task ReleaseAndExpireRace_RestoresStockExactlyOnce()
    {
        await using var database = await SharedTestDatabase.CreateAsync();
        await database.SetStockAsync(1, 4);
        var reservation = await ReserveAsync(
            database,
            "inventory-release-expire-race",
            2,
            Now.AddMinutes(1));
        var afterExpiry = Now.AddMinutes(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<InventoryReservationResult> Release()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(afterExpiry))
                .ReleaseAsync(reservation.Id);
        }

        async Task<int> Expire()
        {
            await start.Task;
            await using var context = database.CreateContext();
            return await new InventoryReservationService(
                    context,
                    new MutableTimeProvider(afterExpiry))
                .ExpireDueAsync();
        }

        var release = Release();
        var expire = Expire();
        start.SetResult();
        var releaseResult = await release;
        var expiredCount = await expire;

        Assert.True(
            (releaseResult.Outcome == InventoryReservationOutcome.Updated && expiredCount == 0) ||
            (releaseResult.Outcome == InventoryReservationOutcome.Conflict && expiredCount == 1));
        await using var verification = database.CreateContext();
        Assert.Equal(4, await verification.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Contains(
            await verification.InventoryReservations
                .Where(candidate => candidate.Id == reservation.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync(),
            new[]
            {
                InventoryReservationStatuses.Released,
                InventoryReservationStatuses.Expired
            });
    }

    private static async Task<InventoryReservation> ReserveAsync(
        SharedTestDatabase database,
        string key,
        int quantity,
        DateTimeOffset? expiresAt = null)
    {
        await using var context = database.CreateContext();
        var result = await new InventoryReservationService(
                context,
                new MutableTimeProvider(Now))
            .ReserveAsync(
                key,
                [new InventoryReservationLine(1, quantity)],
                expiresAt ?? Now.AddMinutes(15));
        Assert.Equal(InventoryReservationOutcome.Created, result.Outcome);
        return result.Reservation!;
    }

    private static Order BuildOrder() => new()
    {
        OrderNumber = $"RES-{Guid.NewGuid():N}",
        CustomerName = "Reservation Test",
        CustomerEmail = "reservation@example.test",
        CustomerPhone = "+905550000000",
        ShippingAddress = "Reservation test shipping address",
        City = "Istanbul",
        PostalCode = "34000",
        TotalAmount = 1m,
        Status = OrderStatuses.Pending
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public InventoryReservationService CreateService() =>
            new(Context, new MutableTimeProvider(Now));

        public async Task SetStockAsync(int productId, int stock)
        {
            var product = await Context.Products.FindAsync(productId) ??
                throw new InvalidOperationException("Seed product not found.");
            product.Stock = stock;
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public Task<int> StockAsync(int productId) =>
            Context.Products.AsNoTracking()
                .Where(product => product.Id == productId)
                .Select(product => product.Stock)
                .SingleAsync();

        public async Task<Order> AddOrderAsync()
        {
            var order = new Order
            {
                OrderNumber = $"RES-{Guid.NewGuid():N}",
                CustomerName = "Reservation Test",
                CustomerEmail = "reservation@example.test",
                CustomerPhone = "+905550000000",
                ShippingAddress = "Reservation test shipping address",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 1m,
                Status = OrderStatuses.Pending
            };
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return order;
        }

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
                $"Data Source=file:inventory-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=5;Pooling=False";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            await using var context = new AutoPartsDbContext(
                new DbContextOptionsBuilder<AutoPartsDbContext>()
                    .UseSqlite(keeper)
                    .Options);
            await context.Database.EnsureCreatedAsync();
            return new SharedTestDatabase(connectionString, keeper);
        }

        public AutoPartsDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(new SqliteConnection(_connectionString))
                .Options);

        public async Task SetStockAsync(int productId, int stock)
        {
            await using var context = CreateContext();
            var product = await context.Products.FindAsync(productId) ??
                throw new InvalidOperationException("Seed product not found.");
            product.Stock = stock;
            await context.SaveChangesAsync();
        }

        public async Task<int> AddOrderAsync()
        {
            await using var context = CreateContext();
            var order = new Order
            {
                OrderNumber = $"RES-{Guid.NewGuid():N}",
                CustomerName = "Reservation Test",
                CustomerEmail = "reservation@example.test",
                CustomerPhone = "+905550000000",
                ShippingAddress = "Reservation test shipping address",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 1m,
                Status = OrderStatuses.Pending
            };
            context.Orders.Add(order);
            await context.SaveChangesAsync();
            return order.Id;
        }

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();
    }
}
