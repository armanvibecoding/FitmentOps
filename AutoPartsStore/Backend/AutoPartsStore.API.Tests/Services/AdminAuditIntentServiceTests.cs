using System.Reflection;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class AdminAuditIntentServiceTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stage_OuterRollbackRemovesBusinessMutationAndIntent()
    {
        await using var database = await FullDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.BusinessMarkers.Add(new BusinessMarker { Value = "rolled-back" });
            var result = CreateIntentService(context, new MutableTimeProvider(InitialTime))
                .Stage(ValidRequest());

            Assert.Equal(AdminAuditIntentStageOutcome.Staged, result.Outcome);
            Assert.Equal(EntityState.Added, context.Entry(result.Intent!).State);
            Assert.Equal(0, await context.AdminAuditIntents.CountAsync());
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verification = database.CreateContext();
        Assert.Equal(0, await verification.AdminAuditIntents.CountAsync());
        Assert.Equal(0, await verification.BusinessMarkers.CountAsync());
    }

    [Fact]
    public async Task Stage_OuterCommitPersistsBusinessMutationAndPendingIntent()
    {
        await using var database = await FullDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.BusinessMarkers.Add(new BusinessMarker { Value = "committed" });
            var result = CreateIntentService(context, new MutableTimeProvider(InitialTime))
                .Stage(ValidRequest());

            Assert.Equal(AdminAuditIntentStageOutcome.Staged, result.Outcome);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verification = database.CreateContext();
        var intent = await verification.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Pending, intent.Status);
        Assert.Equal(0, intent.AttemptCount);
        Assert.Equal(1, await verification.BusinessMarkers.CountAsync());
    }

    [Fact]
    public async Task Stage_InvalidOrUnboundedMetadataDoesNotTrackAnIntent()
    {
        await using var database = await FullDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateIntentService(context, new MutableTimeProvider(InitialTime));
        var requests = new[]
        {
            ValidRequest() with { OperationId = Guid.Empty },
            ValidRequest() with { ActorUserId = 0 },
            ValidRequest() with { ActorRole = "owner" },
            ValidRequest() with { Action = "arbitrary.action" },
            ValidRequest() with { AggregateType = "database-row" },
            ValidRequest() with { AggregateId = 0 },
            ValidRequest() with { CorrelationId = " " },
            ValidRequest() with
            {
                CorrelationId = new string('x', AdminAuditIntentService.MaxCorrelationIdLength + 1)
            },
            ValidRequest() with { Outcome = "maybe" }
        };

        foreach (var request in requests)
        {
            var result = service.Stage(request);
            Assert.Equal(AdminAuditIntentStageOutcome.InvalidRequest, result.Outcome);
            Assert.Null(result.Intent);
            Assert.NotNull(result.ErrorCode);
        }

        Assert.DoesNotContain(
            context.ChangeTracker.Entries(),
            entry => entry.Entity is AdminAuditIntent);
        Assert.Equal(0, await context.AdminAuditIntents.CountAsync());
    }

    [Fact]
    public async Task Stage_DatabaseRejectsDuplicateServerOperationId()
    {
        await using var database = await FullDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateIntentService(context, new MutableTimeProvider(InitialTime));
        var request = ValidRequest();
        service.Stage(request);
        service.Stage(request with { AggregateId = request.AggregateId + 1 });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Dispatch_AppendsAuditAndMarksIntentSucceeded()
    {
        var time = new MutableTimeProvider(InitialTime);
        await using var database = await FullDatabase.CreateAsync();
        await StageAndCommitAsync(database, time, ValidRequest());

        await using var context = database.CreateContext();
        var intentService = CreateIntentService(context, time);
        var summary = await intentService.DispatchBatchAsync(
            new AdminAuditService(context, time),
            TestOptions());

        Assert.Equal(new AdminAuditIntentDispatchSummary(1, 1, 0, 0), summary);
        var intent = await context.AdminAuditIntents.AsNoTracking().SingleAsync();
        var auditEvent = await context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(1, intent.AttemptCount);
        Assert.NotNull(intent.CompletedAtUtc);
        Assert.Equal(intent.ActorUserId, auditEvent.ActorUserId);
        Assert.Equal(intent.Action, auditEvent.Action);
        Assert.Equal(intent.AggregateType, auditEvent.AggregateType);
        Assert.Equal(intent.AggregateId, auditEvent.AggregateId);
        Assert.Equal(intent.Outcome, auditEvent.Outcome);
    }

    [Fact]
    public async Task Dispatch_CrashAfterAuditAppend_ReclaimsLeaseAndReplaysSingleAuditEvent()
    {
        var time = new MutableTimeProvider(InitialTime);
        var options = TestOptions();
        await using var database = await FullDatabase.CreateAsync();
        var request = ValidRequest();
        await StageAndCommitAsync(database, time, request);

        await using (var crashedContext = database.CreateContext())
        {
            var crashedIntentService = CreateIntentService(crashedContext, time);
            var lease = Assert.Single(await crashedIntentService.ClaimBatchAsync(options));
            var append = await new AdminAuditService(crashedContext, time).AppendAsync(
                ToAuditRequest(lease));
            Assert.Equal(AdminAuditAppendOutcome.Appended, append.Outcome);
            // Simulate process loss before MarkSucceededAsync.
        }

        time.Advance(options.LeaseDuration + TimeSpan.FromSeconds(1));
        await using (var recoveryContext = database.CreateContext())
        {
            var recoveryService = CreateIntentService(recoveryContext, time);
            var summary = await recoveryService.DispatchBatchAsync(
                new AdminAuditService(recoveryContext, time),
                options);
            Assert.Equal(new AdminAuditIntentDispatchSummary(1, 1, 0, 0), summary);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.AdminAuditEvents.CountAsync());
        var intent = await verification.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(2, intent.AttemptCount);
    }

    [Fact]
    public async Task ClaimBatch_ConcurrentWorkersOnlyLeaseIntentOnce()
    {
        var time = new MutableTimeProvider(InitialTime);
        await using var database = await FullDatabase.CreateAsync();
        await StageAndCommitAsync(database, time, ValidRequest());
        var options = TestOptions() with { MaxBatchSize = 1 };

        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = database.CreateContext();
            return await CreateIntentService(context, time).ClaimBatchAsync(options);
        }));

        var lease = Assert.Single(claims.SelectMany(items => items));
        Assert.NotEqual(Guid.Empty, lease.LeaseId);
        await using var verification = database.CreateContext();
        var intent = await verification.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Processing, intent.Status);
        Assert.Equal(1, intent.AttemptCount);
    }

    [Fact]
    public async Task ClaimBatch_NeverExceedsConfiguredMaximum()
    {
        var time = new MutableTimeProvider(InitialTime);
        await using var database = await FullDatabase.CreateAsync();
        await using (var context = database.CreateContext())
        {
            var service = CreateIntentService(context, time);
            foreach (var index in Enumerable.Range(1, 3))
            {
                service.Stage(ValidRequest() with
                {
                    OperationId = Guid.NewGuid(),
                    AggregateId = index
                });
            }

            await context.SaveChangesAsync();
        }

        await using var claimingContext = database.CreateContext();
        var leases = await CreateIntentService(claimingContext, time).ClaimBatchAsync(
            TestOptions() with { MaxBatchSize = 2 });

        Assert.Equal(2, leases.Count);
        Assert.Equal(2, await claimingContext.AdminAuditIntents.CountAsync(
            intent => intent.Status == AdminAuditIntentStatuses.Processing));
        Assert.Equal(1, await claimingContext.AdminAuditIntents.CountAsync(
            intent => intent.Status == AdminAuditIntentStatuses.Pending));
    }

    [Fact]
    public async Task Dispatch_RepeatedExceptionsStopAtConfiguredTerminalAttempt()
    {
        var time = new MutableTimeProvider(InitialTime);
        var options = TestOptions() with { MaxAttempts = 3 };
        await using var database = await IntentOnlyDatabase.CreateAsync();
        await using (var stagingContext = database.CreateContext())
        {
            CreateIntentService(stagingContext, time).Stage(ValidRequest());
            await stagingContext.SaveChangesAsync();
        }

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            await using var context = database.CreateContext();
            var summary = await CreateIntentService(context, time).DispatchBatchAsync(
                new AdminAuditService(context, time),
                options);

            Assert.Equal(1, summary.Claimed);
            Assert.Equal(0, summary.Succeeded);
            Assert.Equal(attempt < options.MaxAttempts ? 1 : 0, summary.RetriesScheduled);
            Assert.Equal(attempt == options.MaxAttempts ? 1 : 0, summary.Failed);
            time.Advance(options.RetryDelay);
        }

        await using var verification = database.CreateContext();
        var intent = await verification.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Failed, intent.Status);
        Assert.Equal(options.MaxAttempts, intent.AttemptCount);
        Assert.Equal(AdminAuditIntentErrorCodes.DispatchException, intent.LastErrorCode);
        Assert.Null(intent.LeaseId);
        Assert.NotNull(intent.CompletedAtUtc);
    }

    [Fact]
    public async Task Stage_PersistsNoRawCorrelationOrSensitiveFreeTextFields()
    {
        const string sensitiveCorrelation = "customer@example.test-Bearer-secret";
        await using var database = await FullDatabase.CreateAsync();
        var time = new MutableTimeProvider(InitialTime);
        await StageAndCommitAsync(
            database,
            time,
            ValidRequest() with { CorrelationId = sensitiveCorrelation });

        await using var context = database.CreateContext();
        var intent = await context.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(64, intent.CorrelationIdSha256.Length);
        Assert.DoesNotContain(
            sensitiveCorrelation,
            intent.CorrelationIdSha256,
            StringComparison.Ordinal);

        var forbiddenNames = new[]
        {
            "Body", "Payload", "Message", "Description", "Email", "Phone",
            "Address", "Name", "AccessToken", "RefreshToken", "AuthToken",
            "Secret", "Cookie", "Password"
        };
        Assert.DoesNotContain(
            typeof(AdminAuditIntent).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => forbiddenNames.Any(forbidden =>
                property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(AdminAuditIntent).GetProperties(),
            property => property.Name == "CorrelationId");
    }

    private static AdminAuditIntentStageRequest ValidRequest()
    {
        return new AdminAuditIntentStageRequest(
            Guid.Parse("98ab76d5-66e8-4fd4-8bab-8afabe42bce9"),
            42,
            AdminAuditRoles.LegacyAdmin,
            AdminAuditActions.ProductUpdated,
            AdminAuditAggregateTypes.Product,
            7,
            "request-correlation-001",
            AdminAuditOutcomes.Succeeded);
    }

    private static AdminAuditIntentOptions TestOptions()
    {
        return new AdminAuditIntentOptions
        {
            MaxBatchSize = 10,
            MaxAttempts = 5,
            LeaseDuration = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(100)
        };
    }

    private static AdminAuditIntentService CreateIntentService(
        DbContext context,
        TimeProvider timeProvider)
    {
        return new AdminAuditIntentService(context, timeProvider);
    }

    private static AdminAuditAppendRequest ToAuditRequest(AdminAuditIntentLease lease)
    {
        return new AdminAuditAppendRequest(
            lease.ActorUserId,
            lease.ActorRole,
            lease.Action,
            lease.AggregateType,
            lease.AggregateId,
            lease.CorrelationIdSha256,
            lease.OperationId.ToString("N"),
            lease.Outcome);
    }

    private static async Task StageAndCommitAsync(
        FullDatabase database,
        TimeProvider timeProvider,
        AdminAuditIntentStageRequest request)
    {
        await using var context = database.CreateContext();
        var result = CreateIntentService(context, timeProvider).Stage(request);
        Assert.Equal(AdminAuditIntentStageOutcome.Staged, result.Outcome);
        await context.SaveChangesAsync();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }

    private sealed class BusinessMarker
    {
        public int Id { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private sealed class FullAuditDbContext : DbContext
    {
        public FullAuditDbContext(DbContextOptions<FullAuditDbContext> options)
            : base(options)
        {
        }

        public DbSet<AdminAuditIntent> AdminAuditIntents => Set<AdminAuditIntent>();
        public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
        public DbSet<BusinessMarker> BusinessMarkers => Set<BusinessMarker>();
    }

    private sealed class IntentOnlyDbContext : DbContext
    {
        public IntentOnlyDbContext(DbContextOptions<IntentOnlyDbContext> options)
            : base(options)
        {
        }

        public DbSet<AdminAuditIntent> AdminAuditIntents => Set<AdminAuditIntent>();
    }

    private sealed class FullDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly string _connectionString;

        private FullDatabase(SqliteConnection keeper, string connectionString)
        {
            _keeper = keeper;
            _connectionString = connectionString;
        }

        public static async Task<FullDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=audit-intent-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var database = new FullDatabase(keeper, connectionString);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public FullAuditDbContext CreateContext()
        {
            return new FullAuditDbContext(
                new DbContextOptionsBuilder<FullAuditDbContext>()
                    .UseSqlite(_connectionString)
                    .Options);
        }

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();
    }

    private sealed class IntentOnlyDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly string _connectionString;

        private IntentOnlyDatabase(SqliteConnection keeper, string connectionString)
        {
            _keeper = keeper;
            _connectionString = connectionString;
        }

        public static async Task<IntentOnlyDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=audit-intent-only-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var database = new IntentOnlyDatabase(keeper, connectionString);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public IntentOnlyDbContext CreateContext()
        {
            return new IntentOnlyDbContext(
                new DbContextOptionsBuilder<IntentOnlyDbContext>()
                    .UseSqlite(_connectionString)
                    .Options);
        }

        public async ValueTask DisposeAsync() => await _keeper.DisposeAsync();
    }
}
