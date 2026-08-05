using System.ComponentModel.DataAnnotations;
using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

[Index(nameof(Sequence), IsUnique = true)]
[Index(nameof(IdempotencyKeySha256), IsUnique = true)]
[Index(nameof(OccurredAtUtc))]
public sealed class AdminAuditEvent
{
    private AdminAuditEvent()
    {
    }

    public long Id { get; private set; }

    public long Sequence { get; private set; }

    public int ActorUserId { get; private set; }

    [Required, StringLength(20)]
    public string ActorRole { get; private set; } = string.Empty;

    [Required, StringLength(50)]
    public string Action { get; private set; } = string.Empty;

    [Required, StringLength(30)]
    public string AggregateType { get; private set; } = string.Empty;

    public long AggregateId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    [Required, StringLength(64, MinimumLength = 64)]
    public string CorrelationIdSha256 { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    public string IdempotencyKeySha256 { get; private set; } = string.Empty;

    [Required, StringLength(20)]
    public string Outcome { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    public string PreviousEventHashSha256 { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    public string EventHashSha256 { get; private set; } = string.Empty;

    internal static AdminAuditEvent Create(
        long sequence,
        int actorUserId,
        string actorRole,
        string action,
        string aggregateType,
        long aggregateId,
        DateTime occurredAtUtc,
        string correlationIdSha256,
        string idempotencyKeySha256,
        string outcome,
        string previousEventHashSha256,
        string eventHashSha256)
    {
        return new AdminAuditEvent
        {
            Sequence = sequence,
            ActorUserId = actorUserId,
            ActorRole = actorRole,
            Action = action,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            OccurredAtUtc = occurredAtUtc,
            CorrelationIdSha256 = correlationIdSha256,
            IdempotencyKeySha256 = idempotencyKeySha256,
            Outcome = outcome,
            PreviousEventHashSha256 = previousEventHashSha256,
            EventHashSha256 = eventHashSha256
        };
    }
}

public static class AdminAuditRoles
{
    public const string Finance = "finance";
    public const string Warehouse = "warehouse";
    public const string Catalog = "catalog";
    public const string Support = "support";
    public const string SuperAdmin = "superadmin";

    // Compatibility bridge for the current JWT role. New policies can be rolled
    // out without invalidating existing Admin tokens.
    public const string LegacyAdmin = "Admin";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        Finance,
        Warehouse,
        Catalog,
        Support,
        SuperAdmin,
        LegacyAdmin
    ]);
}

public static class AdminAuditActions
{
    public const string ProductCreated = "product.created";
    public const string ProductUpdated = "product.updated";
    public const string ProductDeleted = "product.deleted";
    public const string CategoryUpdated = "category.updated";
    public const string BrandUpdated = "brand.updated";
    public const string OrderStatusChanged = "order.status_changed";
    public const string OrderProcessing = "order.status.processing";
    public const string OrderCancelled = "order.status.cancelled";
    public const string PaymentMarkedPaid = "payment.marked_paid";
    public const string ShipmentCreated = "shipment.created";
    public const string ShipmentStatusChanged = "shipment.status_changed";
    public const string ShipmentLabelPending = "shipment.status.label_pending";
    public const string ShipmentReadyToShip = "shipment.status.ready_to_ship";
    public const string ShipmentShipped = "shipment.status.shipped";
    public const string ShipmentDelivered = "shipment.status.delivered";
    public const string ShipmentFailed = "shipment.status.failed";
    public const string ShipmentCancelled = "shipment.status.cancelled";
    public const string ReturnCreated = "return.created";
    public const string ReturnStatusChanged = "return.status_changed";
    public const string ReturnApproved = "return.status.approved";
    public const string ReturnRejected = "return.status.rejected";
    public const string ReturnReceived = "return.status.received";
    public const string ReturnInspected = "return.status.inspected";
    public const string ReturnCancelled = "return.status.cancelled";
    public const string ReturnClosed = "return.status.closed";
    public const string RefundRequested = "refund.requested";
    public const string RefundStatusChanged = "refund.status_changed";
    public const string UserRoleChanged = "user.role_changed";
    public const string VehicleUpserted = "vehicle.upserted";
    public const string ProductFitmentUpserted = "product_fitment.upserted";
    public const string ProductIdentifierUpserted = "product_identifier.upserted";
    public const string DealerApplicationReviewed = "dealer_application.reviewed";
    public const string CustomerGroupUpserted = "customer_group.upserted";
    public const string PriceListUpserted = "price_list.upserted";
    public const string PriceRuleUpserted = "price_rule.upserted";
    public const string BulkQuotePrepared = "bulk_quote.prepared";
    public const string SupplierUpserted = "supplier.upserted";
    public const string SupplierOfferRegistered = "supplier_offer.registered";
    public const string SupplierOfferStatusChanged = "supplier_offer.status_changed";
    public const string SalesChannelStateChanged = "sales_channel.state_changed";
    public const string ChannelListingSyncRequested = "channel_listing.sync_requested";
    public const string LegalDocumentCreated = "legal_document.created";
    public const string LegalDocumentPublished = "legal_document.published";
    public const string LegalDocumentRetired = "legal_document.retired";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        ProductCreated,
        ProductUpdated,
        ProductDeleted,
        CategoryUpdated,
        BrandUpdated,
        OrderStatusChanged,
        OrderProcessing,
        OrderCancelled,
        PaymentMarkedPaid,
        ShipmentCreated,
        ShipmentStatusChanged,
        ShipmentLabelPending,
        ShipmentReadyToShip,
        ShipmentShipped,
        ShipmentDelivered,
        ShipmentFailed,
        ShipmentCancelled,
        ReturnCreated,
        ReturnStatusChanged,
        ReturnApproved,
        ReturnRejected,
        ReturnReceived,
        ReturnInspected,
        ReturnCancelled,
        ReturnClosed,
        RefundRequested,
        RefundStatusChanged,
        UserRoleChanged,
        VehicleUpserted,
        ProductFitmentUpserted,
        ProductIdentifierUpserted,
        DealerApplicationReviewed,
        CustomerGroupUpserted,
        PriceListUpserted,
        PriceRuleUpserted,
        BulkQuotePrepared,
        SupplierUpserted,
        SupplierOfferRegistered,
        SupplierOfferStatusChanged,
        SalesChannelStateChanged,
        ChannelListingSyncRequested,
        LegalDocumentCreated,
        LegalDocumentPublished,
        LegalDocumentRetired
    ]);

    public static string ForOrderStatus(string status) => status switch
    {
        OrderStatuses.Processing => OrderProcessing,
        OrderStatuses.Cancelled => OrderCancelled,
        _ => OrderStatusChanged
    };

    public static string ForShipmentStatus(string status) => status switch
    {
        ShipmentStatuses.LabelPending => ShipmentLabelPending,
        ShipmentStatuses.ReadyToShip => ShipmentReadyToShip,
        ShipmentStatuses.Shipped => ShipmentShipped,
        ShipmentStatuses.Delivered => ShipmentDelivered,
        ShipmentStatuses.Failed => ShipmentFailed,
        ShipmentStatuses.Cancelled => ShipmentCancelled,
        _ => ShipmentStatusChanged
    };

    public static string ForReturnStatus(string status) => status switch
    {
        ReturnRequestStatuses.Approved => ReturnApproved,
        ReturnRequestStatuses.Rejected => ReturnRejected,
        ReturnRequestStatuses.Received => ReturnReceived,
        ReturnRequestStatuses.Inspected => ReturnInspected,
        ReturnRequestStatuses.Cancelled => ReturnCancelled,
        ReturnRequestStatuses.Closed => ReturnClosed,
        _ => ReturnStatusChanged
    };
}

