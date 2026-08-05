using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.SqlServerTests;

public sealed class SqlServerMigrationAndCheckoutTests
{
    [Fact]
    public async Task ProductionMigrations_ApplyAndConcurrentCheckoutCannotOversell()
    {
        var masterConnection = Environment.GetEnvironmentVariable("SQLSERVER_TEST_MASTER_CONNECTION");
        if (string.IsNullOrWhiteSpace(masterConnection))
        {
            throw new InvalidOperationException(
                "SQLSERVER_TEST_MASTER_CONNECTION is required; this production-provider gate must not be skipped.");
        }

        var databaseName = $"AutoPartsSqlTests_{Guid.NewGuid():N}";
        if (!databaseName.StartsWith("AutoPartsSqlTests_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsafe SQL Server test database name.");
        }

        var builder = new SqlConnectionStringBuilder(masterConnection)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true
        };
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        try
        {
            await using (var migrationContext = new AutoPartsDbContext(options))
            {
                await migrationContext.Database.MigrateAsync();
                Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync());
            }

            var fixture = await SeedCheckoutFixtureAsync(options);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 10)
                .Select(index => RunCheckoutAsync(options, fixture, index, release.Task))
                .ToArray();

            release.SetResult();
            var results = await Task.WhenAll(attempts);

            Assert.Equal(5, results.Count(result => result.Outcome == CheckoutOutcome.Created));
            Assert.Equal(5, results.Count(result => result.Outcome == CheckoutOutcome.InventoryUnavailable));

            await using var verificationContext = new AutoPartsDbContext(options);
            Assert.Equal(0, await verificationContext.Products
                .Where(product => product.Id == fixture.ProductId)
                .Select(product => product.Stock)
                .SingleAsync());
            Assert.Equal(5, await verificationContext.Orders.CountAsync());
            Assert.Equal(5, await verificationContext.OrderItems.CountAsync());
            Assert.Equal(10, await verificationContext.LegalAcceptances.CountAsync());
        }
        finally
        {
            await using var cleanupContext = new AutoPartsDbContext(options);
            await cleanupContext.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<CheckoutFixture> SeedCheckoutFixtureAsync(
        DbContextOptions<AutoPartsDbContext> options)
    {
        await using var context = new AutoPartsDbContext(options);
        var now = DateTime.UtcNow;
        var actor = new User
        {
            FullName = "SQL Server Test Admin",
            Email = $"sql-admin-{Guid.NewGuid():N}@integration.test",
            Password = "test-only-password-hash",
            Role = "SuperAdmin",
            IsActive = true,
            CreatedAt = now
        };
        context.Users.Add(actor);
        await context.SaveChangesAsync();

        var category = new Category { Name = "SQL Test Category", Slug = $"sql-category-{Guid.NewGuid():N}" };
        var brand = new Brand { Name = "SQL Test Vehicle Brand", Slug = $"sql-brand-{Guid.NewGuid():N}" };
        var partBrand = new PartBrand { Name = "SQL Test Part Brand", Slug = $"sql-part-brand-{Guid.NewGuid():N}" };
        var product = new Product
        {
            Name = "Concurrent Checkout Test Part",
            PartNumber = $"SQL-{Guid.NewGuid():N}",
            Price = 199.90m,
            Stock = 5,
            Category = category,
            Brand = brand,
            PartBrand = partBrand,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.Products.Add(product);

        var documents = new[]
        {
            LegalDocumentVersion.CreateDraft(
                LegalDocumentTypes.PreliminaryInformation,
                "sql-v1",
                "SQL Preliminary Information",
                "SQL Server integration test preliminary information.",
                actor.Id,
                now),
            LegalDocumentVersion.CreateDraft(
                LegalDocumentTypes.DistanceSalesAgreement,
                "sql-v1",
                "SQL Distance Sales Agreement",
                "SQL Server integration test distance sales agreement.",
                actor.Id,
                now)
        };
        foreach (var document in documents)
        {
            document.Publish(actor.Id, now);
        }
        context.LegalDocumentVersions.AddRange(documents);
        await context.SaveChangesAsync();

        return new CheckoutFixture(
            product.Id,
            documents.Select(document => new LegalAcceptanceDto
            {
                DocumentType = document.DocumentType,
                Version = document.Version,
                ContentSha256 = document.ContentSha256,
                Accepted = true
            }).ToArray());
    }

    private static async Task<CheckoutResult> RunCheckoutAsync(
        DbContextOptions<AutoPartsDbContext> options,
        CheckoutFixture fixture,
        int index,
        Task release)
    {
        await release;
        await using var context = new AutoPartsDbContext(options);
        var service = new CheckoutService(
            context,
            new LegalConsentService(context, new LegalCheckoutOptions()));
        var dto = new CreateOrderDto
        {
            CustomerName = $"Concurrent Customer {index}",
            CustomerEmail = $"sql-customer-{index}@integration.test",
            CustomerPhone = $"+905550000{index:000}",
            ShippingAddress = $"Integration Test Street Number {index}",
            City = "Istanbul",
            PostalCode = "34000",
            PaymentMethod = PaymentMethods.PayAtDelivery,
            Items = [new OrderItemDto { ProductId = fixture.ProductId, Quantity = 1 }],
            LegalAcceptances = fixture.Acceptances.Select(acceptance => new LegalAcceptanceDto
            {
                DocumentType = acceptance.DocumentType,
                Version = acceptance.Version,
                ContentSha256 = acceptance.ContentSha256,
                Accepted = true
            }).ToList()
        };

        return await service.CreateOrderAsync(
            dto,
            $"sql-concurrency-attempt-{index:00}-{Guid.NewGuid():N}"[..40],
            userId: null);
    }

    private sealed record CheckoutFixture(int ProductId, IReadOnlyList<LegalAcceptanceDto> Acceptances);
}
