using AutoPartsStore.API.Data;
using AutoPartsStore.API.Invoicing;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Observability;

public static class OperationalStates
{
    public const string Live = "live";
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
}

public sealed class OperationalReadinessOptions
{
    public int OutboxBacklogWarningThreshold { get; init; } = 100;
    public int OutboxBacklogCriticalThreshold { get; init; } = 1_000;
    public int OutboxDueWarningThreshold { get; init; } = 25;
    public int OutboxDueCriticalThreshold { get; init; } = 250;
    public int OutboxFailedWarningThreshold { get; init; } = 1;
    public int OutboxFailedCriticalThreshold { get; init; } = 50;
    public int AuditIntentPendingWarningThreshold { get; init; } = 25;
    public int AuditIntentPendingCriticalThreshold { get; init; } = 250;
    public int AuditIntentFailedWarningThreshold { get; init; } = 1;
    public int AuditIntentFailedCriticalThreshold { get; init; } = 10;
    public int ChannelDriftWarningThreshold { get; init; } = 1;
    public int ChannelDriftCriticalThreshold { get; init; } = 20;
    public int ChannelFailedInboxWarningThreshold { get; init; } = 1;
    public int ChannelFailedInboxCriticalThreshold { get; init; } = 10;

    internal void Validate()
    {
        ValidatePair(
            OutboxBacklogWarningThreshold,
            OutboxBacklogCriticalThreshold,
            nameof(OutboxBacklogWarningThreshold),
            nameof(OutboxBacklogCriticalThreshold));
        ValidatePair(
            OutboxDueWarningThreshold,
            OutboxDueCriticalThreshold,
            nameof(OutboxDueWarningThreshold),
            nameof(OutboxDueCriticalThreshold));
        ValidatePair(
            OutboxFailedWarningThreshold,
            OutboxFailedCriticalThreshold,
            nameof(OutboxFailedWarningThreshold),
            nameof(OutboxFailedCriticalThreshold));
        ValidatePair(
            AuditIntentPendingWarningThreshold,
            AuditIntentPendingCriticalThreshold,
            nameof(AuditIntentPendingWarningThreshold),
            nameof(AuditIntentPendingCriticalThreshold));
        ValidatePair(
            AuditIntentFailedWarningThreshold,
            AuditIntentFailedCriticalThreshold,
            nameof(AuditIntentFailedWarningThreshold),
            nameof(AuditIntentFailedCriticalThreshold));
        ValidatePair(
            ChannelDriftWarningThreshold,
            ChannelDriftCriticalThreshold,
            nameof(ChannelDriftWarningThreshold),
            nameof(ChannelDriftCriticalThreshold));
        ValidatePair(
            ChannelFailedInboxWarningThreshold,
            ChannelFailedInboxCriticalThreshold,
            nameof(ChannelFailedInboxWarningThreshold),
            nameof(ChannelFailedInboxCriticalThreshold));
    }

    private static void ValidatePair(
        int warning,
        int critical,
        string warningName,
        string criticalName)
    {
        if (warning <= 0)
        {
            throw new ArgumentOutOfRangeException(warningName, "Threshold must be positive.");
        }

        if (critical < warning)
        {
            throw new ArgumentOutOfRangeException(
                criticalName,
                "Critical threshold cannot be below its warning threshold.");
        }
    }
}

public sealed record LivenessResponse(string Status, bool Live);

public sealed record DatabaseHealthResponse(string Status, bool CanConnect);

public sealed record GatewayCapabilityResponse(string Status, bool Enabled);

public sealed record LegalDocumentsHealthResponse(
    string Status,
    int Required,
    int Published);

public sealed record OutboxHealthResponse(
    string Status,
    int Backlog,
    int Due,
    int Failed);

public sealed record AuditIntentHealthResponse(
    string Status,
    int Pending,
    int StaleProcessing,
    int Failed);

public sealed record SalesChannelHealthResponse(
    string Status,
    int RequestedEnabled,
    int Misconfigured,
    int DriftedListings,
    int FailedInbox);

/// <summary>
/// Public operational state only. It intentionally contains no provider names,
/// connection details, exceptions, credentials, payloads, or customer data.
/// </summary>
public sealed record ReadinessResponse(
    string Status,
    bool Ready,
    DateTimeOffset ObservedAtUtc,
    DatabaseHealthResponse Database,
    GatewayCapabilityResponse Payment,
    GatewayCapabilityResponse Invoice,
    GatewayCapabilityResponse InventoryReservationExpiry,
    LegalDocumentsHealthResponse LegalDocuments,
    OutboxHealthResponse Outbox,
    AuditIntentHealthResponse AuditIntents,
    SalesChannelHealthResponse SalesChannels);

