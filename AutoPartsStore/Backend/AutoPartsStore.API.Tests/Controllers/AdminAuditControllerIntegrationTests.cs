using System.Security.Claims;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminAuditControllerIntegrationTests
{
    [Fact]
    public async Task UpdateProduct_SameClientCorrelationAndAggregate_AppendsTwoAuditEvents()
    {
        const string clientCorrelationId = "client-selected-correlation";
        await using var database = await ControllerDatabase.CreateAsync();

        await using (var firstContext = database.CreateContext())
        {
            var product = await firstContext.Products.AsNoTracking().SingleAsync(item => item.Id == 1);
            var controller = CreateController(firstContext, clientCorrelationId);

            var result = await controller.UpdateProduct(
                product.Id,
                ToUpdateRequest(product, "First audited update", product.Stock + 1));

            Assert.IsType<NoContentResult>(result);
        }

        await using (var secondContext = database.CreateContext())
        {
            var product = await secondContext.Products.AsNoTracking().SingleAsync(item => item.Id == 1);
            var controller = CreateController(secondContext, clientCorrelationId);

            var result = await controller.UpdateProduct(
                product.Id,
                ToUpdateRequest(product, "Second distinct audited update", product.Stock + 1));

            Assert.IsType<NoContentResult>(result);
        }

        await using var verificationContext = database.CreateContext();
        var auditEvents = await verificationContext.AdminAuditEvents
            .AsNoTracking()
            .Where(auditEvent =>
                auditEvent.Action == AdminAuditActions.ProductUpdated &&
                auditEvent.AggregateType == AdminAuditAggregateTypes.Product &&
                auditEvent.AggregateId == 1)
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync();

        Assert.Equal(2, auditEvents.Count);
        Assert.Equal(2, auditEvents.Select(item => item.IdempotencyKeySha256).Distinct().Count());
        Assert.Single(auditEvents.Select(item => item.CorrelationIdSha256).Distinct());
        Assert.All(auditEvents, item =>
        {
            Assert.Equal(42, item.ActorUserId);
            Assert.Equal(AdminAuditRoles.LegacyAdmin, item.ActorRole);
            Assert.Equal(AdminAuditOutcomes.Succeeded, item.Outcome);
        });
        Assert.Equal(auditEvents[0].EventHashSha256, auditEvents[1].PreviousEventHashSha256);

        var intents = await verificationContext.AdminAuditIntents
            .AsNoTracking()
            .Where(intent =>
                intent.Action == AdminAuditActions.ProductUpdated &&
                intent.AggregateType == AdminAuditAggregateTypes.Product &&
                intent.AggregateId == 1)
            .ToListAsync();
        Assert.Equal(2, intents.Count);
        Assert.All(intents, intent =>
            Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status));

        var persistedProduct = await verificationContext.Products
            .AsNoTracking()
            .SingleAsync(item => item.Id == 1);
        Assert.Equal("Second distinct audited update", persistedProduct.Name);
    }

    [Fact]
    public async Task CreateProduct_InvalidAuditIdentity_RollsBackProductAndIntentAtomically()
    {
        await using var database = await ControllerDatabase.CreateAsync();
        int originalProductCount;
        Product sourceProduct;
        await using (var setupContext = database.CreateContext())
        {
            originalProductCount = await setupContext.Products.CountAsync();
            sourceProduct = await setupContext.Products.AsNoTracking().FirstAsync();
        }

        await using (var mutationContext = database.CreateContext())
        {
            var controller = CreateController(
                mutationContext,
                "atomic-rollback-correlation",
                actorRole: "not-an-audit-role");
            var request = new ProductCreateDto
            {
                Name = "Must roll back",
                Description = sourceProduct.Description,
                BrandId = sourceProduct.BrandId,
                PartBrandId = sourceProduct.PartBrandId,
                PartNumber = $"ROLLBACK-{Guid.NewGuid():N}",
                Price = sourceProduct.Price,
                OldPrice = sourceProduct.OldPrice,
                Stock = 1,
                ImageUrl = sourceProduct.ImageUrl,
                CategoryId = sourceProduct.CategoryId
            };

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await controller.CreateProduct(request));
        }

        await using var verificationContext = database.CreateContext();
        Assert.Equal(originalProductCount, await verificationContext.Products.CountAsync());
        Assert.Empty(await verificationContext.AdminAuditIntents.ToListAsync());
        Assert.Empty(await verificationContext.AdminAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateProduct_ImmediateDispatchFailure_PreservesBusinessResultAndDurableIntent()
    {
        await using var database = await ControllerDatabase.CreateAsync();
        await using (var mutationContext = database.CreateContext())
        {
            var product = await mutationContext.Products.AsNoTracking().SingleAsync(item => item.Id == 1);
            var controller = CreateController(
                mutationContext,
                "dispatch-failure-correlation",
                intentOptions: new AdminAuditIntentOptions { MaxBatchSize = 0 });

            var result = await controller.UpdateProduct(
                product.Id,
                ToUpdateRequest(product, "Committed despite dispatch failure", product.Stock + 1));

            Assert.IsType<NoContentResult>(result);
        }

        await using (var committedContext = database.CreateContext())
        {
            Assert.Equal(
                "Committed despite dispatch failure",
                (await committedContext.Products.AsNoTracking().SingleAsync(item => item.Id == 1)).Name);
            var pendingIntent = await committedContext.AdminAuditIntents.AsNoTracking().SingleAsync();
            Assert.Equal(AdminAuditIntentStatuses.Pending, pendingIntent.Status);
            Assert.Empty(await committedContext.AdminAuditEvents.ToListAsync());
        }

        await using var retryContext = database.CreateContext();
        var retry = await new AdminAuditIntentService(retryContext)
            .DispatchBatchAsync(new AdminAuditService(retryContext));
        Assert.Equal(1, retry.Succeeded);
        Assert.Equal(1, await retryContext.AdminAuditEvents.CountAsync());
        Assert.Equal(
            AdminAuditIntentStatuses.Succeeded,
            (await retryContext.AdminAuditIntents.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task UpdateOrderStatus_ReplayPersistsSucceededIntentAndSingleEvent()
    {
        await using var database = await ControllerDatabase.CreateAsync();
        int orderId;
        await using (var setupContext = database.CreateContext())
        {
            var order = new Order
            {
                OrderNumber = $"REPLAY-{Guid.NewGuid():N}",
                CustomerName = "Audit replay customer",
                CustomerEmail = "audit-replay@example.test",
                CustomerPhone = "+905551112233",
                ShippingAddress = "Audit replay test address",
                City = "Istanbul",
                PostalCode = "34000",
                TotalAmount = 100m,
                Status = OrderStatuses.Processing,
                OrderDate = DateTime.UtcNow
            };
            setupContext.Orders.Add(order);
            await setupContext.SaveChangesAsync();
            orderId = order.Id;
        }

        await using (var mutationContext = database.CreateContext())
        {
            var controller = CreateController(mutationContext, "order-replay-correlation");
            var result = await controller.UpdateOrderStatus(
                orderId,
                new UpdateOrderStatusDto { Status = OrderStatuses.Processing },
                default);

            Assert.IsType<NoContentResult>(result);
        }

        await using var verificationContext = database.CreateContext();
        var intent = await verificationContext.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(AdminAuditOutcomes.Replayed, intent.Outcome);
        Assert.Equal(AdminAuditActions.OrderProcessing, intent.Action);

        var auditEvent = await verificationContext.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditOutcomes.Replayed, auditEvent.Outcome);
        Assert.Equal(AdminAuditActions.OrderProcessing, auditEvent.Action);
        Assert.Equal(orderId, auditEvent.AggregateId);

        var duplicateDispatch = await new AdminAuditIntentService(verificationContext)
            .DispatchBatchAsync(new AdminAuditService(verificationContext));
        Assert.Equal(0, duplicateDispatch.Claimed);
        Assert.Equal(1, await verificationContext.AdminAuditEvents.CountAsync());
    }

    private static AdminController CreateController(
        AutoPartsDbContext context,
        string traceIdentifier,
        string actorRole = AdminAuditRoles.LegacyAdmin,
        AdminAuditIntentOptions? intentOptions = null)
    {
        var controller = new AdminController(
            context,
            new OrderLifecycleService(context),
            new AdminAuditService(context),
            new AdminAuditIntentService(context),
            intentOptions);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                TraceIdentifier = traceIdentifier,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "42"),
                    new Claim(ClaimTypes.Role, actorRole)
                ],
                authenticationType: "AuditControllerIntegrationTest"))
            }
        };
        return controller;
    }

    private static ProductUpdateDto ToUpdateRequest(
        Product product,
        string name,
        int stock)
    {
        return new ProductUpdateDto
        {
            Name = name,
            Description = product.Description,
            BrandId = product.BrandId,
            PartBrandId = product.PartBrandId,
            PartNumber = product.PartNumber,
            Price = product.Price,
            OldPrice = product.OldPrice,
            Stock = stock,
            ImageUrl = product.ImageUrl,
            DiscountPercentage = product.DiscountPercentage,
            BadgeText = product.BadgeText,
            IsFeatured = product.IsFeatured,
            IsNew = product.IsNew,
            CategoryId = product.CategoryId
        };
    }

    private sealed class ControllerDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private ControllerDatabase(SqliteConnection keeperConnection, string connectionString)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
        }

        public static async Task<ControllerDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=admin-audit-controller-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var database = new ControllerDatabase(keeperConnection, connectionString);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public AutoPartsDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AutoPartsDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }
}
