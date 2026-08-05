using System.Security.Claims;
using System.Text.Json;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Invoicing;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminOperationsControllerTests
{
    [Fact]
    public void IntegrationCapabilities_AreFailClosedAndDoNotExposeSecrets()
    {
        using var context = CreateContext();
        var controller = CreateController(context);

        var result = controller.GetIntegrationCapabilities();
        var capabilities = Assert.IsType<AdminIntegrationCapabilitiesDto>(result.Value);
        var json = JsonSerializer.Serialize(capabilities);

        Assert.False(capabilities.Payment.Enabled);
        Assert.False(capabilities.ElectronicInvoice.Enabled);
        Assert.False(capabilities.Email.Enabled);
        Assert.False(capabilities.OutboxDispatch.Enabled);
        Assert.False(capabilities.InventoryReservationExpiry.Enabled);
        Assert.False(capabilities.PublicSite.Enabled);
        Assert.False(capabilities.ShippingCarrier.Enabled);
        Assert.Equal("FailClosed", capabilities.Payment.Mode);
        Assert.Equal("FailClosed", capabilities.ElectronicInvoice.Mode);
        Assert.Equal("FailClosed", capabilities.Email.Mode);
        Assert.Equal("FailClosed", capabilities.OutboxDispatch.Mode);
        Assert.False(capabilities.Payment.LiveReady);
        Assert.False(capabilities.ElectronicInvoice.LiveReady);
        Assert.Equal("NotChecked", capabilities.Payment.HealthStatus);
        Assert.Equal("NotChecked", capabilities.ElectronicInvoice.HealthStatus);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp-password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperationLists_DoNotExposeIdempotencyOrExternalRefundReferences()
    {
        await using var context = CreateContext();
        var product = new Product
        {
            Id = 1,
            Name = "Safe projection product",
            Description = "Safe projection product description",
            PartNumber = "SAFE-1",
            Price = 10m,
            Stock = 1,
            CategoryId = 1,
            BrandId = 1,
            PartBrandId = 1
        };
        var order = new Order
        {
            Id = 1,
            OrderNumber = "SAFE-ORDER-1",
            CustomerName = "Private Customer",
            CustomerEmail = "private@example.test",
            CustomerPhone = "+905550000000",
            ShippingAddress = "Private shipping address",
            City = "Istanbul",
            PostalCode = "34000",
            TotalAmount = 10m,
            Status = OrderStatuses.Delivered
        };
        var orderItem = new OrderItem
        {
            Id = 1,
            Order = order,
            Product = product,
            Quantity = 1,
            Price = 10m
        };
        context.Shipments.Add(new Shipment
        {
            Order = order,
            IdempotencyKey = "secret-shipment-key",
            PayloadHash = new string('A', 64),
            Status = ShipmentStatuses.Delivered,
            Items = [new ShipmentItem { OrderItem = orderItem, Quantity = 1 }]
        });
        context.ReturnRequests.Add(new ReturnRequest
        {
            Order = order,
            IdempotencyKey = "secret-return-key",
            Status = ReturnRequestStatuses.Refunded,
            ExternalRefundRequestReference = "secret-request-reference",
            ExternalRefundConfirmationReference = "secret-confirmation-reference",
            Items = { new ReturnItem
                {
                    OrderItem = orderItem,
                    Quantity = 1,
                    ReasonCode = ReturnReasonCodes.Defective
                }
            }
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var shipments = await controller.GetShipments(default);
        var returns = await controller.GetReturns(default);
        var json = JsonSerializer.Serialize(new
        {
            Shipments = shipments.Value,
            Returns = returns.Value
        });

        Assert.Contains("SAFE-ORDER-1", json);
        Assert.DoesNotContain("secret-shipment-key", json);
        Assert.DoesNotContain("secret-return-key", json);
        Assert.DoesNotContain("secret-request-reference", json);
        Assert.DoesNotContain("secret-confirmation-reference", json);
        Assert.DoesNotContain("PayloadHash", json);
        Assert.DoesNotContain("ConcurrencyToken", json);
        Assert.DoesNotContain("private@example.test", json);
        Assert.DoesNotContain("Private shipping address", json);
    }

    [Fact]
    public async Task CreateShipment_CommitsBusinessAndIntent_AndDispatchesOneEventPerOperation()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (_, orderItem) = await AddOrderAsync(database.Context, OrderStatuses.Pending);
        var controller = CreateController(database.Context, authenticated: true);
        var dto = new CreateAdminShipmentDto
        {
            Items =
            [
                new AdminShipmentLineDto
                {
                    OrderItemId = orderItem.Id,
                    Quantity = 1
                }
            ]
        };

        var created = await controller.CreateShipment(
            orderItem.OrderId,
            "controller-shipment",
            dto,
            default);
        var replayed = await controller.CreateShipment(
            orderItem.OrderId,
            "controller-shipment",
            dto,
            default);

        Assert.Equal(
            StatusCodes.Status201Created,
            Assert.IsType<ObjectResult>(created).StatusCode);
        Assert.IsType<OkObjectResult>(replayed);

        var intents = await database.Context.AdminAuditIntents
            .OrderBy(intent => intent.Id)
            .ToListAsync();
        var events = await database.Context.AdminAuditEvents
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync();
        Assert.Equal(2, intents.Count);
        Assert.All(intents, intent => Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status));
        Assert.Equal(2, events.Count);
        Assert.Equal(AdminAuditOutcomes.Succeeded, events[0].Outcome);
        Assert.Equal(AdminAuditOutcomes.Replayed, events[1].Outcome);
        Assert.All(events, auditEvent => Assert.Equal(AdminAuditActions.ShipmentCreated, auditEvent.Action));

        var retry = await new AdminAuditIntentService(database.Context).DispatchBatchAsync(
            new AdminAuditService(database.Context));
        Assert.Equal(0, retry.Claimed);
        Assert.Equal(2, await database.Context.AdminAuditEvents.CountAsync());
    }

    [Fact]
    public async Task AuditIdentityFailure_RollsBackShipmentAndIntentTogether()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (order, orderItem) = await AddOrderAsync(database.Context, OrderStatuses.Pending);
        var controller = CreateController(database.Context, authenticated: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateShipment(
            order.Id,
            "rollback-shipment",
            new CreateAdminShipmentDto
            {
                Items =
                [
                    new AdminShipmentLineDto
                    {
                        OrderItemId = orderItem.Id,
                        Quantity = 1
                    }
                ]
            },
            default));

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.Shipments.ToListAsync());
        Assert.Empty(await database.Context.AdminAuditIntents.ToListAsync());
        Assert.Equal(
            OrderStatuses.Pending,
            await database.Context.Orders
                .Where(candidate => candidate.Id == order.Id)
                .Select(candidate => candidate.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task CreateReturn_CommitsBusinessAndIntent_AndAuditsReplay()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (order, orderItem) = await AddOrderAsync(database.Context, OrderStatuses.Delivered);
        var controller = CreateController(database.Context, authenticated: true);
        var dto = new CreateAdminReturnDto
        {
            Items =
            [
                new AdminReturnLineDto
                {
                    OrderItemId = orderItem.Id,
                    Quantity = 1,
                    ReasonCode = ReturnReasonCodes.Defective
                }
            ]
        };

        var created = await controller.CreateReturn(
            order.Id,
            "controller-return",
            dto,
            default);
        var replayed = await controller.CreateReturn(
            order.Id,
            "controller-return",
            dto,
            default);

        Assert.Equal(
            StatusCodes.Status201Created,
            Assert.IsType<ObjectResult>(created).StatusCode);
        Assert.IsType<OkObjectResult>(replayed);
        var events = await database.Context.AdminAuditEvents
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(AdminAuditOutcomes.Succeeded, events[0].Outcome);
        Assert.Equal(AdminAuditOutcomes.Replayed, events[1].Outcome);
        Assert.All(events, auditEvent => Assert.Equal(AdminAuditActions.ReturnCreated, auditEvent.Action));
        Assert.All(
            await database.Context.AdminAuditIntents.ToListAsync(),
            intent => Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status));
    }

    [Fact]
    public async Task AuditIdentityFailure_RollsBackReturnAndIntentTogether()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (order, orderItem) = await AddOrderAsync(database.Context, OrderStatuses.Delivered);
        var controller = CreateController(database.Context, authenticated: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreateReturn(
            order.Id,
            "rollback-return",
            new CreateAdminReturnDto
            {
                Items =
                [
                    new AdminReturnLineDto
                    {
                        OrderItemId = orderItem.Id,
                        Quantity = 1,
                        ReasonCode = ReturnReasonCodes.Defective
                    }
                ]
            },
            default));

        database.Context.ChangeTracker.Clear();
        Assert.Empty(await database.Context.ReturnRequests.ToListAsync());
        Assert.Empty(await database.Context.AdminAuditIntents.ToListAsync());
    }

    [Fact]
    public async Task ImmediateDispatchFailure_DoesNotChangeCommittedBusinessResult()
    {
        await using var context = CreateContext();
        var (order, orderItem) = await AddOrderAsync(context, OrderStatuses.Pending);
        var controller = CreateController(context, authenticated: true);

        var result = await controller.CreateShipment(
            order.Id,
            "best-effort-shipment",
            new CreateAdminShipmentDto
            {
                Items =
                [
                    new AdminShipmentLineDto
                    {
                        OrderItemId = orderItem.Id,
                        Quantity = 1
                    }
                ]
            },
            default);

        Assert.Equal(
            StatusCodes.Status201Created,
            Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Single(await context.Shipments.ToListAsync());
        var intent = Assert.Single(await context.AdminAuditIntents.ToListAsync());
        Assert.Equal(AdminAuditIntentStatuses.Pending, intent.Status);
        Assert.Empty(await context.AdminAuditEvents.ToListAsync());
    }

    public static TheoryData<string, string> ShipmentStatusTransitions => new()
    {
        { ShipmentStatuses.LabelPending, ShipmentStatuses.Created },
        { ShipmentStatuses.ReadyToShip, ShipmentStatuses.Created },
        { ShipmentStatuses.Shipped, ShipmentStatuses.ReadyToShip },
        { ShipmentStatuses.Delivered, ShipmentStatuses.Shipped },
        { ShipmentStatuses.Failed, ShipmentStatuses.Created },
        { ShipmentStatuses.Cancelled, ShipmentStatuses.Created }
    };

    [Theory]
    [MemberData(nameof(ShipmentStatusTransitions))]
    public async Task ShipmentStatusMutation_AndReplay_EachDispatchExactlyOneAuditEvent(
        string targetStatus,
        string initialStatus)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (order, _) = await AddOrderAsync(database.Context, OrderStatuses.Processing);
        var shipment = new Shipment
        {
            Order = order,
            IdempotencyKey = $"shipment-status-{targetStatus}",
            PayloadHash = new string('a', 64),
            Status = initialStatus,
            Carrier = initialStatus == ShipmentStatuses.Shipped ? "CARRIER" : null,
            TrackingNumber = initialStatus == ShipmentStatuses.Shipped ? "TRACKING" : null,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        database.Context.Shipments.Add(shipment);
        await database.Context.SaveChangesAsync();
        var controller = CreateController(database.Context, authenticated: true);

        Assert.IsType<OkObjectResult>(await InvokeShipmentTransition(controller, shipment.Id, targetStatus));
        Assert.IsType<OkObjectResult>(await InvokeShipmentTransition(controller, shipment.Id, targetStatus));

        var events = await database.Context.AdminAuditEvents
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(AdminAuditOutcomes.Succeeded, events[0].Outcome);
        Assert.Equal(AdminAuditOutcomes.Replayed, events[1].Outcome);
        Assert.All(
            events,
            auditEvent => Assert.Equal(
                AdminAuditActions.ForShipmentStatus(targetStatus),
                auditEvent.Action));
        Assert.Equal(2, await database.Context.AdminAuditIntents.CountAsync());
    }

    public static TheoryData<string, string> ReturnStatusTransitions => new()
    {
        { ReturnRequestStatuses.Approved, ReturnRequestStatuses.Requested },
        { ReturnRequestStatuses.Rejected, ReturnRequestStatuses.Requested },
        { ReturnRequestStatuses.Received, ReturnRequestStatuses.Approved },
        { ReturnRequestStatuses.Inspected, ReturnRequestStatuses.Received },
        { ReturnRequestStatuses.Cancelled, ReturnRequestStatuses.Requested },
        { ReturnRequestStatuses.Closed, ReturnRequestStatuses.Refunded }
    };

    [Theory]
    [MemberData(nameof(ReturnStatusTransitions))]
    public async Task ReturnStatusMutation_AndReplay_EachDispatchExactlyOneAuditEvent(
        string targetStatus,
        string initialStatus)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var (order, _) = await AddOrderAsync(database.Context, OrderStatuses.Delivered);
        var request = new ReturnRequest
        {
            Order = order,
            IdempotencyKey = $"return-status-{targetStatus}",
            Status = initialStatus,
            RequestedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        };
        database.Context.ReturnRequests.Add(request);
        await database.Context.SaveChangesAsync();
        var controller = CreateController(database.Context, authenticated: true);

        Assert.IsType<OkObjectResult>(await InvokeReturnTransition(controller, request.Id, targetStatus));
        Assert.IsType<OkObjectResult>(await InvokeReturnTransition(controller, request.Id, targetStatus));

        var events = await database.Context.AdminAuditEvents
            .OrderBy(auditEvent => auditEvent.Sequence)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(AdminAuditOutcomes.Succeeded, events[0].Outcome);
        Assert.Equal(AdminAuditOutcomes.Replayed, events[1].Outcome);
        Assert.All(
            events,
            auditEvent => Assert.Equal(
                AdminAuditActions.ForReturnStatus(targetStatus),
                auditEvent.Action));
        Assert.Equal(2, await database.Context.AdminAuditIntents.CountAsync());
    }

    private static Task<IActionResult> InvokeShipmentTransition(
        AdminOperationsController controller,
        int shipmentId,
        string targetStatus) => targetStatus switch
        {
            ShipmentStatuses.LabelPending => controller.MarkLabelPending(shipmentId, default),
            ShipmentStatuses.ReadyToShip => controller.MarkReadyToShip(shipmentId, default),
            ShipmentStatuses.Shipped => controller.MarkShipped(
                shipmentId,
                new ShipAdminShipmentDto
                {
                    Carrier = "carrier",
                    TrackingNumber = "tracking"
                },
                default),
            ShipmentStatuses.Delivered => controller.MarkDelivered(shipmentId, default),
            ShipmentStatuses.Failed => controller.MarkShipmentFailed(shipmentId, default),
            ShipmentStatuses.Cancelled => controller.CancelShipment(shipmentId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(targetStatus))
        };

    private static Task<IActionResult> InvokeReturnTransition(
        AdminOperationsController controller,
        long returnRequestId,
        string targetStatus) => targetStatus switch
        {
            ReturnRequestStatuses.Approved => controller.ApproveReturn(returnRequestId, default),
            ReturnRequestStatuses.Rejected => controller.RejectReturn(returnRequestId, default),
            ReturnRequestStatuses.Received => controller.ReceiveReturn(returnRequestId, default),
            ReturnRequestStatuses.Inspected => controller.InspectReturn(returnRequestId, default),
            ReturnRequestStatuses.Cancelled => controller.CancelReturn(returnRequestId, default),
            ReturnRequestStatuses.Closed => controller.CloseReturn(returnRequestId, default),
            _ => throw new ArgumentOutOfRangeException(nameof(targetStatus))
        };

    private static async Task<(Order Order, OrderItem OrderItem)> AddOrderAsync(
        AutoPartsDbContext context,
        string status)
    {
        var order = new Order
        {
            OrderNumber = $"ADMIN-OPS-{Guid.NewGuid():N}",
            CustomerName = "Admin Operations Test",
            CustomerEmail = "admin-operations@example.test",
            CustomerPhone = "+905551112233",
            ShippingAddress = "Test Mahallesi Test Sokak No 1",
            City = "Istanbul",
            PostalCode = "34000",
            TotalAmount = 100m,
            Status = status,
            OrderDate = DateTime.UtcNow.AddDays(-2)
        };
        var orderItem = new OrderItem
        {
            ProductId = 1,
            Quantity = 1,
            Price = 100m
        };
        order.OrderItems.Add(orderItem);
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return (order, orderItem);
    }

    private static AdminOperationsController CreateController(
        AutoPartsDbContext context,
        bool authenticated = false)
    {
        var controller = new AdminOperationsController(
            context,
            new FulfillmentService(context),
            new ReturnService(context),
            new DisabledPaymentGateway(),
            new DisabledInvoiceGateway(),
            new DisabledOutboxMessageDispatcher(),
            new OutboxWorkerOptions(),
            new InventoryReservationExpiryOptions(),
            new PublicSiteOptions(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EmailSettings:SmtpServer"] = "smtp.example.test",
                    ["EmailSettings:SmtpPort"] = "587",
                    ["EmailSettings:SenderEmail"] = string.Empty,
                    ["EmailSettings:Username"] = string.Empty,
                    ["EmailSettings:Password"] = "smtp-password"
                })
                .Build(),
            TimeProvider.System,
            new AdminAuditService(context),
            new AdminAuditIntentService(context),
            NullLogger<AdminOperationsController>.Instance);
        var claims = authenticated
            ? new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim(ClaimTypes.Role, AdminAuditRoles.SuperAdmin)
            }
            : [];
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                TraceIdentifier = $"admin-operations-{Guid.NewGuid():N}",
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    private static AutoPartsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AutoPartsDbContext(options);
    }

    private sealed class SqliteTestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteTestDatabase(
            AutoPartsDbContext context,
            SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AutoPartsDbContext Context { get; }

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new SqliteTestDatabase(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