public readonly record struct OutboxHealthCounts(int Backlog, int Due, int Failed)
{
    public static OutboxHealthCounts Empty => new(0, 0, 0);
}

public readonly record struct AuditIntentHealthCounts(
    int Pending,
    int StaleProcessing,
    int Failed)
{
    public static AuditIntentHealthCounts Empty => new(0, 0, 0);
}

public readonly record struct SalesChannelHealthCounts(
    int RequestedEnabled,
    int Misconfigured,
    int DriftedListings,
    int FailedInbox)
{
    public static SalesChannelHealthCounts Empty => new(0, 0, 0, 0);
}

public readonly record struct LegalDocumentHealthCounts(int Required, int Published)
{
    public static LegalDocumentHealthCounts Ready => new(2, 2);
}

public sealed class OperationalReadinessService
{
    private readonly AutoPartsDbContext _context;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IInvoiceGateway _invoiceGateway;
    private readonly OperationalReadinessOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OperationalReadinessService> _logger;
    private readonly InventoryReservationExpiryOptions _inventoryExpiryOptions;
    private readonly ISalesChannelAdapterRegistry _salesChannelAdapters;
    private readonly LegalCheckoutOptions _legalCheckoutOptions;

    public OperationalReadinessService(
        AutoPartsDbContext context,
        IPaymentGateway paymentGateway,
        IInvoiceGateway invoiceGateway,
        OperationalReadinessOptions options,
        TimeProvider timeProvider,
        ILogger<OperationalReadinessService> logger,
        InventoryReservationExpiryOptions? inventoryExpiryOptions = null,
        ISalesChannelAdapterRegistry? salesChannelAdapters = null,
        LegalCheckoutOptions? legalCheckoutOptions = null)
    {
        _context = context;
        _paymentGateway = paymentGateway;
        _invoiceGateway = invoiceGateway;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _inventoryExpiryOptions = inventoryExpiryOptions ?? new InventoryReservationExpiryOptions();
        _salesChannelAdapters = salesChannelAdapters ?? new DisabledSalesChannelAdapterRegistry();
        _legalCheckoutOptions = legalCheckoutOptions ?? new LegalCheckoutOptions();
        _options.Validate();
        _inventoryExpiryOptions.Validate();
        _legalCheckoutOptions.Validate();
    }

    public static LivenessResponse Liveness() =>
        new(OperationalStates.Live, true);

    public async Task<ReadinessResponse> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        var observedAtUtc = _timeProvider.GetUtcNow();
        bool databaseAvailable;

