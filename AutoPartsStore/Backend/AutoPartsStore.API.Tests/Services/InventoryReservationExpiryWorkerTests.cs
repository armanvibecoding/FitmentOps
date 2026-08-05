using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class InventoryReservationExpiryWorkerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_AreFailClosedByDefaultAndDefaultBoundsAreValid()
    {
        var options = new InventoryReservationExpiryOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal("InventoryReservationExpiry", InventoryReservationExpiryOptions.ConfigurationSectionName);
    }

    [Theory]
    [InlineData("BatchSize", 0)]
    [InlineData("BatchSize", 101)]
    [InlineData("MaxBatchesPerPoll", 0)]
    [InlineData("MaxBatchesPerPoll", 101)]
    [InlineData("PollIntervalMilliseconds", 99)]
    [InlineData("PollIntervalMilliseconds", 3_600_001)]
    public void Options_RejectUnboundedValues(string setting, int value)
    {
        var options = setting switch
        {
            "BatchSize" => new InventoryReservationExpiryOptions { BatchSize = value },
            "MaxBatchesPerPoll" => new InventoryReservationExpiryOptions { MaxBatchesPerPoll = value },
            "PollIntervalMilliseconds" => new InventoryReservationExpiryOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(value)
            },
            _ => throw new InvalidOperationException("Unknown test setting.")
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public async Task DisabledProcessor_DoesNotExpireOrRestoreStock()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var clock = new MutableTimeProvider(Now);
        var service = new InventoryReservationService(database.Context, clock);
        var reservation = await service.ReserveAsync(
            "worker-disabled",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(1));
        clock.Now = Now.AddMinutes(2);
        var processor = new InventoryReservationExpiryProcessor(
            service,
            new InventoryReservationExpiryOptions());

        var expired = await processor.ExpireBatchAsync();

        Assert.Equal(0, expired);
        Assert.Equal(3, await database.StockAsync(1));
        Assert.Equal(
            InventoryReservationStatuses.Active,
            await database.StatusAsync(reservation.Reservation!.Id));
    }

    [Fact]
    public async Task DisabledWorkerPoll_DoesNotResolveScopedProcessor()
    {
        var worker = new InventoryReservationExpiryWorker(
            new ThrowingScopeFactory("must not resolve"),
            new InventoryReservationExpiryOptions(),
            NullLogger<InventoryReservationExpiryWorker>.Instance);

        var expired = await worker.ProcessPollAsync();

        Assert.Equal(0, expired);
    }

    [Fact]
    public async Task EnabledProcessor_ExpiresDueAndLeavesFutureAndCommittedUntouched()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 10);
        var clock = new MutableTimeProvider(Now);
        var service = new InventoryReservationService(database.Context, clock);
        var due = await service.ReserveAsync(
            "worker-due",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(5));
        var future = await service.ReserveAsync(
            "worker-future",
            [new InventoryReservationLine(1, 3)],
            Now.AddMinutes(30));
        var committed = await service.ReserveAsync(
            "worker-committed",
            [new InventoryReservationLine(1, 1)],
            Now.AddMinutes(30));
        var order = await database.AddOrderAsync();
        var commit = await service.CommitAsync(committed.Reservation!.Id, order.Id);
        Assert.Equal(InventoryReservationOutcome.Updated, commit.Outcome);
        clock.Now = Now.AddMinutes(10);
        var processor = new InventoryReservationExpiryProcessor(
            service,
            EnabledOptions());

        var expired = await processor.ExpireBatchAsync();

        Assert.Equal(1, expired);
        Assert.Equal(6, await database.StockAsync(1));
        Assert.Equal(
            InventoryReservationStatuses.Expired,
            await database.StatusAsync(due.Reservation!.Id));
        Assert.Equal(
            InventoryReservationStatuses.Active,
            await database.StatusAsync(future.Reservation!.Id));
        Assert.Equal(
            InventoryReservationStatuses.Committed,
            await database.StatusAsync(committed.Reservation.Id));
    }

    [Fact]
    public async Task WorkerPoll_StopsAtConfiguredBatchAndPollBounds()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 10);
        var clock = new MutableTimeProvider(Now);
        var service = new InventoryReservationService(database.Context, clock);
        for (var index = 0; index < 5; index++)
        {
            var result = await service.ReserveAsync(
                $"worker-bounded-{index}",
                [new InventoryReservationLine(1, 1)],
                Now.AddMinutes(1));
            Assert.Equal(InventoryReservationOutcome.Created, result.Outcome);
        }

        clock.Now = Now.AddMinutes(2);
        var options = new InventoryReservationExpiryOptions
        {
            Enabled = true,
            BatchSize = 2,
            MaxBatchesPerPoll = 2,
            PollInterval = TimeSpan.FromHours(1)
        };
        await using var services = database.CreateWorkerServices(options, clock);
        var worker = new InventoryReservationExpiryWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<InventoryReservationExpiryWorker>.Instance);

        var expired = await worker.ProcessPollAsync();

        Assert.Equal(4, expired);
        Assert.Equal(4, await database.ExpiredCountAsync());
        Assert.Equal(1, await database.ActiveCountAsync());
        Assert.Equal(9, await database.StockAsync(1));
    }

    [Fact]
    public async Task Worker_ExceptionLogContainsTypeButNotExceptionOrSensitiveMessage()
    {
        const string sensitiveMessage = "customer@example.test payload=full-address";
        var options = EnabledOptions(TimeSpan.FromHours(1));
        var logger = new RecordingLogger<InventoryReservationExpiryWorker>();
        var worker = new InventoryReservationExpiryWorker(
            new ThrowingScopeFactory(sensitiveMessage),
            options,
            logger);

        await worker.StartAsync(CancellationToken.None);
        var entry = await logger.ErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(stopTimeout.Token);

        Assert.Null(entry.Exception);
        Assert.Contains(nameof(SensitiveExpiryException), entry.Message);
        Assert.DoesNotContain(sensitiveMessage, entry.Message);
        Assert.DoesNotContain("customer@example.test", entry.Message);
        Assert.DoesNotContain("payload=", entry.Message);
        var loggedProperty = Assert.Single(
            entry.Properties,
            item => item.Key != "{OriginalFormat}");
        Assert.Equal("ExceptionType", loggedProperty.Key);
        Assert.Equal(nameof(SensitiveExpiryException), loggedProperty.Value);
    }

    [Fact]
    public async Task EnabledProcessor_HonorsPreCancelledTokenWithoutMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.SetStockAsync(1, 5);
        var clock = new MutableTimeProvider(Now);
        var service = new InventoryReservationService(database.Context, clock);
        var reservation = await service.ReserveAsync(
            "worker-cancelled",
            [new InventoryReservationLine(1, 2)],
            Now.AddMinutes(1));
        clock.Now = Now.AddMinutes(2);
        var processor = new InventoryReservationExpiryProcessor(service, EnabledOptions());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ExpireBatchAsync(cancelled.Token));

        Assert.Equal(3, await database.StockAsync(1));
        Assert.Equal(
            InventoryReservationStatuses.Active,
            await database.StatusAsync(reservation.Reservation!.Id));
    }

    [Fact]
    public async Task Worker_StopCancelsLongPollDelayPromptly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = EnabledOptions(TimeSpan.FromHours(1));
        var clock = new MutableTimeProvider(Now);
        await using var services = database.CreateWorkerServices(options, clock);
        var worker = new InventoryReservationExpiryWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<InventoryReservationExpiryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await worker.StopAsync(stopTimeout.Token);
    }

    private static InventoryReservationExpiryOptions EnabledOptions(
        TimeSpan? pollInterval = null) =>
        new()
        {
            Enabled = true,
            BatchSize = 10,
            MaxBatchesPerPoll = 2,
            PollInterval = pollInterval ?? TimeSpan.FromSeconds(1)
        };

    private sealed record LogEntry(
        string Message,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> Properties);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource<LogEntry> ErrorLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Error)
            {
                return;
            }

            var properties = state is IReadOnlyList<KeyValuePair<string, object?>> list
                ? list
                : Array.Empty<KeyValuePair<string, object?>>();
            ErrorLogged.TrySetResult(new LogEntry(
                formatter(state, exception),
                exception,
                properties));
        }
    }

    private sealed class ThrowingScopeFactory(string message) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new SensitiveExpiryException(message);
    }

    private sealed class SensitiveExpiryException(string message) : Exception(message);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AutoPartsDbContext> _options;

        private TestDatabase(
            AutoPartsDbContext context,
            SqliteConnection connection,
            DbContextOptions<AutoPartsDbContext> options)
        {
            Context = context;
            _connection = connection;
            _options = options;
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
            return new TestDatabase(context, connection, options);
        }

        public ServiceProvider CreateWorkerServices(
            InventoryReservationExpiryOptions options,
            TimeProvider clock)
        {
            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddScoped(_ => new AutoPartsDbContext(_options));
            services.AddScoped(provider => new InventoryReservationService(
                provider.GetRequiredService<AutoPartsDbContext>(),
                clock));
            services.AddScoped<InventoryReservationExpiryProcessor>();
            return services.BuildServiceProvider();
        }

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
                OrderNumber = $"EXP-{Guid.NewGuid():N}",
                CustomerName = "Expiry Test",
                CustomerEmail = "expiry@example.test",
                CustomerPhone = "+905550000000",
                ShippingAddress = "Expiry test shipping address",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 1m,
                Status = OrderStatuses.Pending
            };
            Context.Orders.Add(order);
            await Context.SaveChangesAsync();
            return order;
        }

        public async Task<string> StatusAsync(long reservationId)
        {
            Context.ChangeTracker.Clear();
            return await Context.InventoryReservations
                .Where(reservation => reservation.Id == reservationId)
                .Select(reservation => reservation.Status)
                .SingleAsync();
        }

        public async Task<int> ActiveCountAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.InventoryReservations.CountAsync(
                reservation => reservation.Status == InventoryReservationStatuses.Active);
        }

        public async Task<int> ExpiredCountAsync()
        {
            Context.ChangeTracker.Clear();
            return await Context.InventoryReservations.CountAsync(
                reservation => reservation.Status == InventoryReservationStatuses.Expired);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
