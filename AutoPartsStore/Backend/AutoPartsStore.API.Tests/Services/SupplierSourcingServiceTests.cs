using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class SupplierSourcingServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RegisterOffer_IsIdempotentAndRejectsChangedReplay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var supplier = await database.AddSupplierAsync("supplier-a", priority: 1);
        var service = database.CreateService();
        var command = Offer(supplier.Id, "offer-1", unitCost: 80m, available: 10);

        var first = await service.RegisterOfferAsync(command);
        var replay = await service.RegisterOfferAsync(command);
        var conflict = await service.RegisterOfferAsync(command with { UnitCost = 81m });

        Assert.Equal(SupplierOfferRegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(SupplierOfferRegistrationOutcome.Replayed, replay.Outcome);
        Assert.Equal(SupplierOfferRegistrationOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.SupplierOffers.CountAsync());
    }

    [Fact]
    public async Task Select_UsesLandedCostThenLeadPriorityAndId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var slow = await database.AddSupplierAsync("slow", priority: 0);
        var fast = await database.AddSupplierAsync("fast", priority: 5);
        var unhealthy = await database.AddSupplierAsync(
            "unhealthy",
            priority: 0,
            SupplierHealthStatuses.Unhealthy);
        var service = database.CreateService();
        await service.RegisterOfferAsync(Offer(slow.Id, "slow-offer", 75m, 20) with
        {
            ShippingCost = 50m,
            LeadTimeDays = 5
        });
        var expected = await service.RegisterOfferAsync(Offer(fast.Id, "fast-offer", 78m, 20) with
        {
            ShippingCost = 5m,
            LeadTimeDays = 1
        });
        await service.RegisterOfferAsync(Offer(unhealthy.Id, "bad-health", 1m, 20));

        var result = await service.SelectAsync(new SupplierSourcingRequest(1, 2, "try"));

        Assert.Equal(SupplierSourcingOutcome.Selected, result.Outcome);
        var allocation = Assert.Single(result.Allocations);
        Assert.Equal(expected.OfferId, allocation.OfferId);
        Assert.Equal(161m, result.TotalLandedCost);
    }

    [Fact]
    public async Task Select_EnforcesMoqValidityAndSplitOptIn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = await database.AddSupplierAsync("split-a", priority: 1);
        var second = await database.AddSupplierAsync("split-b", priority: 2);
        var service = database.CreateService();
        await service.RegisterOfferAsync(Offer(first.Id, "split-offer-a", 50m, 3));
        await service.RegisterOfferAsync(Offer(second.Id, "split-offer-b", 55m, 4) with
        {
            MinimumOrderQuantity = 2
        });

        var noSplit = await service.SelectAsync(
            new SupplierSourcingRequest(1, 6, "TRY", AllowSplit: false));
        var split = await service.SelectAsync(
            new SupplierSourcingRequest(1, 6, "TRY", AllowSplit: true));

        Assert.Equal(SupplierSourcingOutcome.InsufficientSupply, noSplit.Outcome);
        Assert.Equal(SupplierSourcingOutcome.Selected, split.Outcome);
        Assert.Equal(6, split.Allocations.Sum(allocation => allocation.Quantity));
        Assert.Equal(2, split.Allocations.Count);
    }

    [Fact]
    public async Task RegisterOffer_RejectsExpiredOrCapabilityFreeInput()
    {
        await using var database = await TestDatabase.CreateAsync();
        var supplier = await database.AddSupplierAsync("supplier-invalid", priority: 0);
        var service = database.CreateService();

        var expired = await service.RegisterOfferAsync(
            Offer(supplier.Id, "expired", 10m, 10) with
            {
                ValidUntilUtc = Now.AddMinutes(-1).UtcDateTime
            });
        var noCapability = await service.RegisterOfferAsync(
            Offer(supplier.Id, "no-capability", 10m, 10) with
            {
                CanDropship = false,
                CanSupplyWarehouse = false
            });

        Assert.Equal(SupplierOfferRegistrationOutcome.InvalidRequest, expired.Outcome);
        Assert.Equal(SupplierOfferRegistrationOutcome.InvalidRequest, noCapability.Outcome);
        Assert.Empty(await database.Context.SupplierOffers.ToListAsync());
    }

    [Fact]
    public void SupplierEntities_DoNotSerializeExternalIdentityOrConcurrencyData()
    {
        var offerJson = JsonSerializer.Serialize(new SupplierOffer
        {
            ExternalOfferId = "private-external-id",
            PayloadHash = new string('A', 64),
            ConcurrencyToken = Guid.NewGuid()
        });
        var supplierJson = JsonSerializer.Serialize(new Supplier
        {
            Code = "supplier-private-code",
            Name = "Supplier",
            ConcurrencyToken = Guid.NewGuid()
        });

        Assert.DoesNotContain("private-external-id", offerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadHash", offerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrencyToken", offerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrencyToken", supplierJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSameOfferRegistration_ProducesOneRowAndReplay()
    {
        var databaseName = $"supplier-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setup = new AutoPartsDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Suppliers.Add(Supplier("concurrent", 0));
            await setup.SaveChangesAsync();
        }

        long supplierId;
        await using (var lookup = new AutoPartsDbContext(options))
        {
            supplierId = await lookup.Suppliers.Select(supplier => supplier.Id).SingleAsync();
        }

        await using var firstContext = new AutoPartsDbContext(options);
        await using var secondContext = new AutoPartsDbContext(options);
        var command = Offer(supplierId, "same-concurrent-offer", 10m, 5);
        var results = await Task.WhenAll(
            new SupplierSourcingService(firstContext, new FixedTimeProvider(Now))
                .RegisterOfferAsync(command),
            new SupplierSourcingService(secondContext, new FixedTimeProvider(Now))
                .RegisterOfferAsync(command));

        Assert.Contains(results, result => result.Outcome == SupplierOfferRegistrationOutcome.Registered);
        Assert.Contains(results, result => result.Outcome == SupplierOfferRegistrationOutcome.Replayed);
        await using var verification = new AutoPartsDbContext(options);
        Assert.Equal(1, await verification.SupplierOffers.CountAsync());
    }

    private static SupplierOfferCommand Offer(
        long supplierId,
        string externalOfferId,
        decimal unitCost,
        int available) => new(
            supplierId,
            externalOfferId,
            ProductId: 1,
            OemNumber: "OEM-123",
            Currency: "TRY",
            UnitCost: unitCost,
            ShippingCost: 0m,
            AvailableQuantity: available,
            LeadTimeDays: 2,
            MinimumOrderQuantity: 1,
            ValidUntilUtc: Now.AddDays(1).UtcDateTime,
            CanDropship: true,
            CanSupplyWarehouse: true);

    private static Supplier Supplier(
        string code,
        int priority,
        string health = SupplierHealthStatuses.Healthy) => new()
        {
            Code = code,
            Name = code,
            IsActive = true,
            HealthStatus = health,
            Priority = priority,
            CreatedAtUtc = Now.UtcDateTime,
            UpdatedAtUtc = Now.UtcDateTime,
            ConcurrencyToken = Guid.NewGuid()
        };

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

        public async Task<Supplier> AddSupplierAsync(
            string code,
            int priority,
            string health = SupplierHealthStatuses.Healthy)
        {
            var supplier = Supplier(code, priority, health);
            Context.Suppliers.Add(supplier);
            await Context.SaveChangesAsync();
            return supplier;
        }

        public SupplierSourcingService CreateService() =>
            new(Context, new FixedTimeProvider(Now));

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
}
