using AutoPartsStore.API.Observability;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Invoicing;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Observability;

public sealed class OperationalReadinessTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledOptionalGateways_ReportDegradedWithoutMaskingReadiness()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            databaseAvailable: true,
            paymentEnabled: false,
            invoiceEnabled: false,
            OutboxHealthCounts.Empty,
            new OperationalReadinessOptions(),
            ObservedAt);

        Assert.True(response.Ready);
        Assert.Equal(OperationalStates.Degraded, response.Status);
        Assert.Equal(OperationalStates.Ready, response.Database.Status);
        Assert.Equal(OperationalStates.Disabled, response.Payment.Status);
        Assert.Equal(OperationalStates.Disabled, response.Invoice.Status);
        Assert.Equal(ObservedAt, response.ObservedAtUtc);
    }

    [Fact]
    public void EnabledGateways_DoNotMakeDisconnectedDatabaseReady()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            databaseAvailable: false,
            paymentEnabled: true,
            invoiceEnabled: true,
            OutboxHealthCounts.Empty,
            new OperationalReadinessOptions(),
            ObservedAt,
            outboxAvailable: false);

        Assert.False(response.Ready);
        Assert.Equal(OperationalStates.Unavailable, response.Status);
        Assert.False(response.Database.CanConnect);
        Assert.Equal(OperationalStates.Unavailable, response.Outbox.Status);
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(0, 0, 2)]
    public void WarningThresholds_DegradeButRemainReady(
        int backlog,
        int due,
        int failed)
    {
        var options = ThresholdOptions();

        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            new OutboxHealthCounts(backlog, due, failed),
            options,
            ObservedAt);

        Assert.True(response.Ready);
        Assert.Equal(OperationalStates.Degraded, response.Status);
        Assert.Equal(OperationalStates.Degraded, response.Outbox.Status);
    }

    [Theory]
    [InlineData(20, 0, 0)]
    [InlineData(0, 8, 0)]
    [InlineData(0, 0, 5)]
    public void CriticalThresholds_FailReadiness(
        int backlog,
        int due,
        int failed)
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            new OutboxHealthCounts(backlog, due, failed),
            ThresholdOptions(),
            ObservedAt);

        Assert.False(response.Ready);
        Assert.Equal(OperationalStates.Unavailable, response.Status);
        Assert.Equal(OperationalStates.Unavailable, response.Outbox.Status);
    }

    [Fact]
    public void CriticalAuditIntentBacklog_FailsReadiness()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            OutboxHealthCounts.Empty,
            ThresholdOptions(),
            ObservedAt,
            auditIntents: new AuditIntentHealthCounts(25, 0, 0));

        Assert.False(response.Ready);
        Assert.Equal(OperationalStates.Unavailable, response.AuditIntents.Status);
    }

    [Fact]
    public void EnabledPaymentWithoutReservationExpiryWorker_FailsReadiness()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            OutboxHealthCounts.Empty,
            ThresholdOptions(),
            ObservedAt,
            inventoryReservationExpiryEnabled: false);

        Assert.False(response.Ready);
        Assert.Equal(
            OperationalStates.Disabled,
            response.InventoryReservationExpiry.Status);
    }

    [Fact]
    public void RequestedChannelWithoutAdapter_FailsReadiness()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            OutboxHealthCounts.Empty,
            ThresholdOptions(),
            ObservedAt,
            salesChannels: new SalesChannelHealthCounts(1, 1, 0, 0));

        Assert.False(response.Ready);
        Assert.Equal(OperationalStates.Unavailable, response.SalesChannels.Status);
        Assert.Equal(1, response.SalesChannels.Misconfigured);
    }

    [Fact]
    public void MissingRequiredLegalDocumentFailsReadiness()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            false,
            false,
            OutboxHealthCounts.Empty,
            ThresholdOptions(),
            ObservedAt,
            legalDocuments: new LegalDocumentHealthCounts(2, 1));

        Assert.False(response.Ready);
        Assert.Equal(OperationalStates.Unavailable, response.LegalDocuments.Status);
        Assert.Equal(2, response.LegalDocuments.Required);
        Assert.Equal(1, response.LegalDocuments.Published);
    }

    [Fact]
    public void ChannelDrift_DegradesUntilCriticalThreshold()
    {
        var response = OperationalReadinessEvaluator.Evaluate(
            true,
            true,
            true,
            OutboxHealthCounts.Empty,
            ThresholdOptions(),
            ObservedAt,
            salesChannels: new SalesChannelHealthCounts(1, 0, 1, 0));

        Assert.True(response.Ready);
        Assert.Equal(OperationalStates.Degraded, response.Status);
        Assert.Equal(OperationalStates.Degraded, response.SalesChannels.Status);
    }

    [Fact]
    public async Task Service_ProbesDatabaseAndMeasuresDueFailedAndBacklog()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var contextOptions = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new AutoPartsDbContext(contextOptions);
        await context.Database.EnsureCreatedAsync();

        var now = ObservedAt.UtcDateTime;
        context.OutboxMessages.AddRange(
            Message(1, nextAttemptAt: null),
            Message(2, nextAttemptAt: now.AddMinutes(-1), lastError: "retry-failed"),
            Message(3, nextAttemptAt: now.AddMinutes(1)),
            Message(4, processedAt: now.AddMinutes(-2), lastError: "terminal-failed"));
        await SeedLegalDocumentsAsync(context);
        await context.SaveChangesAsync();

        var service = new OperationalReadinessService(
            context,
            new DisabledPaymentGateway(),
            new DisabledInvoiceGateway(),
            new OperationalReadinessOptions
            {
                OutboxBacklogWarningThreshold = 3,
                OutboxBacklogCriticalThreshold = 10,
                OutboxDueWarningThreshold = 2,
                OutboxDueCriticalThreshold = 10,
                OutboxFailedWarningThreshold = 2,
                OutboxFailedCriticalThreshold = 10
            },
            new FixedTimeProvider(ObservedAt),
            NullLogger<OperationalReadinessService>.Instance,
            new InventoryReservationExpiryOptions { Enabled = true });

        var response = await service.CheckReadinessAsync();

        Assert.True(response.Database.CanConnect);
        Assert.Equal(3, response.Outbox.Backlog);
        Assert.Equal(2, response.Outbox.Due);
        Assert.Equal(2, response.Outbox.Failed);
        Assert.Equal(OperationalStates.Ready, response.AuditIntents.Status);
        Assert.Equal(OperationalStates.Ready, response.LegalDocuments.Status);
        Assert.True(response.Ready);
        Assert.Equal(OperationalStates.Degraded, response.Status);
    }

    private static OperationalReadinessOptions ThresholdOptions() => new()
    {
        OutboxBacklogWarningThreshold = 10,
        OutboxBacklogCriticalThreshold = 20,
        OutboxDueWarningThreshold = 4,
        OutboxDueCriticalThreshold = 8,
        OutboxFailedWarningThreshold = 2,
        OutboxFailedCriticalThreshold = 5,
        AuditIntentPendingWarningThreshold = 10,
        AuditIntentPendingCriticalThreshold = 20,
        AuditIntentFailedWarningThreshold = 2,
        AuditIntentFailedCriticalThreshold = 5,
        ChannelDriftWarningThreshold = 1,
        ChannelDriftCriticalThreshold = 5,
        ChannelFailedInboxWarningThreshold = 1,
        ChannelFailedInboxCriticalThreshold = 5
    };

    private static OutboxMessage Message(
        int index,
        DateTime? nextAttemptAt = null,
        DateTime? processedAt = null,
        string? lastError = null) => new()
        {
            EventId = Guid.NewGuid(),
            Type = "test.event",
            AggregateId = $"aggregate-{index}",
            Payload = "{}",
            CreatedAt = ObservedAt.UtcDateTime.AddSeconds(index),
            NextAttemptAt = nextAttemptAt,
            ProcessedAt = processedAt,
            LastError = lastError
        };

    private static async Task SeedLegalDocumentsAsync(AutoPartsDbContext context)
    {
        foreach (var (type, title) in new[]
                 {
                     (LegalDocumentTypes.PreliminaryInformation, "Preliminary"),
                     (LegalDocumentTypes.DistanceSalesAgreement, "Distance sales")
                 })
        {
            var document = LegalDocumentVersion.CreateDraft(
                type,
                "readiness-v1",
                title,
                $"{title} readiness content",
                1,
                ObservedAt.UtcDateTime);
            context.LegalDocumentVersions.Add(document);
            await context.SaveChangesAsync();
            document.Publish(1, ObservedAt.UtcDateTime);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
