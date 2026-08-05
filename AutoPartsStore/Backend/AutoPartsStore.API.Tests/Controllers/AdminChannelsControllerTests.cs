using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
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

public sealed class AdminChannelsControllerTests
{
    [Fact]
    public void EndpointsHaveExplicitLeastPrivilegePolicies()
    {
        var expected = new Dictionary<string, string>
        {
            [nameof(AdminChannelsController.GetChannels)] = AdminPolicyNames.AdminAccess,
            [nameof(AdminChannelsController.UpdateChannelState)] = AdminPolicyNames.SuperAdmin,
            [nameof(AdminChannelsController.RefreshListing)] = AdminPolicyNames.SuperAdmin
        };

        Assert.NotNull(typeof(AdminChannelsController).GetCustomAttribute<AuthorizeAttribute>());
        foreach (var endpoint in expected)
        {
            var method = typeof(AdminChannelsController).GetMethod(endpoint.Key);
            var authorize = Assert.Single(method!.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Equal(endpoint.Value, authorize.Policy);
        }
    }

    [Fact]
    public async Task CapabilitiesAreFailClosedAndDoNotExposeCredentials()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = database.Controller();

        var result = Assert.IsType<OkObjectResult>(await controller.GetChannels(CancellationToken.None));
        var json = JsonSerializer.Serialize(result.Value);

        Assert.Contains("adapter-not-configured", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CredentialValue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnableWithoutAdapterReturnsUnavailableWithoutMutationOrAudit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = database.Controller();
        var channel = await database.Context.SalesChannels.AsNoTracking().SingleAsync(
            candidate => candidate.Code == SalesChannelCodes.Trendyol);

        var action = await controller.UpdateChannelState(
            channel.Id,
            new UpdateSalesChannelStateDto
            {
                RequestedEnabled = true,
                Mode = SalesChannelModes.Sandbox,
                ConcurrencyToken = channel.ConcurrencyToken
            },
            CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.False((await database.Context.SalesChannels.AsNoTracking().SingleAsync(
            candidate => candidate.Id == channel.Id)).RequestedEnabled);
        Assert.Empty(await database.Context.AdminAuditEvents.ToListAsync());
        Assert.Empty(await database.Context.AdminAuditIntents.ToListAsync());
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DisabledSalesChannelAdapterRegistry _registry = new();

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

        public AdminChannelsController Controller()
        {
            var time = TimeProvider.System;
            var controller = new AdminChannelsController(
                Context,
                new SalesChannelService(Context, _registry, time),
                _registry,
                new AdminAuditIntentService(Context, time),
                new AdminAuditService(Context, time),
                new AdminAuditIntentOptions(),
                NullLogger<AdminChannelsController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, "1"),
                            new Claim(ClaimTypes.Role, AdminAuditRoles.LegacyAdmin)
                        ], "AdminChannelsTest"))
                    }
                }
            };
            controller.HttpContext.TraceIdentifier = "admin-channels-test-trace";
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
