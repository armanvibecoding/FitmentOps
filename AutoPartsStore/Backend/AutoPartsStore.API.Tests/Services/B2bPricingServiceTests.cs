using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class B2bPricingServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DealerApplication_IsIdempotentPrivateAndRequiresExplicitApproval()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = await database.AddUserAsync("dealer@example.test");
        var service = database.ApplicationService();
        var command = Application(user.Id, "dealer-application-0001");

        var submitted = await service.SubmitAsync(command);
        var replay = await service.SubmitAsync(command);
        var conflict = await service.SubmitAsync(command with { TaxNumber = "9999999999" });
        var application = await database.Context.DealerApplications.AsNoTracking().SingleAsync();
        var json = JsonSerializer.Serialize(application);

        Assert.Equal(DealerApplicationOutcome.Submitted, submitted.Outcome);
        Assert.Equal(DealerApplicationOutcome.Replayed, replay.Outcome);
        Assert.Equal(DealerApplicationOutcome.Conflict, conflict.Outcome);
        Assert.Equal(DealerApplicationStatuses.Pending, application.Status);
        Assert.DoesNotContain(command.TaxNumber, json, StringComparison.Ordinal);
        Assert.DoesNotContain(command.ContactEmail, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IdempotencyKey", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_RequiresActiveGroupAndEnforcesLifecycle()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = await database.AddUserAsync("review@example.test");
        var application = await database.ApplicationService().SubmitAsync(
            Application(user.Id, "dealer-application-0002"));
        var inactiveGroup = await database.AddGroupAsync("inactive", isActive: false);
        var activeGroup = await database.AddGroupAsync("active", isActive: true);

        var invalid = await database.ApplicationService().ReviewAsync(
            application.ApplicationId!.Value,
            DealerReviewDecision.Approve,
            inactiveGroup.Id);
        var approved = await database.ApplicationService().ReviewAsync(
            application.ApplicationId.Value,
            DealerReviewDecision.Approve,
            activeGroup.Id);
        var duplicateApproval = await database.ApplicationService().ReviewAsync(
            application.ApplicationId.Value,
            DealerReviewDecision.Approve,
            activeGroup.Id);
        var suspended = await database.ApplicationService().ReviewAsync(
            application.ApplicationId.Value,
            DealerReviewDecision.Suspend,
            activeGroup.Id);

        Assert.Equal(DealerApplicationOutcome.InvalidRequest, invalid.Outcome);
        Assert.Equal(DealerApplicationStatuses.Approved, approved.Status);
        Assert.Equal(DealerApplicationOutcome.Conflict, duplicateApproval.Outcome);
        Assert.Equal(DealerApplicationStatuses.Suspended, suspended.Status);
    }

    [Fact]
    public async Task Pricing_ReturnsBaseForUnapprovedDealer()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = await database.AddUserAsync("pending@example.test");
        await database.ApplicationService().SubmitAsync(
            Application(user.Id, "dealer-application-0003"));

        var result = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(user.Id, 1, 3, 10_000m, "TRY"));

        Assert.Equal(B2bPriceOutcome.NotEligible, result.Outcome);
        Assert.Equal(result.UnitPrice * 3, result.LineTotal);
    }

    [Fact]
    public async Task Pricing_UsesPriorityThenSpecificityDeterministically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("priced@example.test");
        var priceList = await database.AddPriceListAsync(dealer.GroupId);
        var generic = await database.AddRuleAsync(priceList.Id, priority: 10, discount: 5m);
        var specific = await database.AddRuleAsync(priceList.Id, priority: 10, discount: 20m, productId: 1);
        await database.AddRuleAsync(priceList.Id, priority: 5, discount: 50m, productId: 1);

        var result = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(dealer.UserId, 1, 2, 0m, "TRY", Now));

        Assert.Equal(B2bPriceOutcome.Priced, result.Outcome);
        Assert.Equal(specific.Id, result.AppliedRuleId);
        Assert.NotEqual(generic.Id, result.AppliedRuleId);
        Assert.Equal(decimal.Round(80m * (await database.ProductPriceAsync(1)) / 100m, 2), result.UnitPrice);
    }

    [Fact]
    public async Task Pricing_EnforcesQuantityRevenueAndValidityBoundaries()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("threshold@example.test");
        var priceList = await database.AddPriceListAsync(dealer.GroupId);
        var rule = await database.AddRuleAsync(
            priceList.Id,
            priority: 1,
            discount: null,
            fixedPrice: 42m,
            minimumQuantity: 5,
            minimumRevenue: 1_000m,
            validFrom: Now.UtcDateTime,
            validTo: Now.AddHours(1).UtcDateTime);

        var below = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(dealer.UserId, 1, 4, 1_000m, "TRY", Now));
        var exact = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(dealer.UserId, 1, 5, 1_000m, "TRY", Now));
        var expired = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(dealer.UserId, 1, 5, 1_000m, "TRY", Now.AddHours(1)));

        Assert.Equal(B2bPriceOutcome.BasePrice, below.Outcome);
        Assert.Equal(B2bPriceOutcome.Priced, exact.Outcome);
        Assert.Equal(rule.Id, exact.AppliedRuleId);
        Assert.Equal(42m, exact.UnitPrice);
        Assert.Equal(B2bPriceOutcome.BasePrice, expired.Outcome);
    }

    [Fact]
    public async Task Pricing_RejectsClientCurrencyAndRevenueAbuseInputs()
    {
        await using var database = await TestDatabase.CreateAsync();

        var currency = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(1, 1, 1, 0m, "USD"));
        var negativeRevenue = await database.PricingService().CalculateAsync(
            new B2bPriceRequest(1, 1, 1, -1m, "TRY"));

        Assert.Equal(B2bPriceOutcome.InvalidRequest, currency.Outcome);
        Assert.Equal(B2bPriceOutcome.InvalidRequest, negativeRevenue.Outcome);
        Assert.Equal(0m, currency.UnitPrice);
    }

    private static DealerApplicationCommand Application(int userId, string key) => new(
        userId,
        key,
        "Test Parts Ltd",
        "1234567890",
        "Dealer Contact",
        "dealer-contact@example.test",
        "+905551112233");

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

        public async Task<User> AddUserAsync(string email)
        {
            var user = new User
            {
                Email = email,
                Password = "test-password-hash",
                FullName = "Test Dealer",
                Phone = "+905551112233",
                Role = "User",
                IsActive = true,
                CreatedAt = Now.UtcDateTime
            };
            Context.Users.Add(user);
            await Context.SaveChangesAsync();
            return user;
        }

        public async Task<CustomerGroup> AddGroupAsync(string code, bool isActive)
        {
            var group = new CustomerGroup
            {
                Code = code,
                Name = code,
                IsActive = isActive,
                Priority = 0,
                CreatedAtUtc = Now.UtcDateTime,
                UpdatedAtUtc = Now.UtcDateTime
            };
            Context.CustomerGroups.Add(group);
            await Context.SaveChangesAsync();
            return group;
        }

        public async Task<(int UserId, long GroupId)> AddApprovedDealerAsync(string email)
        {
            var user = await AddUserAsync(email);
            var group = await AddGroupAsync($"group-{user.Id}", true);
            var submitted = await ApplicationService().SubmitAsync(
                Application(user.Id, $"approved-application-{user.Id:D8}"));
            var approved = await ApplicationService().ReviewAsync(
                submitted.ApplicationId!.Value,
                DealerReviewDecision.Approve,
                group.Id);
            Assert.Equal(DealerApplicationStatuses.Approved, approved.Status);
            return (user.Id, group.Id);
        }

        public async Task<PriceList> AddPriceListAsync(long groupId)
        {
            var priceList = new PriceList
            {
                Code = $"list-{groupId}",
                Name = "Dealer list",
                CustomerGroupId = groupId,
                Currency = "TRY",
                IsActive = true,
                ValidFromUtc = Now.AddDays(-1).UtcDateTime,
                ValidToUtc = Now.AddDays(1).UtcDateTime
            };
            Context.PriceLists.Add(priceList);
            await Context.SaveChangesAsync();
            return priceList;
        }

        public async Task<PriceRule> AddRuleAsync(
            long priceListId,
            int priority,
            decimal? discount,
            decimal? fixedPrice = null,
            int? productId = null,
            int minimumQuantity = 1,
            decimal minimumRevenue = 0m,
            DateTime? validFrom = null,
            DateTime? validTo = null)
        {
            var rule = new PriceRule
            {
                PriceListId = priceListId,
                ProductId = productId,
                MinimumQuantity = minimumQuantity,
                MinimumPeriodRevenue = minimumRevenue,
                Priority = priority,
                DiscountPercentage = discount,
                FixedUnitPrice = fixedPrice,
                ValidFromUtc = validFrom ?? Now.AddDays(-1).UtcDateTime,
                ValidToUtc = validTo ?? Now.AddDays(1).UtcDateTime,
                IsActive = true
            };
            Context.PriceRules.Add(rule);
            await Context.SaveChangesAsync();
            return rule;
        }

        public async Task<decimal> ProductPriceAsync(int productId) =>
            await Context.Products
                .Where(product => product.Id == productId)
                .Select(product => product.Price)
                .SingleAsync();

        public DealerApplicationService ApplicationService() =>
            new(Context, new FixedTimeProvider(Now));

        public B2bPricingService PricingService() =>
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
