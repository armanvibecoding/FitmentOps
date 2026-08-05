using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Invoicing;

public enum InvoiceGatewayOutcome
{
    Succeeded,
    Pending,
    Failed
}

public enum InvoiceGatewayErrorCode
{
    None,
    ProviderNotConfigured,
    InvalidRequest,
    InvalidTaxCalculation,
    RecipientNotEligible,
    InvoiceNotFound,
    DuplicateRequest,
    NotCancellable,
    CancellationRejected,
    ObjectionRejected,
    Conflict,
    ProviderUnavailable,
    UnexpectedProviderResponse
}

public enum ElectronicInvoiceKind
{
    EInvoice,
    EArchiveInvoice
}

public enum InvoiceDocumentType
{
    Sale,
    Return,
    Withholding,
    Exemption
}

public enum InvoiceDocumentStatus
{
    Pending,
    Issued,
    Failed,
    Cancelled,
    Objected
}

/// <summary>
/// Transport acknowledgement is deliberately separate from GIB and recipient
/// application responses. Provider acceptance alone does not mean an invoice was issued.
/// </summary>
public enum InvoiceTransportStatus
{
    NotSubmitted,
    Queued,
    Submitted,
    ProviderAccepted,
    ProviderRejected
}

public enum InvoiceApplicationStatus
{
    Unknown,
    Processing,
    GibAccepted,
    GibRejected,
    DeliveredToRecipient,
    AcceptedByRecipient,
    RejectedByRecipient
}

public enum InvoiceCancellationStatus
{
    None,
    Requested,
    PendingCounterparty,
    Accepted,
    Rejected,
    Expired
}

public enum InvoiceObjectionStatus
{
    None,
    Requested,
    PendingCounterparty,
    Accepted,
    Rejected,
    Expired
}

public enum InvoiceIdentifierKind
{
    TurkishTaxNumber,
    TurkishNationalId
}

public enum InvoiceRecipientType
{
    Individual,
    Organization
}

public enum InvoiceObjectionMethod
{
    Notary,
    RegisteredMail,
    Telegram,
    RegisteredElectronicMail
}

public enum InvoiceSupplyKind
{
    Goods,
    Service
}

/// <summary>
/// A typed VKN/TCKN. The value is PII and is unavailable to general JSON and string logs.
/// </summary>
public sealed record InvoicePartyIdentifier
{
    private InvoicePartyIdentifier(InvoiceIdentifierKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public InvoiceIdentifierKind Kind { get; }

    [JsonIgnore]
    public string Value { get; }

    public static InvoicePartyIdentifier TaxNumber(string value) =>
        Create(InvoiceIdentifierKind.TurkishTaxNumber, value, 10, "VKN");

    public static InvoicePartyIdentifier NationalId(string value) =>
        Create(InvoiceIdentifierKind.TurkishNationalId, value, 11, "TCKN");

    public override string ToString() => $"{nameof(InvoicePartyIdentifier)} {{ Kind = {Kind}, Sensitive = true }}";

    private static InvoicePartyIdentifier Create(
        InvoiceIdentifierKind kind,
        string value,
        int expectedLength,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != expectedLength
            || value.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException(
                $"{displayName} must contain exactly {expectedLength} digits.",
                nameof(value));
        }

        return new InvoicePartyIdentifier(kind, value);
    }
}

/// <summary>
/// Exact tax calculation for one tax type, expressed in the currency's minor unit.
/// TaxPercent remains decimal because Turkish tax rates are not money values.
/// </summary>
public sealed record InvoiceTaxSnapshot(
    string TaxTypeCode,
    decimal TaxPercent,
    long TaxableAmountMinor,
    long TaxAmountMinor,
    string? ExemptionReasonCode = null);

/// <summary>
/// Immutable merchant-side line calculation. It contains no card data or customer PII.
/// </summary>
public sealed record InvoiceLineSnapshot(
    string LineId,
    string ProductReference,
    string Description,
    decimal Quantity,
    string UnitCode,
    long UnitPriceMinor,
    long LineExtensionAmountMinor,
    long DiscountAmountMinor,
    ImmutableArray<InvoiceTaxSnapshot> Taxes);