        try
        {
            databaseAvailable = await _context.Database.CanConnectAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Database readiness probe failed. ExceptionType: {ExceptionType}",
                exception.GetType().Name);
            databaseAvailable = false;
        }

        if (!databaseAvailable)
        {
            return OperationalReadinessEvaluator.Evaluate(
                false,
                _paymentGateway.IsEnabled,
                _invoiceGateway.IsEnabled,
                OutboxHealthCounts.Empty,
                _options,
                observedAtUtc,
                outboxAvailable: false,
                auditIntents: AuditIntentHealthCounts.Empty,
                auditIntentsAvailable: false,
                salesChannels: SalesChannelHealthCounts.Empty,
                salesChannelsAvailable: false,
                inventoryReservationExpiryEnabled: _inventoryExpiryOptions.Enabled,
                legalDocuments: new LegalDocumentHealthCounts(
                    _legalCheckoutOptions.RequiredDocumentTypes.Length,
                    0),
                legalDocumentsAvailable: false);
        }

        try
        {
            var now = observedAtUtc.UtcDateTime;
            var backlog = await _context.OutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.ProcessedAt == null, cancellationToken);
            var due = await _context.OutboxMessages
                .AsNoTracking()
                .CountAsync(
                    message =>
                        message.ProcessedAt == null &&
                        (message.NextAttemptAt == null || message.NextAttemptAt <= now),
                    cancellationToken);
            var failed = await _context.OutboxMessages
                .AsNoTracking()
                .CountAsync(message => message.LastError != null, cancellationToken);
            var auditPending = await _context.AdminAuditIntents
                .AsNoTracking()
                .CountAsync(
                    intent => intent.Status == AdminAuditIntentStatuses.Pending,
                    cancellationToken);
            var staleAuditProcessing = await _context.AdminAuditIntents
                .AsNoTracking()
                .CountAsync(
                    intent =>
                        intent.Status == AdminAuditIntentStatuses.Processing &&
                        intent.LeaseExpiresAtUtc <= now,
                    cancellationToken);
            var failedAuditIntents = await _context.AdminAuditIntents
                .AsNoTracking()
                .CountAsync(
                    intent => intent.Status == AdminAuditIntentStatuses.Failed,
                    cancellationToken);
            var requestedChannels = await _context.SalesChannels
                .AsNoTracking()
                .Where(channel => channel.RequestedEnabled)
                .Select(channel => new { channel.Code, channel.Mode })
                .ToListAsync(cancellationToken);
            var misconfiguredChannels = requestedChannels.Count(channel =>
            {
                var capability = _salesChannelAdapters.GetCapability(channel.Code);
                return channel.Mode == SalesChannelModes.Disabled ||
                       !capability.IsConfigured ||
                       (channel.Mode == SalesChannelModes.Sandbox && !capability.SupportsSandbox) ||
                       (channel.Mode == SalesChannelModes.Production && !capability.SupportsProduction);
            });
            var driftedListings = await _context.ChannelListings
                .AsNoTracking()
                .CountAsync(
                    listing => listing.Status == ChannelListingStatuses.Error ||
                               listing.ObservedPrice != listing.DesiredPrice ||
                               listing.ObservedStock != listing.DesiredStock,
                    cancellationToken);
            var failedInbox = await _context.ChannelInboxEvents
                .AsNoTracking()
                .CountAsync(
                    inbox => inbox.Status == ChannelInboxStatuses.Failed,
                    cancellationToken);
            var publishedLegalDocuments = await _context.LegalDocumentVersions
                .AsNoTracking()
                .CountAsync(
                    document =>
                        document.Status == LegalDocumentStatuses.Published &&
                        document.PublishedAtUtc != null &&
                        _legalCheckoutOptions.RequiredDocumentTypes.Contains(document.DocumentType),
                    cancellationToken);

            return OperationalReadinessEvaluator.Evaluate(
                true,
                _paymentGateway.IsEnabled,
                _invoiceGateway.IsEnabled,
                new OutboxHealthCounts(backlog, due, failed),
                _options,
                observedAtUtc,
                auditIntents: new AuditIntentHealthCounts(
                    auditPending,
                    staleAuditProcessing,
                    failedAuditIntents),
                salesChannels: new SalesChannelHealthCounts(
                    requestedChannels.Count,
                    misconfiguredChannels,
                    driftedListings,
                    failedInbox),
                inventoryReservationExpiryEnabled: _inventoryExpiryOptions.Enabled,
                legalDocuments: new LegalDocumentHealthCounts(
                    _legalCheckoutOptions.RequiredDocumentTypes.Length,
                    publishedLegalDocuments));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Operational readiness probe failed. ExceptionType: {ExceptionType}",
                exception.GetType().Name);
            return OperationalReadinessEvaluator.Evaluate(
                true,
                _paymentGateway.IsEnabled,
                _invoiceGateway.IsEnabled,
                OutboxHealthCounts.Empty,
                _options,
                observedAtUtc,
                outboxAvailable: false,
                auditIntents: AuditIntentHealthCounts.Empty,
                auditIntentsAvailable: false,
                salesChannels: SalesChannelHealthCounts.Empty,
                salesChannelsAvailable: false,
                inventoryReservationExpiryEnabled: _inventoryExpiryOptions.Enabled,
                legalDocuments: new LegalDocumentHealthCounts(
                    _legalCheckoutOptions.RequiredDocumentTypes.Length,
                    0),
                legalDocumentsAvailable: false);
        }
    }
}