public static class AdminAuditAggregateTypes
{
    public const string Product = "product";
    public const string Category = "category";
    public const string Brand = "brand";
    public const string PartBrand = "part_brand";
    public const string Order = "order";
    public const string Payment = "payment";
    public const string Shipment = "shipment";
    public const string Return = "return";
    public const string Refund = "refund";
    public const string User = "user";
    public const string Vehicle = "vehicle";
    public const string ProductFitment = "product_fitment";
    public const string ProductIdentifier = "product_identifier";
    public const string DealerApplication = "dealer_application";
    public const string CustomerGroup = "customer_group";
    public const string PriceList = "price_list";
    public const string PriceRule = "price_rule";
    public const string BulkQuote = "bulk_quote";
    public const string Supplier = "supplier";
    public const string SupplierOffer = "supplier_offer";
    public const string SalesChannel = "sales_channel";
    public const string ChannelListing = "channel_listing";
    public const string LegalDocument = "legal_document";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        Product,
        Category,
        Brand,
        PartBrand,
        Order,
        Payment,
        Shipment,
        Return,
        Refund,
        User,
        Vehicle,
        ProductFitment,
        ProductIdentifier,
        DealerApplication,
        CustomerGroup,
        PriceList,
        PriceRule,
        BulkQuote,
        Supplier,
        SupplierOffer,
        SalesChannel,
        ChannelListing,
        LegalDocument
    ]);
}

