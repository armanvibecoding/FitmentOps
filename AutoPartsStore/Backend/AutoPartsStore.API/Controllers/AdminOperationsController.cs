using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Invoicing;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Admin")]
[Authorize]
public sealed class AdminOperationsController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly FulfillmentService _fulfillmentService;
    private readonly ReturnService _returnService;
    private readonly IPaymentGateway _paymentGateway;
    private readonly IInvoiceGateway _invoiceGateway;
    private readonly IOutboxMessageDispatcher _outboxDispatcher;
    private readonly OutboxWorkerOptions _outboxWorkerOptions;
    private readonly InventoryReservationExpiryOptions _reservationExpiryOptions;
    private readonly PublicSiteOptions _publicSiteOptions;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly AdminAuditService _adminAuditService;
    private readonly AdminAuditIntentService _adminAuditIntentService;
    private readonly ILogger<AdminOperationsController> _logger;

    public AdminOperationsController(
        AutoPartsDbContext context,
        FulfillmentService fulfillmentService,
        ReturnService returnService,
        IPaymentGateway paymentGateway,
        IInvoiceGateway invoiceGateway,
        IOutboxMessageDispatcher outboxDispatcher,
        OutboxWorkerOptions outboxWorkerOptions,
        InventoryReservationExpiryOptions reservationExpiryOptions,
        PublicSiteOptions publicSiteOptions,
        IConfiguration configuration,
        TimeProvider timeProvider,
        AdminAuditService adminAuditService,
        AdminAuditIntentService adminAuditIntentService,
        ILogger<AdminOperationsController> logger)
    {
        _context = context;
        _fulfillmentService = fulfillmentService;
        _returnService = returnService;
        _paymentGateway = paymentGateway;
        _invoiceGateway = invoiceGateway;
        _outboxDispatcher = outboxDispatcher;
        _outboxWorkerOptions = outboxWorkerOptions;
        _reservationExpiryOptions = reservationExpiryOptions;
        _publicSiteOptions = publicSiteOptions;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _adminAuditService = adminAuditService;
        _adminAuditIntentService = adminAuditIntentService;
        _logger = logger;
    }

    [HttpGet("shipments")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public async Task<ActionResult<IEnumerable<AdminShipmentDto>>> GetShipments(
        CancellationToken cancellationToken)
    {
        var shipments = await _context.Shipments
            .AsNoTracking()
            .Include(shipment => shipment.Order)
            .Include(shipment => shipment.Items)
                .ThenInclude(item => item.OrderItem)
                .ThenInclude(item => item.Product)
            .OrderByDescending(shipment => shipment.CreatedAt)
            .ToListAsync(cancellationToken);

        return shipments.Select(ToAdminShipment).ToList();
    }

    [HttpPost("orders/{orderId:int}/shipments")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public async Task<IActionResult> CreateShipment(
        int orderId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CreateAdminShipmentDto dto,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAuditedMutationAsync(
            token => _fulfillmentService.CreateShipmentAsync(
                orderId,
                idempotencyKey,
                dto.Items.Select(item => new ShipmentLineRequest(
                    item.OrderItemId,
                    item.Quantity)).ToArray(),
                _timeProvider.GetUtcNow(),
                token),
            result => result.Shipment != null &&
                      result.Outcome is FulfillmentOutcome.Created or FulfillmentOutcome.Replayed
                ? new PendingAdminAudit(
                    AdminAuditActions.ShipmentCreated,
                    AdminAuditAggregateTypes.Shipment,
                    result.Shipment.Id,
                    result.Outcome == FulfillmentOutcome.Replayed
                        ? AdminAuditOutcomes.Replayed
                        : AdminAuditOutcomes.Succeeded)
                : null,
            cancellationToken);

        return MapFulfillmentResult(result, StatusCodes.Status201Created);
    }

    [HttpPost("shipments/{shipmentId:int}/label-pending")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> MarkLabelPending(
        int shipmentId,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.LabelPending, null, cancellationToken);

    [HttpPost("shipments/{shipmentId:int}/ready-to-ship")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> MarkReadyToShip(
        int shipmentId,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.ReadyToShip, null, cancellationToken);

    [HttpPost("shipments/{shipmentId:int}/ship")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> MarkShipped(
        int shipmentId,
        ShipAdminShipmentDto dto,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.Shipped, dto, cancellationToken);

    [HttpPost("shipments/{shipmentId:int}/deliver")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> MarkDelivered(
        int shipmentId,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.Delivered, null, cancellationToken);

    [HttpPost("shipments/{shipmentId:int}/fail")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> MarkShipmentFailed(
        int shipmentId,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.Failed, null, cancellationToken);

    [HttpPost("shipments/{shipmentId:int}/cancel")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> CancelShipment(
        int shipmentId,
        CancellationToken cancellationToken) =>
        TransitionShipment(shipmentId, ShipmentStatuses.Cancelled, null, cancellationToken);

    [HttpGet("returns")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public async Task<ActionResult<IEnumerable<AdminReturnDto>>> GetReturns(
        CancellationToken cancellationToken)
    {
        var requests = await _context.ReturnRequests
            .AsNoTracking()
            .Include(request => request.Order)
            .Include(request => request.Items)
                .ThenInclude(item => item.OrderItem)
                .ThenInclude(item => item.Product)
            .OrderByDescending(request => request.RequestedAt)
            .ToListAsync(cancellationToken);

        return requests.Select(ToAdminReturn).ToList();
    }

    [HttpPost("orders/{orderId:int}/returns")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public async Task<IActionResult> CreateReturn(
        int orderId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CreateAdminReturnDto dto,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAuditedMutationAsync(
            token => _returnService.RequestAsync(
                orderId,
                idempotencyKey,
                dto.Items.Select(item => new ReturnItemRequest(
                    item.OrderItemId,
                    item.Quantity,
                    item.ReasonCode)).ToArray(),
                token),
            result => result.ReturnRequest != null &&
                      result.Outcome is ReturnServiceOutcome.Created or ReturnServiceOutcome.Replayed
                ? new PendingAdminAudit(
                    AdminAuditActions.ReturnCreated,
                    AdminAuditAggregateTypes.Return,
                    result.ReturnRequest.Id,
                    result.Outcome == ReturnServiceOutcome.Replayed
                        ? AdminAuditOutcomes.Replayed
                        : AdminAuditOutcomes.Succeeded)
                : null,
            cancellationToken);

        return MapReturnResult(result, StatusCodes.Status201Created);
    }

    [HttpPost("returns/{returnRequestId:long}/approve")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> ApproveReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Approved, cancellationToken);

    [HttpPost("returns/{returnRequestId:long}/reject")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> RejectReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Rejected, cancellationToken);

    [HttpPost("returns/{returnRequestId:long}/receive")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> ReceiveReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Received, cancellationToken);

    [HttpPost("returns/{returnRequestId:long}/inspect")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> InspectReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Inspected, cancellationToken);

    [HttpPost("returns/{returnRequestId:long}/cancel")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> CancelReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Cancelled, cancellationToken);

    [HttpPost("returns/{returnRequestId:long}/close")]
    [Authorize(Policy = AdminPolicyNames.Returns)]
    public Task<IActionResult> CloseReturn(
        long returnRequestId,
        CancellationToken cancellationToken) =>
        TransitionReturn(returnRequestId, ReturnRequestStatuses.Closed, cancellationToken);

    [HttpGet("integrations/capabilities")]
    [Authorize(Policy = AdminPolicyNames.AdminAccess)]
    public ActionResult<AdminIntegrationCapabilitiesDto> GetIntegrationCapabilities()
    {
        var emailConfigured = IsEmailConfigured();
        var publicSiteConfigured = _publicSiteOptions.TryGetBaseUri(out _);
        var outboxConfigured = _outboxWorkerOptions.Enabled && _outboxDispatcher.IsEnabled;

        return new AdminIntegrationCapabilitiesDto
        {
            Payment = new AdminIntegrationCapabilityDto
            {
                Provider = _paymentGateway.ProviderName,
                Enabled = _paymentGateway.IsEnabled,
                Mode = _paymentGateway.IsEnabled ? "ConfiguredUnverified" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = _paymentGateway.IsEnabled
                    ? "ProviderCertificationRequired"
                    : "ProviderAdapterDisabled"
            },
            ElectronicInvoice = new AdminIntegrationCapabilityDto
            {
                Provider = _invoiceGateway.ProviderName,
                Enabled = _invoiceGateway.IsEnabled,
                Mode = _invoiceGateway.IsEnabled ? "ConfiguredUnverified" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = _invoiceGateway.IsEnabled
                    ? "ProviderCertificationRequired"
                    : "ProviderAdapterDisabled"
            },
            Email = new AdminIntegrationCapabilityDto
            {
                Provider = "SMTP",
                Enabled = emailConfigured,
                Mode = emailConfigured ? "ConfiguredUnverified" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = emailConfigured
                    ? "DeliveryVerificationRequired"
                    : "ConfigurationIncomplete"
            },
            OutboxDispatch = new AdminIntegrationCapabilityDto
            {
                Provider = _outboxDispatcher.IsEnabled ? "RegisteredDispatcher" : "DisabledDispatcher",
                Enabled = outboxConfigured,
                Mode = outboxConfigured
                    ? "ConfiguredUnverified"
                    : _outboxWorkerOptions.Enabled ? "DispatcherMissing" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = outboxConfigured
                    ? "EndToEndVerificationRequired"
                    : _outboxWorkerOptions.Enabled
                        ? "DispatcherDisabled"
                        : "WorkerDisabled"
            },
            InventoryReservationExpiry = new AdminIntegrationCapabilityDto
            {
                Provider = "BuiltInWorker",
                Enabled = _reservationExpiryOptions.Enabled,
                Mode = _reservationExpiryOptions.Enabled ? "ConfiguredUnverified" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = _reservationExpiryOptions.Enabled
                    ? "ProductionDatabaseVerificationRequired"
                    : "WorkerDisabled"
            },
            PublicSite = new AdminIntegrationCapabilityDto
            {
                Provider = "ApplicationConfiguration",
                Enabled = publicSiteConfigured,
                Mode = publicSiteConfigured ? "ConfiguredUnverified" : "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = publicSiteConfigured
                    ? "PublicReachabilityVerificationRequired"
                    : "CanonicalOriginMissing"
            },
            ShippingCarrier = new AdminIntegrationCapabilityDto
            {
                Provider = "ManualOnly",
                Enabled = false,
                Mode = "FailClosed",
                HealthStatus = "NotChecked",
                LiveReady = false,
                BlockingReason = "CarrierAdapterMissing"
            }
        };
    }

    private bool IsEmailConfigured()
    {
        var smtpPort = _configuration["EmailSettings:SmtpPort"];
        return !string.IsNullOrWhiteSpace(_configuration["EmailSettings:SmtpServer"]) &&
               int.TryParse(smtpPort, out var parsedPort) &&
               parsedPort is >= 1 and <= 65_535 &&
               !string.IsNullOrWhiteSpace(_configuration["EmailSettings:SenderEmail"]) &&
               !string.IsNullOrWhiteSpace(_configuration["EmailSettings:Username"]) &&
               !string.IsNullOrWhiteSpace(_configuration["EmailSettings:Password"]);
    }

    private async Task<IActionResult> TransitionShipment(
        int shipmentId,
        string targetStatus,
        ShipAdminShipmentDto? shipping,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAuditedMutationAsync(
            token => _fulfillmentService.TransitionAsync(
                shipmentId,
                targetStatus,
                _timeProvider.GetUtcNow(),
                shipping?.Carrier,
                shipping?.TrackingNumber,
                token),
            result => result.Shipment != null &&
                      result.Outcome is FulfillmentOutcome.Updated or FulfillmentOutcome.Replayed
                ? new PendingAdminAudit(
                    AdminAuditActions.ForShipmentStatus(targetStatus),
                    AdminAuditAggregateTypes.Shipment,
                    result.Shipment.Id,
                    result.Outcome == FulfillmentOutcome.Replayed
                        ? AdminAuditOutcomes.Replayed
                        : AdminAuditOutcomes.Succeeded)
                : null,
            cancellationToken);

        return MapFulfillmentResult(result);
    }

    private async Task<IActionResult> TransitionReturn(
        long returnRequestId,
        string targetStatus,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAuditedMutationAsync(
            token => _returnService.TransitionAsync(
                returnRequestId,
                targetStatus,
                token),
            result => result.ReturnRequest != null &&
                      result.Outcome is ReturnServiceOutcome.Updated or ReturnServiceOutcome.Replayed
                ? new PendingAdminAudit(
                    AdminAuditActions.ForReturnStatus(targetStatus),
                    AdminAuditAggregateTypes.Return,
                    result.ReturnRequest.Id,
                    result.Outcome == ReturnServiceOutcome.Replayed
                        ? AdminAuditOutcomes.Replayed
                        : AdminAuditOutcomes.Succeeded)
                : null,
            cancellationToken);

        return MapReturnResult(result);
    }

    private async Task<T> ExecuteAuditedMutationAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        Func<T, PendingAdminAudit?> buildAudit,
        CancellationToken cancellationToken)
    {
        T result;
        var staged = false;
        await using (var transaction = _context.Database.IsRelational()
                         ? await _context.Database.BeginTransactionAsync(
                             IsolationLevel.Serializable,
                             cancellationToken)
                         : null)
        {
            try
            {
                result = await mutation(cancellationToken);
                var audit = buildAudit(result);
                if (audit == null)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    return result;
                }

                StageAuditIntent(audit);
                await _context.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                staged = true;
            }
            catch
            {
                if (transaction != null)
                {
                    await TryRollbackAsync(transaction, cancellationToken);
                }

                throw;
            }
        }

        if (staged)
        {
            await TryDispatchAuditIntentsAsync();
        }

        return result;
    }

    private void StageAuditIntent(PendingAdminAudit audit)
    {
        var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorRole = User.FindFirstValue(ClaimTypes.Role);
        if (!int.TryParse(actorClaim, out var actorUserId) ||
            string.IsNullOrWhiteSpace(actorRole))
        {
            throw new InvalidOperationException("Authenticated admin audit identity is missing.");
        }

        var result = _adminAuditIntentService.Stage(
            new AdminAuditIntentStageRequest(
                Guid.NewGuid(),
                actorUserId,
                actorRole,
                audit.Action,
                audit.AggregateType,
                audit.AggregateId,
                HttpContext.TraceIdentifier,
                audit.Outcome));
        if (result.Outcome != AdminAuditIntentStageOutcome.Staged)
        {
            throw new InvalidOperationException($"Admin audit intent staging failed: {result.ErrorCode}");
        }
    }

    private async Task TryDispatchAuditIntentsAsync()
    {
        try
        {
            await _adminAuditIntentService.DispatchBatchAsync(
                _adminAuditService,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Immediate admin audit intent dispatch failed with {ExceptionType}; durable retry remains pending.",
                exception.GetType().Name);
        }
    }

    private static async Task TryRollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The provider may already have rolled back a failed transaction.
        }
    }

    private sealed record PendingAdminAudit(
        string Action,
        string AggregateType,
        long AggregateId,
        string Outcome);

    private IActionResult MapFulfillmentResult(
        FulfillmentResult result,
        int createdStatusCode = StatusCodes.Status200OK)
    {
        var response = result.Shipment == null
            ? null
            : new AdminOperationResultDto
            {
                Id = result.Shipment.Id,
                Status = result.Shipment.Status,
                Replayed = result.Outcome == FulfillmentOutcome.Replayed
            };

        return result.Outcome switch
        {
            FulfillmentOutcome.Created => StatusCode(createdStatusCode, response),
            FulfillmentOutcome.Updated => Ok(response),
            FulfillmentOutcome.Replayed => Ok(response),
            FulfillmentOutcome.NotFound => NotFound(new { message = result.Message ?? "Resource not found." }),
            FulfillmentOutcome.Conflict => Conflict(new { message = result.Message }),
            FulfillmentOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private IActionResult MapReturnResult(
        ReturnServiceResult result,
        int createdStatusCode = StatusCodes.Status200OK)
    {
        var response = result.ReturnRequest == null
            ? null
            : new AdminOperationResultDto
            {
                Id = result.ReturnRequest.Id,
                Status = result.ReturnRequest.Status,
                Replayed = result.Outcome == ReturnServiceOutcome.Replayed
            };

        return result.Outcome switch
        {
            ReturnServiceOutcome.Created => StatusCode(createdStatusCode, response),
            ReturnServiceOutcome.Updated => Ok(response),
            ReturnServiceOutcome.Replayed => Ok(response),
            ReturnServiceOutcome.NotFound => NotFound(new { message = result.Message ?? "Resource not found." }),
            ReturnServiceOutcome.Conflict => Conflict(new { message = result.Message }),
            ReturnServiceOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private static AdminShipmentDto ToAdminShipment(Shipment shipment) => new()
    {
        Id = shipment.Id,
        OrderId = shipment.OrderId,
        OrderNumber = shipment.Order.OrderNumber,
        Status = shipment.Status,
        Carrier = shipment.Carrier,
        TrackingNumber = shipment.TrackingNumber,
        CreatedAt = shipment.CreatedAt,
        UpdatedAt = shipment.UpdatedAt,
        ShippedAt = shipment.ShippedAt,
        DeliveredAt = shipment.DeliveredAt,
        Items = shipment.Items.Select(item => new AdminShipmentItemDto
        {
            OrderItemId = item.OrderItemId,
            ProductName = item.OrderItem.Product.Name,
            PartNumber = item.OrderItem.Product.PartNumber,
            Quantity = item.Quantity
        }).ToList()
    };

    private static AdminReturnDto ToAdminReturn(ReturnRequest request) => new()
    {
        Id = request.Id,
        OrderId = request.OrderId,
        OrderNumber = request.Order.OrderNumber,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        UpdatedAt = request.UpdatedAt,
        RefundedAt = request.RefundedAt,
        Items = request.Items.Select(item => new AdminReturnItemDto
        {
            OrderItemId = item.OrderItemId,
            ProductName = item.OrderItem.Product.Name,
            PartNumber = item.OrderItem.Product.PartNumber,
            Quantity = item.Quantity,
            ReasonCode = item.ReasonCode
        }).ToList()
    };
}

public sealed class CreateAdminShipmentDto
{
    [Required, MinLength(1), MaxLength(100)]
    public List<AdminShipmentLineDto> Items { get; set; } = [];
}

public sealed class AdminShipmentLineDto
{
    [Range(1, int.MaxValue)]
    public int OrderItemId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}

public sealed class ShipAdminShipmentDto
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Carrier { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 1)]
    public string TrackingNumber { get; set; } = string.Empty;
}

public sealed class CreateAdminReturnDto
{
    [Required, MinLength(1), MaxLength(100)]
    public List<AdminReturnLineDto> Items { get; set; } = [];
}

public sealed class AdminReturnLineDto
{
    [Range(1, int.MaxValue)]
    public int OrderItemId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }

    [Required, StringLength(40, MinimumLength = 1)]
    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class AdminOperationResultDto
{
    public long Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool Replayed { get; set; }
}

public sealed class AdminShipmentDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public List<AdminShipmentItemDto> Items { get; set; } = [];
}

public sealed class AdminShipmentItemDto
{
    public int OrderItemId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class AdminReturnDto
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? RefundedAt { get; set; }
    public List<AdminReturnItemDto> Items { get; set; } = [];
}

public sealed class AdminReturnItemDto
{
    public int OrderItemId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
}

public sealed class AdminIntegrationCapabilitiesDto
{
    public AdminIntegrationCapabilityDto Payment { get; set; } = new();
    public AdminIntegrationCapabilityDto ElectronicInvoice { get; set; } = new();
    public AdminIntegrationCapabilityDto Email { get; set; } = new();
    public AdminIntegrationCapabilityDto OutboxDispatch { get; set; } = new();
    public AdminIntegrationCapabilityDto InventoryReservationExpiry { get; set; } = new();
    public AdminIntegrationCapabilityDto PublicSite { get; set; } = new();
    public AdminIntegrationCapabilityDto ShippingCarrier { get; set; } = new();
}

public sealed class AdminIntegrationCapabilityDto
{
    public string Provider { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public bool LiveReady { get; set; }
    public string BlockingReason { get; set; } = string.Empty;
}
