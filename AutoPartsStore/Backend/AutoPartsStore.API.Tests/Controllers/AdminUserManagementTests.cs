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

public sealed class AdminUserManagementTests
{
    [Fact]
    public async Task UpdateUserRole_ChangesAllowlistedRoleAndAuditsWithoutSensitiveData()
    {
        await using var database = await TestDatabase.CreateAsync();
        var actor = await database.AddUserAsync("actor@example.test", AdminAuditRoles.LegacyAdmin);
        var target = await database.AddUserAsync("target@example.test", "User");
        var controller = database.CreateController(actor);

        var result = await controller.UpdateUserRole(
            target.Id,
            new UpdateUserRoleDto { Role = " FINANCE " },
            default);

        Assert.IsType<NoContentResult>(result);
        var updated = await database.Context.Users.AsNoTracking().SingleAsync(user => user.Id == target.Id);
        Assert.Equal(AdminAuditRoles.Finance, updated.Role);
        var audit = await database.Context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditActions.UserRoleChanged, audit.Action);
        Assert.Equal(AdminAuditAggregateTypes.User, audit.AggregateType);
        Assert.Equal(target.Id, audit.AggregateId);
        Assert.DoesNotContain(target.Email, audit.EventHashSha256, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateUserRole_DoesNotDemoteLastPrivilegedAdministrator()
    {
        await using var database = await TestDatabase.CreateAsync();
        var actor = await database.AddUserAsync("only-admin@example.test", AdminAuditRoles.LegacyAdmin);
        var controller = database.CreateController(actor);

        var result = await controller.UpdateUserRole(
            actor.Id,
            new UpdateUserRoleDto { Role = "User" },
            default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(
            AdminAuditRoles.LegacyAdmin,
            (await database.Context.Users.AsNoTracking().SingleAsync(user => user.Id == actor.Id)).Role);
        Assert.Empty(await database.Context.AdminAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateUserRole_RejectsUnknownRoleBeforeMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var actor = await database.AddUserAsync("admin@example.test", AdminAuditRoles.LegacyAdmin);
        var target = await database.AddUserAsync("target@example.test", "User");

        var result = await database.CreateController(actor).UpdateUserRole(
            target.Id,
            new UpdateUserRoleDto { Role = "owner" },
            default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "User",
            (await database.Context.Users.AsNoTracking().SingleAsync(user => user.Id == target.Id)).Role);
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

        public async Task<User> AddUserAsync(string email, string role)
        {
            var user = new User
            {
                Email = email,
                Password = "not-a-real-password-hash",
                FullName = "Admin role test",
                Phone = string.Empty,
                Role = role,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            Context.Users.Add(user);
            await Context.SaveChangesAsync();
            return user;
        }

        public AdminController CreateController(User actor)
        {
            var controller = new AdminController(
                Context,
                new OrderLifecycleService(Context),
                new AdminAuditService(Context));
            var httpContext = new DefaultHttpContext
            {
                TraceIdentifier = "admin-role-test-correlation"
            };
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()),
                new Claim(ClaimTypes.Role, actor.Role)
            ], "test"));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