/// <summary>
/// Carrier identity required for internet sales of goods. Title and identifier are excluded
/// from general serialization because a carrier may be an individual.
/// </summary>
public sealed record InvoiceCarrierContext
{
    public InvoiceCarrierContext(string title, InvoicePartyIdentifier identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(identifier);

        Title = title;
        Identifier = identifier;
    }

    [JsonIgnore]
    public string Title { get; }

    [JsonIgnore]
    public InvoicePartyIdentifier Identifier { get; }

    public override string ToString() => $"{nameof(InvoiceCarrierContext)} {{ Sensitive = true }}";
}

/// <summary>
/// Transaction-specific fields required by the 509 General Communique for an internet sale.
/// Factories enforce that goods have a carrier while services do not invent one.
/// </summary>
public sealed record InvoiceInternetSaleSnapshot
{
    private InvoiceInternetSaleSnapshot(
        bool isInternetSale,
        InvoiceSupplyKind? supplyKind,
        Uri? salesWebAddress,
        string? paymentMethod,
        DateOnly? paymentDate,
        DateOnly? deliveryOrServiceDate,
        InvoiceCarrierContext? carrier)
    {
        IsInternetSale = isInternetSale;
        SupplyKind = supplyKind;
        SalesWebAddress = salesWebAddress;
        PaymentMethod = paymentMethod;
        PaymentDate = paymentDate;
        DeliveryOrServiceDate = deliveryOrServiceDate;
        Carrier = carrier;
    }

    public bool IsInternetSale { get; }
    public InvoiceSupplyKind? SupplyKind { get; }
    public Uri? SalesWebAddress { get; }
    public string? PaymentMethod { get; }
    public DateOnly? PaymentDate { get; }
    public DateOnly? DeliveryOrServiceDate { get; }

    [JsonIgnore]
    public InvoiceCarrierContext? Carrier { get; }

    public static InvoiceInternetSaleSnapshot NotInternetSale() =>
        new(false, null, null, null, null, null, null);

    public static InvoiceInternetSaleSnapshot Goods(
        Uri salesWebAddress,
        string paymentMethod,
        DateOnly paymentDate,
        DateOnly deliveryDate,
        InvoiceCarrierContext carrier)
    {
        ValidateInternetSale(salesWebAddress, paymentMethod);
        ArgumentNullException.ThrowIfNull(carrier);

        return new(
            true,
            InvoiceSupplyKind.Goods,
            salesWebAddress,
            paymentMethod,
            paymentDate,
            deliveryDate,
            carrier);
    }

    public static InvoiceInternetSaleSnapshot Service(
        Uri salesWebAddress,
        string paymentMethod,
        DateOnly paymentDate,
        DateOnly serviceDate)
    {
        ValidateInternetSale(salesWebAddress, paymentMethod);

        return new(
            true,
            InvoiceSupplyKind.Service,
            salesWebAddress,
            paymentMethod,
            paymentDate,
            serviceDate,
            null);
    }

    private static void ValidateInternetSale(Uri salesWebAddress, string paymentMethod)
    {
        ArgumentNullException.ThrowIfNull(salesWebAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethod);

        if (!salesWebAddress.IsAbsoluteUri
            || (salesWebAddress.Scheme != Uri.UriSchemeHttps
                && salesWebAddress.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "The sales web address must be an absolute HTTP or HTTPS URI.",
                nameof(salesWebAddress));
        }
    }
}

/// <summary>
/// Values that an adapter result must reconcile against before an invoice is treated
/// as issued. All monetary values use the currency's minor unit.
/// </summary>
public sealed record InvoiceRequestSnapshot(
    int OrderId,
    string OrderNumber,
    ElectronicInvoiceKind Kind,
    InvoiceDocumentType DocumentType,
    DateOnly IssueDate,
    string Currency,
    long LineExtensionAmountMinor,
    long AllowanceTotalAmountMinor,
    long ChargeTotalAmountMinor,
    long TaxExclusiveAmountMinor,
    long TaxAmountMinor,
    long PayableAmountMinor,
    ImmutableArray<InvoiceLineSnapshot> Lines,
    InvoiceInternetSaleSnapshot InternetSale);

