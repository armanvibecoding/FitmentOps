using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Payments;

public enum PaymentGatewayOutcome
{
    Succeeded,
    Pending,
    Failed
}

public enum PaymentGatewayErrorCode
{
    None,
    ProviderNotConfigured,
    InvalidRequest,
    InvalidSignature,
    PaymentNotFound,
    AmountMismatch,
    CurrencyMismatch,
    OrderMismatch,
    Declined,
    Conflict,
    ProviderUnavailable,
    UnexpectedProviderResponse
}

public enum GatewayPaymentStatus
{
    Unknown,
    RequiresCustomerAction,
    Pending,
    Authorized,
    Paid,
    Failed,
    Cancelled,
    PartiallyRefunded,
    Refunded
}

/// <summary>
/// An immutable checkout line expressed in the currency's minor unit. Card data
/// is intentionally absent because checkout is hosted by the payment provider.
/// </summary>
public sealed record PaymentBasketItemSnapshot(
    string LineId,
    string ProductReference,
    string DisplayName,
    int Quantity,
    long UnitPriceMinor,
    long LineTotalMinor);

/// <summary>
/// The merchant-side values a provider response must reconcile against before a
/// local payment can be marked as paid.
/// </summary>
public sealed record PaymentRequestSnapshot(
    int PaymentId,
    string ConversationId,
    string OrderNumber,
    long AmountMinor,
    string Currency,
    ImmutableArray<PaymentBasketItemSnapshot> BasketItems);

/// <summary>
/// Provider-required customer data that may exist only for the lifetime of an
/// initialization request. Every field is excluded from general JSON
/// serialization so commands cannot accidentally become a PII persistence or
/// logging format. A provider adapter must map the fields explicitly.
/// </summary>
public sealed record PaymentBuyerContext(
    [property: JsonIgnore] string Reference,
    [property: JsonIgnore] string FirstName,
    [property: JsonIgnore] string LastName,
    [property: JsonIgnore] string Email,
    [property: JsonIgnore] string Phone,
    [property: JsonIgnore] string IdentityNumber,
    [property: JsonIgnore] string IpAddress)
{
    public override string ToString() => $"{nameof(PaymentBuyerContext)} {{ Sensitive = true }}";
}

public sealed record PaymentAddressContext(
    [property: JsonIgnore] string ContactName,
    [property: JsonIgnore] string AddressLine,
    [property: JsonIgnore] string City,
    [property: JsonIgnore] string Country,
    [property: JsonIgnore] string? PostalCode = null)
{
    public override string ToString() => $"{nameof(PaymentAddressContext)} {{ Sensitive = true }}";
}

public sealed record InitializePaymentCommand(
    PaymentRequestSnapshot Expected,
    Uri CallbackUri,
    Uri ReturnUri,
    [property: JsonIgnore] string IdempotencyKey,
    [property: JsonIgnore] PaymentBuyerContext? Buyer = null,
    [property: JsonIgnore] PaymentAddressContext? BillingAddress = null,
    [property: JsonIgnore] PaymentAddressContext? ShippingAddress = null)
{
    public override string ToString() => $"{nameof(InitializePaymentCommand)} {{ Sensitive = true }}";
}

public sealed record ConfirmPaymentCommand(
    PaymentRequestSnapshot Expected,
    [property: JsonIgnore] string HostedPaymentToken)
{
    public override string ToString() => $"{nameof(ConfirmPaymentCommand)} {{ Sensitive = true }}";
}

public sealed record RetrievePaymentCommand(
    PaymentRequestSnapshot Expected,
    string? ProviderPaymentId = null,
    [property: JsonIgnore] string? HostedPaymentToken = null)
{
    public override string ToString() => $"{nameof(RetrievePaymentCommand)} {{ Sensitive = true }}";
}

public sealed record VerifyPaymentWebhookCommand(
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBody,
    [property: JsonIgnore] ImmutableDictionary<string, ImmutableArray<string>> Headers)
{
    public override string ToString() => $"{nameof(VerifyPaymentWebhookCommand)} {{ Sensitive = true }}";
}

public sealed record RefundPaymentCommand(
    PaymentRequestSnapshot Expected,
    string ProviderPaymentId,
    long RefundAmountMinor,
    string Currency,
    [property: JsonIgnore] string IdempotencyKey,
    string ReasonCode)
{
    public override string ToString() => $"{nameof(RefundPaymentCommand)} {{ Sensitive = true }}";
}

public sealed record InquirePaymentCommand(
    PaymentRequestSnapshot Expected,
    string ProviderPaymentId);

/// <summary>
/// Provider values returned after signature/authenticity verification. Callers
/// must compare these values with their PaymentRequestSnapshot.
/// </summary>
public sealed record ProviderPaymentSnapshot(
    string? ProviderPaymentId,
    string? OrderNumber,
    long? AmountMinor,
    string? Currency,
    GatewayPaymentStatus Status);

public sealed record PaymentInitializationResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    Uri? HostedPaymentPageUri = null,
    [property: JsonIgnore] string? HostedPaymentToken = null,
    string? ProviderPaymentId = null,
    DateTimeOffset? ExpiresAtUtc = null)
{
    public override string ToString() => $"{nameof(PaymentInitializationResult)} {{ Sensitive = true }}";
}

public sealed record PaymentConfirmationResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    ProviderPaymentSnapshot? Payment = null);

public sealed record PaymentRetrievalResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    ProviderPaymentSnapshot? Payment = null);

public sealed record PaymentWebhookVerificationResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    string? ProviderEventId = null,
    string? EventType = null,
    ProviderPaymentSnapshot? Payment = null);

public sealed record PaymentRefundResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    string? ProviderRefundId = null,
    string? ProviderPaymentId = null,
    long? RefundedAmountMinor = null,
    string? Currency = null,
    GatewayPaymentStatus PaymentStatus = GatewayPaymentStatus.Unknown);

public sealed record PaymentInquiryResult(
    PaymentGatewayOutcome Outcome,
    PaymentGatewayErrorCode ErrorCode,
    string? UserSafeMessage = null,
    ProviderPaymentSnapshot? Payment = null);
