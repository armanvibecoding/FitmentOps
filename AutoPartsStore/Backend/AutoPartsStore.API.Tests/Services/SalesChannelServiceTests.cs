using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class SalesChannelServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChannelCannotBeEnabledWithoutConfiguredAdapter()
    {
        await using var database = await TestDatabase.CreateAsync();
        var channel = await database.Context.SalesChannels.SingleAsync(
            candidate => candidate.Code == SalesChannelCodes.Trendyol);
        var service = database.Service(configured: false);

        var result = await service.UpdateStateAsync(
            channel.Id,
            requestedEnabled: true,
            SalesChannelModes.Sandbox,
            channel.ConcurrencyToken);

        Assert.Equal(SalesChannelStateOutcome.ProviderUnavailable, result.Outcome);
        Assert.False(channel.RequestedEnabled);
        Assert.Equal(SalesChannelModes.Disabled, channel.Mode);
    }

    [Fact]
    public async Task ListingRefreshUsesServerPriceAndStockAndFailsClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.AddProductAsync(price: 125m, stock: 7);
        var channel = await database.Context.SalesChannels.SingleAsync(
            candidate => candidate.Code == SalesChannelCodes.Trendyol);

        var result = await database.Service(configured: false).RefreshListingAsync(
            channel.Id,
            product.Id,
            "listing-1");

        Assert.Equal(ChannelListingRefreshOutcome.Blocked, result.Outcome);
        var listing = await database.Context.ChannelListings.SingleAsync();
        Assert.Equal(125m, listing.DesiredPrice);
        Assert.Equal(7, listing.DesiredStock);
        Assert.Equal(ChannelListingStatuses.Blocked, listing.Status);
        Assert.Empty(await database.Context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task EnabledListingQueuesOnceAndObservationDetectsDrift()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.AddProductAsync(price: 100m, stock: 5);
        var channel = await database.EnableTrendyolAsync();
        var service = database.Service(configured: true);

        var queued = await service.RefreshListingAsync(channel.Id, product.Id, "listing-2");
        var replay = await service.RefreshListingAsync(channel.Id, product.Id, "listing-2");
        var drift = await service.RecordListingObservationAsync(
            queued.ListingId!.Value,
            observedPrice: 101m,
            observedStock: 5);

        Assert.Equal(ChannelListingRefreshOutcome.Queued, queued.Outcome);
        Assert.Equal(ChannelListingRefreshOutcome.Replayed, replay.Outcome);
        Assert.Equal(ChannelListingRefreshOutcome.Conflict, drift.Outcome);
        Assert.Equal("stock-price-drift", drift.Message);
        Assert.Equal(1, await database.Context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task OrderImportIsIdempotentAcrossSameAndDifferentEvents()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.AddProductAsync(price: 50m, stock: 10);
        await database.EnableTrendyolAsync();
        var service = database.Service(configured: true);
        var command = OrderCommand(product.Id, "event-1", "external-order-1");

        var imported = await service.ImportOrderAsync(command);
        var eventReplay = await service.ImportOrderAsync(command);
        var orderReplay = await service.ImportOrderAsync(command with { ExternalEventId = "event-2" });
        var conflict = await service.ImportOrderAsync(command with { CustomerName = "Changed Customer" });
        var changedOrderConflict = await service.ImportOrderAsync(command with
        {
            ExternalEventId = "event-3",
            CustomerName = "Changed Customer"
        });

        Assert.Equal(ChannelOrderImportOutcome.Imported, imported.Outcome);
        Assert.Equal(ChannelOrderImportOutcome.Replayed, eventReplay.Outcome);
        Assert.Equal(ChannelOrderImportOutcome.Replayed, orderReplay.Outcome);
        Assert.Equal(ChannelOrderImportOutcome.Conflict, conflict.Outcome);
        Assert.Equal(ChannelOrderImportOutcome.Conflict, changedOrderConflict.Outcome);
        Assert.Equal(imported.OrderId, eventReplay.OrderId);
        Assert.Equal(imported.OrderId, orderReplay.OrderId);
        Assert.Equal(1, await database.Context.Orders.CountAsync());
        Assert.Equal(2, await database.Context.ChannelInboxEvents.CountAsync());
        Assert.Equal(1, await database.Context.ChannelOrderLinks.CountAsync());
        Assert.Equal(1, await database.Context.OutboxMessages.CountAsync());
        Assert.Equal(8, (await database.Context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id)).Stock);
        var payment = await database.Context.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Equal(PaymentMethods.Marketplace, payment.Method);
    }

    [Fact]
    public async Task ImportRejectsInsufficientStockAndSensitiveEntitiesDoNotSerializeKeys()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = await database.AddProductAsync(price: 50m, stock: 1);
        await database.EnableTrendyolAsync();
        var service = database.Service(configured: true);

        var result = await service.ImportOrderAsync(OrderCommand(product.Id, "event-3", "external-order-3"));
        var commandJson = OrderCommand(product.Id, "secret-event", "secret-order").ToString();
        var entityJson = JsonSerializer.Serialize(new ChannelInboxEvent
        {
            ExternalEventId = "secret-event",
            PayloadHash = new string('A', 64)
        });

        Assert.Equal(ChannelOrderImportOutcome.InventoryUnavailable, result.Outcome);
        Assert.Empty(await database.Context.Orders.ToListAsync());
        Assert.Equal(1, (await database.Context.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == product.Id)).Stock);
        Assert.DoesNotContain("private@example.test", commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-event", entityJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadHash", entityJson, StringComparison.Ordinal);
    }

    private static ChannelOrderImportCommand OrderCommand(
        int productId,
        string eventId,
        string orderId) => new(
            SalesChannelCodes.Trendyol,
            eventId,
            orderId,
            "Private Customer",
            "private@example.test",
            "+905550000000",
            "Private shipping address 1",
            "Istanbul",
            "34000",
            "TRY",
            100m,
            [new ChannelOrderImportLine(productId, 2, 50m)]);

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

        public SalesChannelService Service(bool configured) => new(
            Context,
            configured ? new ConfiguredRegistry() : new DisabledSalesChannelAdapterRegistry(),
            new FixedTimeProvider(Now));

        public async Task<Product> AddProductAsync(decimal price, int stock)
        {
            var product = new Product
            {
                Name = "Channel product",
                Description = "Channel product description",
                PartNumber = $"CHANNEL-{Guid.NewGuid():N}",
                Price = price,
                Stock = stock,
                CategoryId = 1,
                BrandId = 1,
                PartBrandId = 1,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime
            };
            Context.Products.Add(product);
            await Context.SaveChangesAsync();
            return product;
        }

        public async Task<SalesChannel> EnableTrendyolAsync()
        {
            var channel = await Context.SalesChannels.SingleAsync(
                candidate => candidate.Code == SalesChannelCodes.Trendyol);
            channel.RequestedEnabled = true;
            channel.Mode = SalesChannelModes.Sandbox;
            channel.UpdatedAtUtc = Now.UtcDateTime;
            channel.ConcurrencyToken = Guid.NewGuid();
            await Context.SaveChangesAsync();
            return channel;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ConfiguredRegistry : ISalesChannelAdapterRegistry
    {
        public SalesChannelAdapterCapability GetCapability(string channelCode) =>
            new(channelCode, true, true, true, "configured-test-adapter");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
