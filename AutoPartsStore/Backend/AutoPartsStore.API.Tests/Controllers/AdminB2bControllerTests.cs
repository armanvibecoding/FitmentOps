using System.Reflection;
using System.Security.Claims;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminB2bControllerTests
{
    [Fact]
    public void EveryAdminB2bEndpoint_HasExplicitLeastPrivilegePolicy()
    {
        var expected = new Dictionary<string, string>
        {
            [nameof(AdminB2bController.GetApplications)] = AdminPolicyNames.SuperAdmin,
            [nameof(AdminB2bController.ReviewApplication)] = AdminPolicyNames.SuperAdmin,
            [nameof(AdminB2bController.GetPricing)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.CreateCustomerGroup)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.UpdateCustomerGroup)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.CreatePriceList)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.UpdatePriceList)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.CreatePriceRule)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.UpdatePriceRule)] = AdminPolicyNames.Finance,
            [nameof(AdminB2bController.GetQuotes)] = AdminPolicyNames.Support,
            [nameof(AdminB2bController.PrepareQuote)] = AdminPolicyNames.Support,
            [nameof(AdminB2bController.GetSuppliers)] = AdminPolicyNames.Warehouse,
            [nameof(AdminB2bController.CreateSupplier)] = AdminPolicyNames.Warehouse,
            [nameof(AdminB2bController.UpdateSupplier)] = AdminPolicyNames.Warehouse,
            [nameof(AdminB2bController.RegisterSupplierOffer)] = AdminPolicyNames.Warehouse,
            [nameof(AdminB2bController.SetSupplierOfferActive)] = AdminPolicyNames.Warehouse,
            [nameof(AdminB2bController.SelectSupplierSource)] = AdminPolicyNames.Warehouse
        };

        Assert.NotNull(typeof(AdminB2bController).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var endpoint in expected)
        {
            var method = typeof(AdminB2bController).GetMethod(endpoint.Key);
            var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Equal(endpoint.Value, authorize.Policy);
        }
    }

    [Fact]
    public async Task SupplierMutation_CommitsBusinessAndDurableAuditIntentTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AutoPartsDbContext(
            new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options);
        await context.Database.EnsureCreatedAsync();
        var controller = CreateController(context);

        var result = await controller.CreateSupplier(
            new CreateSupplierDto
            {
                Code = "supplier-one",
                Name = "Supplier One",
                HealthStatus = SupplierHealthStatuses.Healthy,
                Priority = 1,
                IsActive = true
            },
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal(1, await context.Suppliers.CountAsync());
        var intent = await context.AdminAuditIntents.AsNoTracking().SingleAsync();
        var auditEvent = await context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(AdminAuditActions.SupplierUpserted, auditEvent.Action);
        Assert.Equal(AdminAuditAggregateTypes.Supplier, auditEvent.AggregateType);
    }

    [Fact]
    public async Task SupplierUpdate_UsesConcurrencyToken_AndAuditsOnlyCommittedChange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AutoPartsDbContext(
            new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options);
        await context.Database.EnsureCreatedAsync();
        var controller = CreateController(context);

        await controller.CreateSupplier(
            new CreateSupplierDto
            {
                Code = "supplier-two",
                Name = "Supplier Two",
                HealthStatus = SupplierHealthStatuses.Healthy,
                Priority = 2,
                IsActive = true
            },
            CancellationToken.None);
        var created = await context.Suppliers.AsNoTracking().SingleAsync();

        var updateResult = await controller.UpdateSupplier(
            created.Id,
            new UpdateSupplierDto
            {
                Name = "Supplier Two Updated",
                HealthStatus = SupplierHealthStatuses.Degraded,
                Priority = 1,
                IsActive = false,
                ConcurrencyToken = created.ConcurrencyToken
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResult);
        var updated = await context.Suppliers.AsNoTracking().SingleAsync();
        Assert.Equal("Supplier Two Updated", updated.Name);
        Assert.Equal(SupplierHealthStatuses.Degraded, updated.HealthStatus);
        Assert.False(updated.IsActive);
        Assert.NotEqual(created.ConcurrencyToken, updated.ConcurrencyToken);
        Assert.Equal(2, await context.AdminAuditEvents.CountAsync());

        var staleResult = await controller.UpdateSupplier(
            created.Id,
            new UpdateSupplierDto
            {
                Name = "Stale Write",
                HealthStatus = SupplierHealthStatuses.Healthy,
                Priority = 0,
                IsActive = true,
                ConcurrencyToken = created.ConcurrencyToken
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(staleResult);
        Assert.Equal(2, await context.AdminAuditEvents.CountAsync());
        Assert.Equal("Supplier Two Updated", (await context.Suppliers.AsNoTracking().SingleAsync()).Name);
    }

    private static AdminB2bController CreateController(AutoPartsDbContext context)
    {
        var time = TimeProvider.System;
        var intentService = new AdminAuditIntentService(context, time);
        var auditService = new AdminAuditService(context, time);
        var controller = new AdminB2bController(
            context,
            new DealerApplicationService(context, time),
            new BulkQuoteService(context, time),
            new SupplierSourcingService(context, time),
            intentService,
            auditService,
            new AdminAuditIntentOptions(),
            NullLogger<AdminB2bController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "1"),
                        new Claim(ClaimTypes.Role, AdminAuditRoles.LegacyAdmin)
                    ], "AdminB2bTest"))
                }
            }
        };
        controller.HttpContext.TraceIdentifier = "admin-b2b-test-trace";
        return controller;
    }
}