/// <summary>
/// Customer address exists only for adapter mapping. Its fields are deliberately
/// omitted from general JSON serialization and redacted from string output.
/// </summary>
public sealed record InvoiceAddressContext(
    [property: JsonIgnore] string AddressLine,
    [property: JsonIgnore] string District,
    [property: JsonIgnore] string City,
    [property: JsonIgnore] string Country,
    [property: JsonIgnore] string? PostalCode = null)
{
    public override string ToString() => $"{nameof(InvoiceAddressContext)} {{ Sensitive = true }}";
}

/// <summary>
/// Provider-required recipient data. Individuals are not forced to provide a TCKN;
/// organizations must provide an explicitly typed VKN and tax office.
/// </summary>
public sealed record InvoiceRecipientContext
{
    private InvoiceRecipientContext(
        InvoiceRecipientType type,
        string reference,
        string name,
        InvoicePartyIdentifier? identifier,
        string? taxOffice,
        string? email,
        string? phone,
        InvoiceAddressContext billingAddress)
    {
        Type = type;
        Reference = reference;
        Name = name;
        Identifier = identifier;
        TaxOffice = taxOffice;
        Email = email;
        Phone = phone;
        BillingAddress = billingAddress;
    }

    public InvoiceRecipientType Type { get; }

    [JsonIgnore]
    public string Reference { get; }

    [JsonIgnore]
    public string Name { get; }

    [JsonIgnore]
    public InvoicePartyIdentifier? Identifier { get; }

    [JsonIgnore]
    public string? TaxOffice { get; }

    [JsonIgnore]
    public string? Email { get; }

    [JsonIgnore]
    public string? Phone { get; }

    [JsonIgnore]
    public InvoiceAddressContext BillingAddress { get; }

    public static InvoiceRecipientContext Individual(
        string reference,
        string name,
        InvoiceAddressContext billingAddress,
        InvoicePartyIdentifier? nationalId = null,
        string? taxOffice = null,
        string? email = null,
        string? phone = null)
    {
        ValidateCommon(reference, name, billingAddress);
        if (nationalId is not null && nationalId.Kind != InvoiceIdentifierKind.TurkishNationalId)
        {
            throw new ArgumentException("An individual identifier must be a TCKN.", nameof(nationalId));
        }

        return new(
            InvoiceRecipientType.Individual,
            reference,
            name,
            nationalId,
            taxOffice,
            email,
            phone,
            billingAddress);
    }

    public static InvoiceRecipientContext Organization(
        string reference,
        string name,
        InvoicePartyIdentifier taxNumber,
        string taxOffice,
        InvoiceAddressContext billingAddress,
        string? email = null,
        string? phone = null)
    {
        ValidateCommon(reference, name, billingAddress);
        ArgumentNullException.ThrowIfNull(taxNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(taxOffice);

        if (taxNumber.Kind != InvoiceIdentifierKind.TurkishTaxNumber)
        {
            throw new ArgumentException("An organization identifier must be a VKN.", nameof(taxNumber));
        }

        return new(
            InvoiceRecipientType.Organization,
            reference,
            name,
            taxNumber,
            taxOffice,
            email,
            phone,
            billingAddress);
    }

    public override string ToString() => $"{nameof(InvoiceRecipientContext)} {{ Type = {Type}, Sensitive = true }}";

    private static void ValidateCommon(
        string reference,
        string name,
        InvoiceAddressContext billingAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(billingAddress);
    }
}

public sealed record CreateInvoiceCommand
{
    public CreateInvoiceCommand(
        InvoiceRequestSnapshot expected,
        InvoiceRecipientContext recipient,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        Expected = expected;
        Recipient = recipient;
        IdempotencyKey = idempotencyKey;
    }

    public InvoiceRequestSnapshot Expected { get; }

    [JsonIgnore]
    public InvoiceRecipientContext Recipient { get; }

    [JsonIgnore]
    public string IdempotencyKey { get; }

