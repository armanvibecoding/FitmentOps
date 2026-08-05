using System.Security.Claims;
using System.Text.Json;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class LegalConsentServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CheckoutFailsClosedBeforeStockMutationWhenRequiredDocumentsAreMissing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var originalStock = await database.Context.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync();
        var service = CreateCheckoutService(database.Context);

        var result = await service.CreateOrderAsync(
            CreateRequest(CreateAcceptances()),
            "legal-missing-docs-0001",
            null);

        Assert.Equal(CheckoutOutcome.ConfigurationUnavailable, result.Outcome);
        Assert.Equal(originalStock, await database.Context.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Empty(database.Context.Orders);
    }

    [Fact]
    public async Task TamperedContentHashIsRejectedBeforeStockMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPublishedDocumentsAsync(database.Context);
        var originalStock = await database.Context.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync();
        var acceptances = CreateAcceptances();
        acceptances[0].ContentSha256 = new string('f', 64);

        var result = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest(acceptances),
            "legal-tampered-hash-001",
            null);

        Assert.Equal(CheckoutOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(originalStock, await database.Context.Products
            .Where(product => product.Id == 1)
            .Select(product => product.Stock)
            .SingleAsync());
        Assert.Empty(database.Context.Orders);
    }

    [Fact]
    public async Task SuccessfulCheckoutPersistsImmutableSnapshotsWithoutRawCheckoutKey()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPublishedDocumentsAsync(database.Context);
        const string checkoutKey = "legal-accepted-order-001";

        var result = await CreateCheckoutService(database.Context).CreateOrderAsync(
            CreateRequest(CreateAcceptances()),
            checkoutKey,
            userId: null);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
        database.Context.ChangeTracker.Clear();
        var acceptances = await database.Context.LegalAcceptances
            .AsNoTracking()
            .OrderBy(acceptance => acceptance.DocumentTypeSnapshot)
            .ToListAsync();
        Assert.Equal(2, acceptances.Count);
        Assert.All(acceptances, acceptance =>
        {
            Assert.Equal("test-v1", acceptance.VersionSnapshot);
            Assert.Equal(64, acceptance.ContentSha256Snapshot.Length);
            Assert.Equal(64, acceptance.CheckoutReferenceSha256.Length);
            Assert.DoesNotContain(checkoutKey, JsonSerializer.Serialize(acceptance));
        });
    }

    [Fact]
    public async Task AdminCreatesAndPublishesImmutableVersionWithAuditIntents()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = new AdminLegalController(
            database.Context,
            new AdminAuditIntentService(database.Context, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                TraceIdentifier = "legal-admin-test",
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "42"),
                    new Claim(ClaimTypes.Role, AdminAuditRoles.SuperAdmin)
                ], "test"))
            }
        };

        var createdResult = await controller.CreateDraft(new CreateLegalDocumentDraftDto
        {
            DocumentType = LegalDocumentTypes.PreliminaryInformation,
            Version = "2026-08-05",
            Title = "Approved test text",
            Content = "Line one\r\nLine two"
        }, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(createdResult.Result);
        var dto = Assert.IsType<AdminLegalDocumentDto>(created.Value);
        Assert.Equal("Line one\nLine two", dto.Content);

        var publish = await controller.Publish(
            dto.Id,
            new LegalDocumentTransitionDto { ConcurrencyToken = dto.ConcurrencyToken },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(publish);
        database.Context.ChangeTracker.Clear();
        var stored = await database.Context.LegalDocumentVersions.SingleAsync();
        Assert.Equal(LegalDocumentStatuses.Published, stored.Status);
        Assert.Equal(2, await database.Context.AdminAuditIntents.CountAsync());
        Assert.Contains(
            await database.Context.AdminAuditIntents.Select(intent => intent.Action).ToListAsync(),
            action => action == AdminAuditActions.LegalDocumentPublished);
    }

    private static CheckoutService CreateCheckoutService(AutoPartsDbContext context) =>
        new(context, new LegalConsentService(context, new LegalCheckoutOptions()));

    private static CreateOrderDto CreateRequest(List<LegalAcceptanceDto> acceptances) => new()
    {
        CustomerName = "Legal Test",
        CustomerEmail = "legal@example.test",
        CustomerPhone = "+905550000000",
        ShippingAddress = "Test Mahallesi Test Sokak No 1",
        City = "Istanbul",
        PostalCode = "34000",
        PaymentMethod = PaymentMethods.PayAtDelivery,
        Items = [new OrderItemDto { ProductId = 1, Quantity = 1 }],
        LegalAcceptances = acceptances
    };

    private static List<LegalAcceptanceDto> CreateAcceptances() =>
    [
        Acceptance(LegalDocumentTypes.PreliminaryInformation, "Test preliminary information"),
        Acceptance(LegalDocumentTypes.DistanceSalesAgreement, "Test distance sales agreement")
    ];

    private static LegalAcceptanceDto Acceptance(string type, string content) => new()
    {
        DocumentType = type,
        Version = "test-v1",
        ContentSha256 = LegalDocumentVersion.ComputeContentHash(content),
        Accepted = true
    };

    private static async Task SeedPublishedDocumentsAsync(AutoPartsDbContext context)
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
                Now);
            context.LegalDocumentVersions.Add(document);
            await context.SaveChangesAsync();
            document.Publish(1, Now);
            await context.SaveChangesAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
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
            var context = new AutoPartsDbContext(
                new DbContextOptionsBuilder<AutoPartsDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
