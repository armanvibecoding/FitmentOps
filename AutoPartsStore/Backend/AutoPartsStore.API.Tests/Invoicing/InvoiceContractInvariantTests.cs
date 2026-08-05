using AutoPartsStore.API.Invoicing;
using Xunit;

namespace AutoPartsStore.API.Tests.Invoicing;

public sealed class InvoiceContractInvariantTests
{
    private static readonly InvoiceDocumentReference LegalReference = new(
        Guid.Parse("cc5411bc-5dce-4424-b301-8d59e842a610"),
        "PMH2026000000042",
        "provider-document-42");

    [Fact]
    public void PartyIdentifiersAndRecipientTypeAreValidatedWithoutForcingIndividualTckn()
    {
        var address = CreateAddress();
        var individual = InvoiceRecipientContext.Individual(
            "customer-42",
            "Ada Lovelace",
            address);
        var organization = InvoiceRecipientContext.Organization(
            "company-42",
            "Example Incorporated",
            InvoicePartyIdentifier.TaxNumber("1234567890"),
            "Kadikoy",
            address);

        Assert.Null(individual.Identifier);
        Assert.Equal(InvoiceRecipientType.Individual, individual.Type);
        Assert.Equal(InvoiceIdentifierKind.TurkishTaxNumber, organization.Identifier?.Kind);
        Assert.Throws<ArgumentException>(() => InvoicePartyIdentifier.TaxNumber("123"));
        Assert.Throws<ArgumentException>(() => InvoicePartyIdentifier.NationalId("not-digits!!"));
        Assert.Throws<ArgumentException>(() => InvoiceRecipientContext.Individual(
            "customer-42",
            "Ada Lovelace",
            address,
            InvoicePartyIdentifier.TaxNumber("1234567890")));
        Assert.Throws<ArgumentException>(() => InvoiceRecipientContext.Organization(
            "company-42",
            "Example Incorporated",
            InvoicePartyIdentifier.NationalId("12345678901"),
            "Kadikoy",
            address));
        Assert.Throws<ArgumentException>(() => InvoiceRecipientContext.Organization(
            "company-42",
            "Example Incorporated",
            InvoicePartyIdentifier.TaxNumber("1234567890"),
            "",
            address));
    }

    [Fact]
    public void InternetSaleFactoriesRequireAllTransactionFieldsAndCarrierForGoods()
    {
        var carrier = new InvoiceCarrierContext(
            "Carrier Incorporated",
            InvoicePartyIdentifier.TaxNumber("1234567890"));
        var sale = InvoiceInternetSaleSnapshot.Goods(
            new Uri("https://merchant.example"),
            "PAY_AT_DELIVERY",
            new DateOnly(2026, 8, 7),
            new DateOnly(2026, 8, 7),
            carrier);

        Assert.True(sale.IsInternetSale);
        Assert.Equal(InvoiceSupplyKind.Goods, sale.SupplyKind);
        Assert.Same(carrier, sale.Carrier);
        Assert.Throws<ArgumentNullException>(() => InvoiceInternetSaleSnapshot.Goods(
            new Uri("https://merchant.example"),
            "PAY_AT_DELIVERY",
            new DateOnly(2026, 8, 7),
            new DateOnly(2026, 8, 7),
            null!));
        Assert.Throws<ArgumentException>(() => InvoiceInternetSaleSnapshot.Service(
            new Uri("mailto:merchant@example.test"),
            "CARD",
            new DateOnly(2026, 8, 5),
            new DateOnly(2026, 8, 5)));
        Assert.Throws<ArgumentException>(() => InvoiceInternetSaleSnapshot.Service(
            new Uri("https://merchant.example"),
            "",
            new DateOnly(2026, 8, 5),
            new DateOnly(2026, 8, 5)));
    }

    [Fact]
    public void IssuedResultCannotBeNullUnidentifiedFailedOrRejected()
    {
        var issued = Snapshot(
            InvoiceDocumentStatus.Issued,
            InvoiceTransportStatus.ProviderAccepted,
            InvoiceApplicationStatus.GibAccepted);

        var result = InvoiceCreationResult.Issued(issued);

        Assert.Equal(InvoiceGatewayOutcome.Succeeded, result.Outcome);
        Assert.Equal(InvoiceGatewayErrorCode.None, result.ErrorCode);
        Assert.Same(issued, result.Invoice);
        Assert.Throws<ArgumentNullException>(() => InvoiceCreationResult.Issued(null!));
        Assert.Throws<ArgumentException>(() => InvoiceCreationResult.Issued(issued with
        {
            Reference = new InvoiceDocumentReference()
        }));
        Assert.Throws<ArgumentException>(() => InvoiceCreationResult.Issued(issued with
        {
            TransportStatus = InvoiceTransportStatus.ProviderRejected
        }));
        Assert.Throws<ArgumentException>(() => InvoiceCreationResult.Issued(issued with
        {
            ApplicationStatus = InvoiceApplicationStatus.GibRejected
        }));
        Assert.Throws<ArgumentException>(() => InvoiceCreationResult.Failed(InvoiceGatewayErrorCode.None));
    }