    public override string ToString() => $"{nameof(CreateInvoiceCommand)} {{ Sensitive = true }}";
}

/// <summary>
/// Provider and legal document identifiers. ExternalUuid represents the ETTN/UUID.
/// </summary>
public sealed record InvoiceDocumentReference(
    Guid? ExternalUuid = null,
    string? InvoiceNumber = null,
    string? ProviderDocumentId = null);

public sealed record QueryInvoiceCommand(
    string OrderNumber,
    InvoiceDocumentReference Reference);

public sealed record CancelInvoiceCommand
{
    public CancelInvoiceCommand(
        string orderNumber,
        InvoiceDocumentReference reference,
        long payableAmountMinor,
        string currency,
        string reasonCode,
        DateTimeOffset requestedAtUtc,
        string idempotencyKey)
    {
        InvoiceCommandInvariants.ValidateLegalRequest(
            orderNumber,
            reference,
            payableAmountMinor,
            currency,
            reasonCode,
            requestedAtUtc,
            idempotencyKey);

        OrderNumber = orderNumber;
        Reference = reference;
        PayableAmountMinor = payableAmountMinor;
        Currency = currency;
        ReasonCode = reasonCode;
        RequestedAtUtc = requestedAtUtc;
        IdempotencyKey = idempotencyKey;
    }

    public string OrderNumber { get; }
    public InvoiceDocumentReference Reference { get; }
    public long PayableAmountMinor { get; }
    public string Currency { get; }
    public string ReasonCode { get; }
    public DateTimeOffset RequestedAtUtc { get; }

    [JsonIgnore]
    public string IdempotencyKey { get; }

    public override string ToString() => $"{nameof(CancelInvoiceCommand)} {{ Sensitive = true }}";
}

public sealed record SubmitInvoiceObjectionCommand
{
    public SubmitInvoiceObjectionCommand(
        string orderNumber,
        InvoiceDocumentReference reference,
        long payableAmountMinor,
        string currency,
        string objectionDocumentNumber,
        DateOnly objectionDocumentDate,
        InvoiceObjectionMethod method,
        string reasonCode,
        DateTimeOffset requestedAtUtc,
        string idempotencyKey)
    {
        InvoiceCommandInvariants.ValidateLegalRequest(
            orderNumber,
            reference,
            payableAmountMinor,
            currency,
            reasonCode,
            requestedAtUtc,
            idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectionDocumentNumber);
        if (objectionDocumentDate == default)
        {
            throw new ArgumentException("Objection document date is required.", nameof(objectionDocumentDate));
        }

        OrderNumber = orderNumber;
        Reference = reference;
        PayableAmountMinor = payableAmountMinor;
        Currency = currency;
        ObjectionDocumentNumber = objectionDocumentNumber;
        ObjectionDocumentDate = objectionDocumentDate;
        Method = method;
        ReasonCode = reasonCode;
        RequestedAtUtc = requestedAtUtc;
        IdempotencyKey = idempotencyKey;
    }

    public string OrderNumber { get; }
    public InvoiceDocumentReference Reference { get; }
    public long PayableAmountMinor { get; }
    public string Currency { get; }
    public string ObjectionDocumentNumber { get; }
    public DateOnly ObjectionDocumentDate { get; }
    public InvoiceObjectionMethod Method { get; }
    public string ReasonCode { get; }
    public DateTimeOffset RequestedAtUtc { get; }

    [JsonIgnore]
    public string IdempotencyKey { get; }

    public override string ToString() => $"{nameof(SubmitInvoiceObjectionCommand)} {{ Sensitive = true }}";
}

/// <summary>
/// Provider values after authenticity verification. Transport, GIB/recipient application,
/// cancellation and objection states remain independent to prevent false legal success.
/// </summary>
public sealed record ProviderInvoiceSnapshot(
    InvoiceDocumentReference Reference,
    InvoiceDocumentStatus DocumentStatus,
    InvoiceTransportStatus TransportStatus,
    InvoiceApplicationStatus ApplicationStatus,
    InvoiceCancellationStatus CancellationStatus = InvoiceCancellationStatus.None,
    InvoiceObjectionStatus ObjectionStatus = InvoiceObjectionStatus.None,
    long? TaxExclusiveAmountMinor = null,
    long? TaxAmountMinor = null,
    long? PayableAmountMinor = null,
    string? Currency = null,
    DateTimeOffset? SubmittedAtUtc = null);

public sealed record InvoiceCreationResult
{
    private InvoiceCreationResult(
        InvoiceGatewayOutcome outcome,
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage,
        ProviderInvoiceSnapshot? invoice)
    {
        Outcome = outcome;
        ErrorCode = errorCode;
        UserSafeMessage = userSafeMessage;
        Invoice = invoice;
    }

