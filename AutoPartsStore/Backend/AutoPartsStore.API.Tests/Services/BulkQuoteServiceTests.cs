using System.Text.Json;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class BulkQuoteServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Submit_RequiresApprovedActiveDealer()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = await database.AddUserAsync("retail@example.test");

        var result = await database.Service().SubmitAsync(Command(user.Id, "retail-rfq-key-0001"));

        Assert.Equal(BulkQuoteOutcome.NotEligible, result.Outcome);
        Assert.Empty(await database.Context.BulkQuoteRequests.ToListAsync());
    }

    [Fact]
    public async Task Submit_MergesNormalizedLinesAndUsesOnlyVerifiedUniqueIdentifier()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("rfq@example.test");
        await database.AddVerifiedIdentifierAsync("OEM123", productId: 1);
        var command = new SubmitBulkQuoteCommand(
            dealer.UserId,
            "bulk-rfq-key-000001",
            "try",
            [
                new BulkQuoteInputLine("OEM-123", 2),
                new BulkQuoteInputLine("oem 123", 3),
                new BulkQuoteInputLine("UNKNOWN-1", 1)
            ]);

        var result = await database.Service().SubmitAsync(command);
        var request = await database.Context.BulkQuoteRequests
            .Include(candidate => candidate.Lines)
            .SingleAsync();

        Assert.Equal(BulkQuoteOutcome.Submitted, result.Outcome);
        Assert.Equal(2, request.Lines.Count);
        var matched = request.Lines.Single(line => line.NormalizedIdentifier == "OEM123");
        Assert.Equal(5, matched.RequestedQuantity);
        Assert.Equal(1, matched.ProductId);
        Assert.Equal(BulkQuoteLineStatuses.Matched, matched.Status);
        Assert.Null(request.Lines.Single(line => line.NormalizedIdentifier == "UNKNOWN1").ProductId);
    }

    [Fact]
    public async Task Submit_IsIdempotentAndRejectsChangedPayload()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("replay-rfq@example.test");
        var command = Command(dealer.UserId, "bulk-rfq-key-000002");

        var submitted = await database.Service().SubmitAsync(command);
        var replay = await database.Service().SubmitAsync(command);
        var conflict = await database.Service().SubmitAsync(command with
        {
            Lines = [new BulkQuoteInputLine("OEM-123", 99)]
        });

        Assert.Equal(BulkQuoteOutcome.Submitted, submitted.Outcome);
        Assert.Equal(BulkQuoteOutcome.Replayed, replay.Outcome);
        Assert.True(replay.Replayed);
        Assert.Equal(BulkQuoteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.BulkQuoteRequests.CountAsync());
    }

    [Fact]
    public async Task PrepareAndAccept_RequireCompleteQuoteAndAreReplaySafe()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("accept-rfq@example.test");
        await database.AddVerifiedIdentifierAsync("OEM123", productId: 1);
        var submitted = await database.Service().SubmitAsync(
            Command(dealer.UserId, "bulk-rfq-key-000003"));
        var line = await database.Context.BulkQuoteLines.SingleAsync();

        var quoted = await database.Service().PrepareQuoteAsync(
            submitted.RequestId!.Value,
            [new BulkQuoteOfferLine(line.Id, 75m, 10, 2)],
            Now.AddDays(1));
        var quoteReplay = await database.Service().PrepareQuoteAsync(
            submitted.RequestId.Value,
            [new BulkQuoteOfferLine(line.Id, 75m, 10, 2)],
            Now.AddDays(1));
        var accepted = await database.Service().AcceptAsync(
            submitted.RequestId.Value,
            dealer.UserId);
        var acceptReplay = await database.Service().AcceptAsync(
            submitted.RequestId.Value,
            dealer.UserId);

        Assert.Equal(BulkQuoteOutcome.Updated, quoted.Outcome);
        Assert.Equal(BulkQuoteOutcome.Replayed, quoteReplay.Outcome);
        Assert.Equal(BulkQuoteStatuses.Accepted, accepted.Status);
        Assert.Equal(BulkQuoteOutcome.Replayed, acceptReplay.Outcome);
    }

    [Fact]
    public async Task ExpiredQuote_CannotBeAcceptedAndPrivateKeysDoNotSerialize()
    {
        await using var database = await TestDatabase.CreateAsync();
        var dealer = await database.AddApprovedDealerAsync("expired-rfq@example.test");
        await database.AddVerifiedIdentifierAsync("OEM123", productId: 1);
        var submitted = await database.Service().SubmitAsync(
            Command(dealer.UserId, "bulk-rfq-key-000004"));
        var line = await database.Context.BulkQuoteLines.SingleAsync();
        await database.Service().PrepareQuoteAsync(
            submitted.RequestId!.Value,
            [new BulkQuoteOfferLine(line.Id, 75m, 10, 2)],
            Now.AddMinutes(1));

        var expired = await database.Service(Now.AddMinutes(2)).AcceptAsync(
            submitted.RequestId.Value,
            dealer.UserId);
        var request = await database.Context.BulkQuoteRequests.AsNoTracking().SingleAsync();
        var json = JsonSerializer.Serialize(request);

        Assert.Equal(BulkQuoteOutcome.Expired, expired.Outcome);
        Assert.Equal(BulkQuoteStatuses.Expired, expired.Status);
        Assert.DoesNotContain("bulk-rfq-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadHash", json, StringComparison.Ordinal);
    }

    private static SubmitBulkQuoteCommand Command(int userId, string key) => new(
        userId,
        key,
        "TRY",
        [new BulkQuoteInputLine("OEM-123", 2)]);

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
                Password = "password-hash",
                FullName = "Dealer User",
                Phone = "+905551112233",
                Role = "User",
                CreatedAt = Now.UtcDateTime,
                IsActive = true
            };
            Context.Users.Add(user);
            await Context.SaveChangesAsync();
            return user;
        }

        public async Task<(int UserId, long GroupId)> AddApprovedDealerAsync(string email)
        {
            var user = await AddUserAsync(email);
            var group = new CustomerGroup
            {
                Code = $"group-{user.Id}",
                Name = "RFQ dealer",
                IsActive = true,
                CreatedAtUtc = Now.UtcDateTime,
                UpdatedAtUtc = Now.UtcDateTime
            };
            Context.CustomerGroups.Add(group);
            await Context.SaveChangesAsync();
            var applicationService = new DealerApplicationService(
                Context,
                new FixedTimeProvider(Now));
            var submitted = await applicationService.SubmitAsync(new DealerApplicationCommand(
                user.Id,
                $"dealer-rfq-application-{user.Id:D8}",
                "RFQ Parts Ltd",
                "1234567890",
                "RFQ Contact",
                email,
                "+905551112233"));
            await applicationService.ReviewAsync(
                submitted.ApplicationId!.Value,
                DealerReviewDecision.Approve,
                group.Id);
            return (user.Id, group.Id);
        }

        public async Task AddVerifiedIdentifierAsync(string normalizedValue, int productId)
        {
            Context.ProductIdentifiers.Add(new ProductIdentifier
            {
                ProductId = productId,
                Kind = PartIdentifierKind.Oem,
                SchemeAuthority = "TEST-OEM",
                Value = normalizedValue,
                NormalizedValue = normalizedValue,
                IsVerified = true,
                SourceKind = FitmentSourceKind.ManualExpertReview,
                SourceName = "test",
                SourceRecordId = $"identifier-{Guid.NewGuid():N}",
                Provenance = "Test verified source",
                ValidFromUtc = Now.AddDays(-1).UtcDateTime
            });
            await Context.SaveChangesAsync();
        }

        public BulkQuoteService Service(DateTimeOffset? now = null) =>
            new(Context, new FixedTimeProvider(now ?? Now));

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