    [Fact]
    public void CancellationAndObjectionSuccessRequireTheirOwnAcceptedLegalState()
    {
        var cancelled = Snapshot(
            InvoiceDocumentStatus.Cancelled,
            InvoiceTransportStatus.ProviderAccepted,
            InvoiceApplicationStatus.GibAccepted) with
        {
            CancellationStatus = InvoiceCancellationStatus.Accepted
        };
        var objected = Snapshot(
            InvoiceDocumentStatus.Objected,
            InvoiceTransportStatus.ProviderAccepted,
            InvoiceApplicationStatus.GibAccepted) with
        {
            ObjectionStatus = InvoiceObjectionStatus.Accepted
        };

        Assert.Equal(InvoiceGatewayOutcome.Succeeded, InvoiceCancellationResult.Accepted(cancelled).Outcome);
        Assert.Equal(InvoiceGatewayOutcome.Succeeded, InvoiceObjectionResult.Accepted(objected).Outcome);
        Assert.Throws<ArgumentException>(() => InvoiceCancellationResult.Accepted(cancelled with
        {
            CancellationStatus = InvoiceCancellationStatus.PendingCounterparty
        }));
        Assert.Throws<ArgumentException>(() => InvoiceObjectionResult.Accepted(objected with
        {
            ObjectionStatus = InvoiceObjectionStatus.Rejected
        }));

        var pendingCancellation = InvoiceCancellationResult.Pending(cancelled with
        {
            DocumentStatus = InvoiceDocumentStatus.Issued,
            CancellationStatus = InvoiceCancellationStatus.Requested
        });
        var pendingObjection = InvoiceObjectionResult.Pending(objected with
        {
            DocumentStatus = InvoiceDocumentStatus.Issued,
            ObjectionStatus = InvoiceObjectionStatus.PendingCounterparty
        });

        Assert.Equal(InvoiceGatewayOutcome.Pending, pendingCancellation.Outcome);
        Assert.Equal(InvoiceGatewayOutcome.Pending, pendingObjection.Outcome);
    }

    [Fact]
    public void QueryCanReportARejectedDocumentWithoutCallingItIssued()
    {
        var rejected = Snapshot(
            InvoiceDocumentStatus.Failed,
            InvoiceTransportStatus.ProviderAccepted,
            InvoiceApplicationStatus.GibRejected);

        var query = InvoiceQueryResult.Found(rejected);

        Assert.Equal(InvoiceGatewayOutcome.Succeeded, query.Outcome);
        Assert.Equal(InvoiceDocumentStatus.Failed, query.Invoice?.DocumentStatus);
        Assert.Throws<ArgumentException>(() => InvoiceCreationResult.Issued(rejected));
    }

    [Fact]
    public void CancellationAndObjectionCommandsRejectIncompleteLegalEvidence()
    {
        var requestedAt = new DateTimeOffset(2026, 8, 5, 12, 30, 0, TimeSpan.Zero);
        var cancellation = new CancelInvoiceCommand(
            "ORDER-42",
            LegalReference,
            15_000,
            "TRY",
            "CUSTOMER_RETURN",
            requestedAt,
            "cancel-order-42");
        var objection = new SubmitInvoiceObjectionCommand(
            "ORDER-42",
            LegalReference,
            15_000,
            "TRY",
            "NOTARY-2026-42",
            new DateOnly(2026, 8, 5),
            InvoiceObjectionMethod.Notary,
            "AMOUNT_DISPUTED",
            requestedAt,
            "object-order-42");

        Assert.Equal(15_000, cancellation.PayableAmountMinor);
        Assert.Equal("NOTARY-2026-42", objection.ObjectionDocumentNumber);
        Assert.Throws<ArgumentException>(() => new CancelInvoiceCommand(
            "ORDER-42", new InvoiceDocumentReference(), 15_000, "TRY", "RETURN", requestedAt, "cancel-42"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CancelInvoiceCommand(
            "ORDER-42", LegalReference, -1, "TRY", "RETURN", requestedAt, "cancel-42"));
        Assert.Throws<ArgumentException>(() => new CancelInvoiceCommand(
            "ORDER-42", LegalReference, 15_000, "try", "RETURN", requestedAt, "cancel-42"));
        Assert.Throws<ArgumentException>(() => new CancelInvoiceCommand(
            "ORDER-42", LegalReference, 15_000, "TRY", "RETURN", default, "cancel-42"));
        Assert.Throws<ArgumentException>(() => new SubmitInvoiceObjectionCommand(
            "ORDER-42",
            LegalReference,
            15_000,
            "TRY",
            "",
            new DateOnly(2026, 8, 5),
            InvoiceObjectionMethod.Notary,
            "AMOUNT_DISPUTED",
            requestedAt,
            "object-order-42"));
        Assert.Throws<ArgumentException>(() => new SubmitInvoiceObjectionCommand(
            "ORDER-42",
            LegalReference,
            15_000,
            "TRY",
            "NOTARY-2026-42",
            default,
            InvoiceObjectionMethod.Notary,
            "AMOUNT_DISPUTED",
            requestedAt,
            "object-order-42"));
    }

    private static ProviderInvoiceSnapshot Snapshot(
        InvoiceDocumentStatus documentStatus,
        InvoiceTransportStatus transportStatus,
        InvoiceApplicationStatus applicationStatus) =>
        new(
            LegalReference,
            documentStatus,
            transportStatus,
            applicationStatus,
            PayableAmountMinor: 15_000,
            Currency: "TRY");

    private static InvoiceAddressContext CreateAddress() =>
        new(
            "Example address",
            "Kadikoy",
            "Istanbul",
            "Turkey",
            "34000");
}
