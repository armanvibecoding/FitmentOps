using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class PaymentEventServiceTests
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 8, 5, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Register_SameProviderEventAndPayloadReturnsOriginalRecord()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context);
        var payload = Encoding.UTF8.GetBytes("{\"paymentId\":\"pay_123\",\"status\":\"success\"}");

        var first = await service.RegisterAsync(
            " IYZICO ",
            " evt_123 ",
            " payment.succeeded ",
            payload);
        var replay = await service.RegisterAsync(
            "iyzico",
            "evt_123",
            "a-different-type-is-not-allowed-to-overwrite-history",
            payload);

        Assert.Equal(PaymentEventRegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(PaymentEventRegistrationOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.PaymentEvent!.Id, replay.PaymentEvent!.Id);
        Assert.Equal("iyzico", replay.PaymentEvent.Provider);
        Assert.Equal("evt_123", replay.PaymentEvent.ProviderEventId);
        Assert.Equal("payment.succeeded", replay.PaymentEvent.EventType);
        Assert.Equal(PaymentEventProcessingStatuses.Received, replay.PaymentEvent.ProcessingStatus);
        Assert.Equal(ReceivedAt.UtcDateTime, replay.PaymentEvent.ReceivedAt);
        Assert.Null(replay.PaymentEvent.ProcessedAt);
        Assert.Null(replay.PaymentEvent.ErrorCode);
        Assert.Equal(1, await database.Context.Set<PaymentEvent>().CountAsync());
    }

    [Fact]
    public async Task Register_SameProviderEventWithDifferentPayloadReturnsConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context);

        var first = await service.RegisterAsync(
            "paytr",
            "notification-42",
            "payment.callback",
            Encoding.UTF8.GetBytes("merchant_oid=order-1&status=success"));
        var conflict = await service.RegisterAsync(
            "paytr",
            "notification-42",
            "payment.callback",
            Encoding.UTF8.GetBytes("merchant_oid=order-2&status=success"));

        Assert.Equal(PaymentEventRegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(PaymentEventRegistrationOutcome.Conflict, conflict.Outcome);
        Assert.Equal(first.PaymentEvent!.Id, conflict.PaymentEvent!.Id);
        Assert.Equal(first.PaymentEvent.PayloadSha256, conflict.PaymentEvent.PayloadSha256);
        Assert.Equal(1, await database.Context.Set<PaymentEvent>().CountAsync());
    }

    [Fact]
    public async Task Register_StoresOnlySha256HashAndNotRawPayload()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context);
        var payload = Encoding.UTF8.GetBytes(
            "{\"email\":\"customer@example.com\",\"card\":\"should-never-be-stored\"}");

        var result = await service.RegisterAsync(
            "iyzico",
            "evt-sensitive",
            "payment.failed",
            payload);

        Assert.Equal(PaymentEventRegistrationOutcome.Registered, result.Outcome);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            result.PaymentEvent!.PayloadSha256);
        Assert.DoesNotContain(
            typeof(PaymentEvent).GetProperties(),
            property => property.Name.Contains("Payload", StringComparison.Ordinal) &&
                        property.Name != nameof(PaymentEvent.PayloadSha256));
        Assert.All(
            typeof(PaymentEvent).GetProperties(),
            property => Assert.Null(property.SetMethod?.IsPublic == true ? property.SetMethod : null));
    }

    [Fact]
    public async Task Register_InvalidEnvelopeDoesNotAppendAnEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context);

        var result = await service.RegisterAsync(
            " ",
            "evt-invalid",
            "payment.callback",
            ReadOnlyMemory<byte>.Empty);

        Assert.Equal(PaymentEventRegistrationOutcome.InvalidRequest, result.Outcome);
        Assert.Null(result.PaymentEvent);
        Assert.Equal(0, await database.Context.Set<PaymentEvent>().CountAsync());
    }

    [Fact]
    public async Task Register_OversizedPayloadDoesNotAppendAnEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = CreateService(database.Context);

        var result = await service.RegisterAsync(
            "iyzico",
            "evt-too-large",
            "payment.callback",
            new byte[(256 * 1024) + 1]);

        Assert.Equal(PaymentEventRegistrationOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(0, await database.Context.PaymentEvents.CountAsync());
    }

    private static PaymentEventService CreateService(AutoPartsDbContext context)
    {
        return new PaymentEventService(context, new FixedTimeProvider(ReceivedAt));
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
        private TestDatabase(SqliteConnection connection, AutoPartsDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();

            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