public static class OperationalReadinessEvaluator
{
    public static ReadinessResponse Evaluate(
        bool databaseAvailable,
        bool paymentEnabled,
        bool invoiceEnabled,
        OutboxHealthCounts outbox,
        OperationalReadinessOptions options,
        DateTimeOffset? observedAtUtc = null,
        bool outboxAvailable = true,
        AuditIntentHealthCounts auditIntents = default,
        bool auditIntentsAvailable = true,
        bool inventoryReservationExpiryEnabled = true,
        SalesChannelHealthCounts salesChannels = default,
        bool salesChannelsAvailable = true,
        LegalDocumentHealthCounts? legalDocuments = null,
        bool legalDocumentsAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (outbox.Backlog < 0 || outbox.Due < 0 || outbox.Failed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outbox),
                "Outbox measurements cannot be negative.");
        }

        if (auditIntents.Pending < 0 ||
            auditIntents.StaleProcessing < 0 ||
            auditIntents.Failed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditIntents),
                "Audit intent measurements cannot be negative.");
        }

        if (salesChannels.RequestedEnabled < 0 ||
            salesChannels.Misconfigured < 0 ||
            salesChannels.DriftedListings < 0 ||
            salesChannels.FailedInbox < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(salesChannels),
                "Sales channel measurements cannot be negative.");
        }

        var legal = legalDocuments ?? LegalDocumentHealthCounts.Ready;
        if (legal.Required <= 0 || legal.Published < 0 || legal.Published > legal.Required)
        {
            throw new ArgumentOutOfRangeException(
                nameof(legalDocuments),
                "Legal document measurements are invalid.");
        }

        var outboxCritical =
            outbox.Backlog >= options.OutboxBacklogCriticalThreshold ||
            outbox.Due >= options.OutboxDueCriticalThreshold ||
            outbox.Failed >= options.OutboxFailedCriticalThreshold;
        var outboxWarning =
            outbox.Backlog >= options.OutboxBacklogWarningThreshold ||
            outbox.Due >= options.OutboxDueWarningThreshold ||
            outbox.Failed >= options.OutboxFailedWarningThreshold;
        var auditCritical =
            auditIntents.Pending >= options.AuditIntentPendingCriticalThreshold ||
            auditIntents.StaleProcessing >= options.AuditIntentPendingCriticalThreshold ||
            auditIntents.Failed >= options.AuditIntentFailedCriticalThreshold;
        var auditWarning =
            auditIntents.Pending >= options.AuditIntentPendingWarningThreshold ||
            auditIntents.StaleProcessing >= options.AuditIntentPendingWarningThreshold ||
            auditIntents.Failed >= options.AuditIntentFailedWarningThreshold;
        var reservationExpiryMisconfigured =
            paymentEnabled && !inventoryReservationExpiryEnabled;
        var channelsCritical =
            salesChannels.Misconfigured > 0 ||
            salesChannels.DriftedListings >= options.ChannelDriftCriticalThreshold ||
            salesChannels.FailedInbox >= options.ChannelFailedInboxCriticalThreshold;
        var channelsWarning =
            salesChannels.DriftedListings >= options.ChannelDriftWarningThreshold ||
            salesChannels.FailedInbox >= options.ChannelFailedInboxWarningThreshold;
        var legalDocumentsMisconfigured =
            !legalDocumentsAvailable || legal.Published != legal.Required;

        var ready = databaseAvailable &&
                    outboxAvailable &&
                    auditIntentsAvailable &&
                    salesChannelsAvailable &&
                    !outboxCritical &&
                    !auditCritical &&
                    !channelsCritical &&
                    !reservationExpiryMisconfigured &&
                    !legalDocumentsMisconfigured;
        var degraded = ready &&
            (outboxWarning || auditWarning || channelsWarning || !paymentEnabled || !invoiceEnabled);
        var status = !ready
            ? OperationalStates.Unavailable
            : degraded
                ? OperationalStates.Degraded
                : OperationalStates.Ready;
        var outboxStatus = !outboxAvailable || outboxCritical
            ? OperationalStates.Unavailable
            : outboxWarning
                ? OperationalStates.Degraded
                : OperationalStates.Ready;
        var auditStatus = !auditIntentsAvailable || auditCritical
            ? OperationalStates.Unavailable
            : auditWarning
                ? OperationalStates.Degraded
                : OperationalStates.Ready;
        var channelStatus = !salesChannelsAvailable || channelsCritical
            ? OperationalStates.Unavailable
            : channelsWarning
                ? OperationalStates.Degraded
                : OperationalStates.Ready;

        return new ReadinessResponse(
            status,
            ready,
            observedAtUtc ?? DateTimeOffset.UtcNow,
            new DatabaseHealthResponse(
                databaseAvailable ? OperationalStates.Ready : OperationalStates.Unavailable,
                databaseAvailable),
            new GatewayCapabilityResponse(
                paymentEnabled ? OperationalStates.Enabled : OperationalStates.Disabled,
                paymentEnabled),
            new GatewayCapabilityResponse(
                invoiceEnabled ? OperationalStates.Enabled : OperationalStates.Disabled,
                invoiceEnabled),
            new GatewayCapabilityResponse(
                inventoryReservationExpiryEnabled
                    ? OperationalStates.Enabled
                    : OperationalStates.Disabled,
                inventoryReservationExpiryEnabled),
            new LegalDocumentsHealthResponse(
                legalDocumentsMisconfigured
                    ? OperationalStates.Unavailable
                    : OperationalStates.Ready,
                legal.Required,
                legal.Published),
            new OutboxHealthResponse(
                outboxStatus,
                outbox.Backlog,
                outbox.Due,
                outbox.Failed),
            new AuditIntentHealthResponse(
                auditStatus,
                auditIntents.Pending,
                auditIntents.StaleProcessing,
                auditIntents.Failed),
            new SalesChannelHealthResponse(
                channelStatus,
                salesChannels.RequestedEnabled,
                salesChannels.Misconfigured,
                salesChannels.DriftedListings,
                salesChannels.FailedInbox));
    }
}
