using System.Reflection;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class AdminAuditServiceTests
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 5, 13, 45, 30, TimeSpan.Zero);

    [Fact]
    public async Task Append_ProducesDeterministicChainedHashes()
    {
        await using var firstDatabase = await TestDatabase.CreateAsync();
        await using var secondDatabase = await TestDatabase.CreateAsync();
        await using var firstContext = firstDatabase.CreateContext();
        await using var secondContext = secondDatabase.CreateContext();
        var request = ValidRequest();

        var first = await CreateService(firstContext).AppendAsync(request);
        var sameInput = await CreateService(secondContext).AppendAsync(request);
        var next = await CreateService(firstContext).AppendAsync(
            request with
            {
                IdempotencyKey = "audit-idem-002",
                AggregateId = 9002
            });

        Assert.Equal(AdminAuditAppendOutcome.Appended, first.Outcome);
        Assert.Equal(AdminAuditAppendOutcome.Appended, sameInput.Outcome);
        Assert.Equal(first.Event!.EventHashSha256, sameInput.Event!.EventHashSha256);
        Assert.Equal(
            "3957876756ffd2c0b6aad43959b8b31f03be2b29b74f7027a9910d23858b1dcc",
            first.Event.EventHashSha256);
        Assert.Equal(AdminAuditService.GenesisHash, first.Event.PreviousEventHashSha256);
        Assert.Equal(first.Event.EventHashSha256, next.Event!.PreviousEventHashSha256);
        Assert.Equal(64, first.Event.EventHashSha256.Length);
        Assert.Equal(1, first.Event.Sequence);
        Assert.Equal(2, next.Event.Sequence);
    }

    [Fact]
    public async Task Append_DuplicateIdempotencyIsReplayAndChangedRequestIsConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateService(context);
        var request = ValidRequest();

        var first = await service.AppendAsync(request);
        var replay = await service.AppendAsync(request with
        {
            ActorRole = " FINANCE ",
            Action = " REFUND.REQUESTED "
        });
        var conflict = await service.AppendAsync(request with { AggregateId = 9010 });

        Assert.Equal(AdminAuditAppendOutcome.Appended, first.Outcome);
        Assert.Equal(AdminAuditAppendOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Event!.Id, replay.Event!.Id);
        Assert.Equal(AdminAuditAppendOutcome.Conflict, conflict.Outcome);
        Assert.Equal(AdminAuditErrorCodes.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(1, await context.Set<AdminAuditEvent>().CountAsync());
    }

    [Fact]
    public async Task VerifyChain_DetectsRelationalTampering()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateService(context);
        await service.AppendAsync(ValidRequest());
        await service.AppendAsync(ValidRequest() with
        {
            IdempotencyKey = "audit-idem-002",
            AggregateId = 9002
        });

        var valid = await service.VerifyChainAsync();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE AdminAuditEvents SET Outcome = 'failed' WHERE Sequence = 1");
        var tampered = await service.VerifyChainAsync();

        Assert.True(valid.IsValid);
        Assert.Equal(2, valid.VerifiedEventCount);
        Assert.False(tampered.IsValid);
        Assert.Equal(1, tampered.FailedSequence);
        Assert.Equal(
            AdminAuditVerificationFailureCodes.EventHashMismatch,
            tampered.FailureCode);
    }

    [Fact]
    public async Task Append_InvalidOrUnboundedDataIsRejectedWithoutPersistence()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateService(context);
        var requests = new[]
        {
            ValidRequest() with { ActorUserId = 0 },
            ValidRequest() with { ActorRole = "owner" },
            ValidRequest() with { Action = "arbitrary.action" },
            ValidRequest() with { AggregateType = "database-row" },
            ValidRequest() with { AggregateType = AdminAuditAggregateTypes.Product },
            ValidRequest() with { AggregateId = 0 },
            ValidRequest() with { CorrelationId = " " },
            ValidRequest() with
            {
                IdempotencyKey = new string('x', AdminAuditService.MaxOpaqueIdentifierLength + 1)
            },
            ValidRequest() with { Outcome = "maybe" }
        };

        foreach (var request in requests)
        {
            var result = await service.AppendAsync(request);
            Assert.Equal(AdminAuditAppendOutcome.InvalidRequest, result.Outcome);
            Assert.NotNull(result.ErrorCode);
        }

        Assert.Equal(0, await context.Set<AdminAuditEvent>().CountAsync());
    }

    [Fact]
    public async Task Append_StoresOpaqueIdentifiersOnlyAsHashesAndQueryReturnsMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var service = CreateService(context);
        const string correlationId = "correlation-value-never-persisted";
        const string idempotencyKey = "idempotency-value-never-persisted";

        await service.AppendAsync(ValidRequest() with
        {
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey
        });
        var row = await context.Set<AdminAuditEvent>().AsNoTracking().SingleAsync();
        var metadata = Assert.Single(await service.GetMetadataAsync());

        Assert.DoesNotContain(correlationId, row.CorrelationIdSha256, StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyKey, row.IdempotencyKeySha256, StringComparison.Ordinal);
        Assert.Equal(64, metadata.CorrelationIdSha256.Length);
        Assert.Equal(64, metadata.IdempotencyKeySha256.Length);
        Assert.Equal(DateTimeKind.Utc, metadata.OccurredAtUtc.Kind);
        Assert.DoesNotContain(
            typeof(AdminAuditEventMetadata).GetProperties(),
            property => ForbiddenDataName(property.Name));
    }

    [Fact]
    public void PublicContract_IsAppendOnlyAndContainsNoFreeTextOrSensitiveDataFields()
    {
        Assert.All(
            typeof(AdminAuditEvent).GetProperties(),
            property => Assert.False(property.SetMethod?.IsPublic == true));

        Assert.DoesNotContain(
            typeof(AdminAuditEvent).GetProperties(),
            property => ForbiddenDataName(property.Name));

        var publicServiceMethods = typeof(AdminAuditService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            publicServiceMethods,
            method => method.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                      method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
                      method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentAppends_ReceiveContiguousUniqueChainPositions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var tasks = Enumerable.Range(1, 12).Select(async index =>
        {
            await using var context = database.CreateContext();
            return await CreateService(context).AppendAsync(ValidRequest() with
            {
                AggregateId = 9000 + index,
                CorrelationId = $"audit-correlation-{index:000}",
                IdempotencyKey = $"audit-idem-{index:000}"
            });
        });

        var results = await Task.WhenAll(tasks);
        await using var verificationContext = database.CreateContext();
        var verification = await CreateService(verificationContext).VerifyChainAsync();
        var sequences = await verificationContext.Set<AdminAuditEvent>()
            .OrderBy(auditEvent => auditEvent.Sequence)
            .Select(auditEvent => auditEvent.Sequence)
            .ToArrayAsync();

        Assert.All(
            results,
            result => Assert.Equal(AdminAuditAppendOutcome.Appended, result.Outcome));
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (long)value), sequences);
        Assert.True(verification.IsValid);
        Assert.Equal(12, verification.VerifiedEventCount);
    }

    [Fact]
    public void RolePermissionMatrix_KeepsLegacyAdminCompatible()
    {
        Assert.True(AdminRolePermissionMatrix.IsAllowed(
            AdminAuditRoles.Finance,
            AdminPermissionNames.FinanceManage));
        Assert.False(AdminRolePermissionMatrix.IsAllowed(
            AdminAuditRoles.Finance,
            AdminPermissionNames.CatalogManage));
        Assert.All(
            AdminPermissionNames.All,
            permission => Assert.True(AdminRolePermissionMatrix.IsAllowed(
                AdminAuditRoles.LegacyAdmin,
                permission)));
    }

    private static AdminAuditAppendRequest ValidRequest()
    {
        return new AdminAuditAppendRequest(
            ActorUserId: 42,
            ActorRole: AdminAuditRoles.Finance,
            Action: AdminAuditActions.RefundRequested,
            AggregateType: AdminAuditAggregateTypes.Refund,
            AggregateId: 9001,
            CorrelationId: "audit-correlation-001",
            IdempotencyKey: "audit-idem-001",
            Outcome: AdminAuditOutcomes.Succeeded);
    }

    private static AdminAuditService CreateService(DbContext context)
    {
        return new AdminAuditService(context, new FixedTimeProvider(OccurredAt));
    }

    private static bool ForbiddenDataName(string name)
    {
        var forbidden = new[]
        {
            "Body",
            "Payload",
            "Message",
            "Description",
            "Email",
            "Phone",
            "Address",
            "Name",
            "Token",
            "Secret",
            "Cookie",
            "Password"
        };

        return forbidden.Any(
            value => name.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keeperConnection;
        private readonly string _connectionString;

        private TestDatabase(SqliteConnection keeperConnection, string connectionString)
        {
            _keeperConnection = keeperConnection;
            _connectionString = connectionString;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connectionString =
                $"Data Source=audit-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            var keeperConnection = new SqliteConnection(connectionString);
            await keeperConnection.OpenAsync();

            var database = new TestDatabase(keeperConnection, connectionString);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public AuditDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AuditDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _keeperConnection.DisposeAsync();
        }
    }

    private sealed class AuditDbContext : DbContext
    {
        public AuditDbContext(DbContextOptions<AuditDbContext> options)
            : base(options)
        {
        }

        public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    }
}