public static class AdminAuditOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Replayed = "replayed";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        Succeeded,
        Rejected,
        Failed,
        Replayed
    ]);
}

public static class AdminPolicyNames
{
    public const string AdminAccess = "AdminPolicy.AdminAccess";
    public const string OperationsRead = "AdminPolicy.OperationsRead";
    public const string Returns = "AdminPolicy.Returns";
    public const string Finance = "AdminPolicy.Finance";
    public const string Warehouse = "AdminPolicy.Warehouse";
    public const string Catalog = "AdminPolicy.Catalog";
    public const string Support = "AdminPolicy.Support";
    public const string SuperAdmin = "AdminPolicy.SuperAdmin";
}

public static class AdminPermissionNames
{
    public const string FinanceManage = "finance.manage";
    public const string WarehouseManage = "warehouse.manage";
    public const string CatalogManage = "catalog.manage";
    public const string SupportManage = "support.manage";
    public const string UserRoleManage = "user-role.manage";
    public const string AuditRead = "audit.read";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        FinanceManage,
        WarehouseManage,
        CatalogManage,
        SupportManage,
        UserRoleManage,
        AuditRead
    ]);
}

public static class AdminRolePermissionMatrix
{
    private static readonly IReadOnlySet<string> EmptyPermissions =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> PermissionsByRole =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [AdminAuditRoles.Finance] = Set(AdminPermissionNames.FinanceManage),
            [AdminAuditRoles.Warehouse] = Set(AdminPermissionNames.WarehouseManage),
            [AdminAuditRoles.Catalog] = Set(AdminPermissionNames.CatalogManage),
            [AdminAuditRoles.Support] = Set(AdminPermissionNames.SupportManage),
            [AdminAuditRoles.SuperAdmin] = Set(AdminPermissionNames.All),
            [AdminAuditRoles.LegacyAdmin] = Set(AdminPermissionNames.All)
        };

    public static bool IsAllowed(string role, string permission)
    {
        return !string.IsNullOrWhiteSpace(role) &&
               !string.IsNullOrWhiteSpace(permission) &&
               PermissionsByRole.TryGetValue(role.Trim(), out var permissions) &&
               permissions.Contains(permission.Trim());
    }

    public static IReadOnlySet<string> GetPermissions(string role)
    {
        return !string.IsNullOrWhiteSpace(role) &&
               PermissionsByRole.TryGetValue(role.Trim(), out var permissions)
            ? permissions
            : EmptyPermissions;
    }

    private static IReadOnlySet<string> Set(params string[] permissions)
    {
        return permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> Set(IEnumerable<string> permissions)
    {
        return permissions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