    public InvoiceGatewayOutcome Outcome { get; }
    public InvoiceGatewayErrorCode ErrorCode { get; }
    public string? UserSafeMessage { get; }
    public ProviderInvoiceSnapshot? Invoice { get; }

    public static InvoiceCreationResult Issued(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureIssued(invoice);
        return new(InvoiceGatewayOutcome.Succeeded, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceCreationResult Pending(
        ProviderInvoiceSnapshot? invoice = null,
        string? userSafeMessage = null) =>
        new(InvoiceGatewayOutcome.Pending, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);

    public static InvoiceCreationResult Failed(
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage = null,
        ProviderInvoiceSnapshot? invoice = null)
    {
        InvoiceResultInvariants.EnsureFailure(errorCode);
        return new(InvoiceGatewayOutcome.Failed, errorCode, userSafeMessage, invoice);
    }
}

public sealed record InvoiceQueryResult
{
    private InvoiceQueryResult(
        InvoiceGatewayOutcome outcome,
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage,
        ProviderInvoiceSnapshot? invoice)
    {
        Outcome = outcome;
        ErrorCode = errorCode;
        UserSafeMessage = userSafeMessage;
        Invoice = invoice;
    }

    public InvoiceGatewayOutcome Outcome { get; }
    public InvoiceGatewayErrorCode ErrorCode { get; }
    public string? UserSafeMessage { get; }
    public ProviderInvoiceSnapshot? Invoice { get; }

    /// <summary>A found rejected invoice remains a valid query result, not an issued result.</summary>
    public static InvoiceQueryResult Found(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new(InvoiceGatewayOutcome.Succeeded, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceQueryResult Failed(
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureFailure(errorCode);
        return new(InvoiceGatewayOutcome.Failed, errorCode, userSafeMessage, null);
    }
}

public sealed record InvoiceCancellationResult
{
    private InvoiceCancellationResult(
        InvoiceGatewayOutcome outcome,
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage,
        ProviderInvoiceSnapshot? invoice)
    {
        Outcome = outcome;
        ErrorCode = errorCode;
        UserSafeMessage = userSafeMessage;
        Invoice = invoice;
    }

    public InvoiceGatewayOutcome Outcome { get; }
    public InvoiceGatewayErrorCode ErrorCode { get; }
    public string? UserSafeMessage { get; }
    public ProviderInvoiceSnapshot? Invoice { get; }

    public static InvoiceCancellationResult Accepted(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureCancellationAccepted(invoice);
        return new(InvoiceGatewayOutcome.Succeeded, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceCancellationResult Pending(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureCancellationPending(invoice);
        return new(InvoiceGatewayOutcome.Pending, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceCancellationResult Failed(
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage = null,
        ProviderInvoiceSnapshot? invoice = null)
    {
        InvoiceResultInvariants.EnsureFailure(errorCode);
        return new(InvoiceGatewayOutcome.Failed, errorCode, userSafeMessage, invoice);
    }
}

public sealed record InvoiceObjectionResult
{
    private InvoiceObjectionResult(
        InvoiceGatewayOutcome outcome,
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage,
        ProviderInvoiceSnapshot? invoice)
    {
        Outcome = outcome;
        ErrorCode = errorCode;
        UserSafeMessage = userSafeMessage;
        Invoice = invoice;
    }

    public InvoiceGatewayOutcome Outcome { get; }
    public InvoiceGatewayErrorCode ErrorCode { get; }
    public string? UserSafeMessage { get; }
    public ProviderInvoiceSnapshot? Invoice { get; }

    public static InvoiceObjectionResult Accepted(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureObjectionAccepted(invoice);
        return new(InvoiceGatewayOutcome.Succeeded, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceObjectionResult Pending(
        ProviderInvoiceSnapshot invoice,
        string? userSafeMessage = null)
    {
        InvoiceResultInvariants.EnsureObjectionPending(invoice);
        return new(InvoiceGatewayOutcome.Pending, InvoiceGatewayErrorCode.None, userSafeMessage, invoice);
    }

    public static InvoiceObjectionResult Failed(
        InvoiceGatewayErrorCode errorCode,
        string? userSafeMessage = null,
        ProviderInvoiceSnapshot? invoice = null)
    {
        InvoiceResultInvariants.EnsureFailure(errorCode);
        return new(InvoiceGatewayOutcome.Failed, errorCode, userSafeMessage, invoice);
    }
}

internal static class InvoiceResultInvariants
{
    public static void EnsureIssued(ProviderInvoiceSnapshot invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        EnsureLegalReference(invoice.Reference);
        EnsureNoRejection(invoice);

        if (invoice.DocumentStatus != InvoiceDocumentStatus.Issued)
        {
            throw new ArgumentException(
                "An issued result requires an issued document with no provider, GIB, or recipient rejection.",
                nameof(invoice));
        }
    }

    public static void EnsureCancellationAccepted(ProviderInvoiceSnapshot invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        EnsureLegalReference(invoice.Reference);
        EnsureNoRejection(invoice);

        if (invoice.DocumentStatus != InvoiceDocumentStatus.Cancelled
            || invoice.CancellationStatus != InvoiceCancellationStatus.Accepted)
        {
            throw new ArgumentException(
                "An accepted cancellation requires a cancelled document and accepted cancellation state.",
                nameof(invoice));
        }
    }

    public static void EnsureCancellationPending(ProviderInvoiceSnapshot invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.CancellationStatus is not InvoiceCancellationStatus.Requested
            and not InvoiceCancellationStatus.PendingCounterparty)
        {
            throw new ArgumentException(
                "A pending cancellation requires a requested or counterparty-pending state.",
                nameof(invoice));
        }
    }

    public static void EnsureObjectionAccepted(ProviderInvoiceSnapshot invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        EnsureLegalReference(invoice.Reference);
        EnsureNoRejection(invoice);

        if (invoice.DocumentStatus != InvoiceDocumentStatus.Objected
            || invoice.ObjectionStatus != InvoiceObjectionStatus.Accepted)
        {
            throw new ArgumentException(
                "An accepted objection requires an objected document and accepted objection state.",
                nameof(invoice));
        }
    }

    public static void EnsureObjectionPending(ProviderInvoiceSnapshot invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.ObjectionStatus is not InvoiceObjectionStatus.Requested
            and not InvoiceObjectionStatus.PendingCounterparty)
        {
            throw new ArgumentException(
                "A pending objection requires a requested or counterparty-pending state.",
                nameof(invoice));
        }
    }

    public static void EnsureFailure(InvoiceGatewayErrorCode errorCode)
    {
        if (errorCode == InvoiceGatewayErrorCode.None)
        {
            throw new ArgumentException("A failed result requires a non-None error code.", nameof(errorCode));
        }
    }

    private static void EnsureLegalReference(InvoiceDocumentReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.ExternalUuid is null || string.IsNullOrWhiteSpace(reference.InvoiceNumber))
        {
            throw new ArgumentException(
                "A legally completed invoice result requires both ETTN/UUID and invoice number.",
                nameof(reference));
        }
    }

    private static void EnsureNoRejection(ProviderInvoiceSnapshot invoice)
    {
        if (invoice.TransportStatus == InvoiceTransportStatus.ProviderRejected
            || invoice.ApplicationStatus is InvoiceApplicationStatus.GibRejected
                or InvoiceApplicationStatus.RejectedByRecipient)
        {
            throw new ArgumentException(
                "A successful legal result cannot contain a provider, GIB, or recipient rejection.",
                nameof(invoice));
        }
    }
}

internal static class InvoiceCommandInvariants
{
    public static void ValidateLegalRequest(
        string orderNumber,
        InvoiceDocumentReference reference,
        long payableAmountMinor,
        string currency,
        string reasonCode,
        DateTimeOffset requestedAtUtc,
        string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (reference.ExternalUuid is null || string.IsNullOrWhiteSpace(reference.InvoiceNumber))
        {
            throw new ArgumentException(
                "A cancellation or objection request requires both ETTN/UUID and invoice number.",
                nameof(reference));
        }

        if (payableAmountMinor < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payableAmountMinor),
                "Payable amount cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(character => !char.IsAsciiLetterUpper(character)))
        {
            throw new ArgumentException("Currency must be a three-letter uppercase code.", nameof(currency));
        }

        if (requestedAtUtc == default)
        {
            throw new ArgumentException("Request timestamp is required.", nameof(requestedAtUtc));
        }
    }
}
