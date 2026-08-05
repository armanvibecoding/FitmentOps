using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoPartsStore.API.Invoicing;
using Xunit;

namespace AutoPartsStore.API.Tests.Invoicing;

public sealed class DisabledInvoiceGatewayTests
{
    private readonly DisabledInvoiceGateway _gateway = new();

    [Fact]
    public async Task AllOperationsFailClosedWithoutFabricatingAnInvoice()
    {
        var expected = CreateExpectedSnapshot();
        var recipient = CreateRecipient();
        var reference = new InvoiceDocumentReference(
            Guid.Parse("cc5411bc-5dce-4424-b301-8d59e842a610"),
            "PMH2026000000042",
            "provider-document-42");

        var creation = await _gateway.CreateAsync(new CreateInvoiceCommand(
            expected,
            recipient,
            "invoice-create-order-42"));
        var query = await _gateway.QueryAsync(new QueryInvoiceCommand(
            expected.OrderNumber,
            reference));
        var cancellation = await _gateway.CancelAsync(new CancelInvoiceCommand(
            expected.OrderNumber,
            reference,
            expected.PayableAmountMinor,
            expected.Currency,
            "CUSTOMER_RETURN",
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            "invoice-cancel-order-42"));
        var objection = await _gateway.SubmitObjectionAsync(new SubmitInvoiceObjectionCommand(
            expected.OrderNumber,
            reference,
            expected.PayableAmountMinor,
            expected.Currency,
            "KEP-2026-42",
            new DateOnly(2026, 8, 5),
            InvoiceObjectionMethod.RegisteredElectronicMail,
            "COMMERCIAL_OBJECTION",
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            "invoice-objection-order-42"));

        AssertFailure(creation.Outcome, creation.ErrorCode, creation.UserSafeMessage);
        AssertFailure(query.Outcome, query.ErrorCode, query.UserSafeMessage);
        AssertFailure(cancellation.Outcome, cancellation.ErrorCode, cancellation.UserSafeMessage);
        AssertFailure(objection.Outcome, objection.ErrorCode, objection.UserSafeMessage);
        Assert.Null(creation.Invoice);
        Assert.Null(query.Invoice);
        Assert.Null(cancellation.Invoice);
        Assert.Null(objection.Invoice);
        Assert.False(_gateway.IsEnabled);
        Assert.Equal("Disabled", _gateway.ProviderName);
    }

    [Fact]
    public void SnapshotCarriesExactOrderLineAndTaxCalculations()
    {
        var snapshot = CreateExpectedSnapshot();

        Assert.Equal(42, snapshot.OrderId);
        Assert.Equal("ORDER-42", snapshot.OrderNumber);
        Assert.Equal(ElectronicInvoiceKind.EArchiveInvoice, snapshot.Kind);
        Assert.Equal(InvoiceDocumentType.Sale, snapshot.DocumentType);
        Assert.Equal(new DateOnly(2026, 8, 5), snapshot.IssueDate);
        Assert.Equal("TRY", snapshot.Currency);
        Assert.Equal(12_500, snapshot.LineExtensionAmountMinor);
        Assert.Equal(0, snapshot.AllowanceTotalAmountMinor);
        Assert.Equal(0, snapshot.ChargeTotalAmountMinor);
        Assert.Equal(12_500, snapshot.TaxExclusiveAmountMinor);
        Assert.Equal(2_500, snapshot.TaxAmountMinor);
        Assert.Equal(15_000, snapshot.PayableAmountMinor);
        Assert.Equal(2, snapshot.Lines.Length);
        Assert.Equal(12_500, snapshot.Lines.Sum(line => line.LineExtensionAmountMinor));
        Assert.Equal(2_500, snapshot.Lines.SelectMany(line => line.Taxes).Sum(tax => tax.TaxAmountMinor));
        Assert.True(snapshot.InternetSale.IsInternetSale);
        Assert.Equal(InvoiceSupplyKind.Goods, snapshot.InternetSale.SupplyKind);
        Assert.Equal(new Uri("https://merchant.example"), snapshot.InternetSale.SalesWebAddress);
        Assert.Equal("PAY_AT_DELIVERY", snapshot.InternetSale.PaymentMethod);
        Assert.Equal(new DateOnly(2026, 8, 7), snapshot.InternetSale.PaymentDate);
        Assert.Equal(new DateOnly(2026, 8, 7), snapshot.InternetSale.DeliveryOrServiceDate);
        Assert.Equal("Carrier Incorporated", snapshot.InternetSale.Carrier?.Title);
        Assert.Equal(InvoiceIdentifierKind.TurkishTaxNumber, snapshot.InternetSale.Carrier?.Identifier.Kind);
        Assert.All(snapshot.Lines.SelectMany(line => line.Taxes), tax =>
        {
            Assert.Equal("0015", tax.TaxTypeCode);
            Assert.Equal(20m, tax.TaxPercent);
        });
    }

    [Fact]
    public void CommandsCarryExternalEttnAndIdempotencyWithoutExposingSensitiveData()
    {
        var sensitiveIdentifier = "11111111111";
        var sensitiveName = "Ada Lovelace";
        var sensitiveEmail = "ada@example.test";
        var sensitiveAddress = "Sensitive billing address";
        var recipient = InvoiceRecipientContext.Individual(
            "customer-42",
            sensitiveName,
            new InvoiceAddressContext(
                sensitiveAddress,
                "Kadikoy",
                "Istanbul",
                "Turkey",
                "34000"),
            InvoicePartyIdentifier.NationalId(sensitiveIdentifier),
            "Sensitive tax office",
            sensitiveEmail,
            "+905550000000");
        var idempotencyKey = "sensitive-create-idempotency";
        var command = new CreateInvoiceCommand(CreateExpectedSnapshot(), recipient, idempotencyKey);
        var externalUuid = Guid.Parse("cc5411bc-5dce-4424-b301-8d59e842a610");
        var reference = new InvoiceDocumentReference(externalUuid, "PMH2026000000042", "document-42");
        var cancellation = new CancelInvoiceCommand(
            "ORDER-42",
            reference,
            15_000,
            "TRY",
            "CUSTOMER_RETURN",
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            "sensitive-cancel-idempotency");
        var objection = new SubmitInvoiceObjectionCommand(
            "ORDER-42",
            reference,
            15_000,
            "TRY",
            "KEP-2026-42",
            new DateOnly(2026, 8, 5),
            InvoiceObjectionMethod.RegisteredElectronicMail,
            "COMMERCIAL_OBJECTION",
            new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            "sensitive-objection-idempotency");

        var commandJson = JsonSerializer.Serialize(command);
        var recipientJson = JsonSerializer.Serialize(recipient);
        var cancellationJson = JsonSerializer.Serialize(cancellation);
        var objectionJson = JsonSerializer.Serialize(objection);
        var internetSaleJson = JsonSerializer.Serialize(CreateExpectedSnapshot().InternetSale);

        Assert.Equal(externalUuid, reference.ExternalUuid);
        Assert.DoesNotContain(sensitiveIdentifier, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveName, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveEmail, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveAddress, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyKey, commandJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-cancel-idempotency", cancellationJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-objection-idempotency", objectionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Carrier Incorporated", internetSaleJson, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", internetSaleJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveIdentifier, recipientJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveName, recipient.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveAddress, recipient.BillingAddress.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyKey, command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-cancel-idempotency", cancellation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-objection-idempotency", objection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveContextPropertiesAreJsonIgnoredAndContractsContainNoSigningOrPaymentMaterial()
    {
        var sensitiveProperties = new[]
        {
            typeof(InvoiceRecipientContext).GetProperties()
                .Where(property => property.Name != nameof(InvoiceRecipientContext.Type)),
            typeof(InvoiceAddressContext).GetProperties().AsEnumerable(),
            new[]
            {
                typeof(InvoicePartyIdentifier).GetProperty(nameof(InvoicePartyIdentifier.Value))!,
                typeof(InvoiceCarrierContext).GetProperty(nameof(InvoiceCarrierContext.Title))!,
                typeof(InvoiceCarrierContext).GetProperty(nameof(InvoiceCarrierContext.Identifier))!,
                typeof(InvoiceInternetSaleSnapshot).GetProperty(nameof(InvoiceInternetSaleSnapshot.Carrier))!,
                typeof(CreateInvoiceCommand).GetProperty(nameof(CreateInvoiceCommand.Recipient))!,
                typeof(CreateInvoiceCommand).GetProperty(nameof(CreateInvoiceCommand.IdempotencyKey))!,
                typeof(CancelInvoiceCommand).GetProperty(nameof(CancelInvoiceCommand.IdempotencyKey))!,
                typeof(SubmitInvoiceObjectionCommand).GetProperty(nameof(SubmitInvoiceObjectionCommand.IdempotencyKey))!
            }.AsEnumerable()
        }.SelectMany(properties => properties);
        var unprotectedSensitiveProperties = sensitiveProperties
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
            .ToArray();

        var forbiddenTerms = new[]
        {
            "CardNumber", "Pan", "Cvv", "Cvc", "Expiry", "PaymentToken",
            "ApiKey", "SecretKey", "PrivateKey", "Credential", "Signature", "Xml"
        };
        var contractTypes = typeof(IInvoiceGateway).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(IInvoiceGateway).Namespace);
        var forbiddenProperties = contractTypes
            .SelectMany(type => type.GetProperties())
            .Where(property => forbiddenTerms.Any(term =>
                property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
            .ToArray();

        Assert.Empty(unprotectedSensitiveProperties);
        Assert.Empty(forbiddenProperties);
    }

    private static InvoiceRequestSnapshot CreateExpectedSnapshot()
    {
        return new InvoiceRequestSnapshot(
            42,
            "ORDER-42",
            ElectronicInvoiceKind.EArchiveInvoice,
            InvoiceDocumentType.Sale,
            new DateOnly(2026, 8, 5),
            "TRY",
            12_500,
            0,
            0,
            12_500,
            2_500,
            15_000,
            [
                new InvoiceLineSnapshot(
                    "line-1",
                    "SKU-BRAKE-1",
                    "Brake pad",
                    1m,
                    "C62",
                    8_000,
                    7_500,
                    500,
                    [new InvoiceTaxSnapshot("0015", 20m, 7_500, 1_500)]),
                new InvoiceLineSnapshot(
                    "line-2",
                    "SKU-FILTER-1",
                    "Oil filter",
                    2m,
                    "C62",
                    2_500,
                    5_000,
                    0,
                    [new InvoiceTaxSnapshot("0015", 20m, 5_000, 1_000)])
            ],
            InvoiceInternetSaleSnapshot.Goods(
                new Uri("https://merchant.example"),
                "PAY_AT_DELIVERY",
                new DateOnly(2026, 8, 7),
                new DateOnly(2026, 8, 7),
                new InvoiceCarrierContext(
                    "Carrier Incorporated",
                    InvoicePartyIdentifier.TaxNumber("1234567890"))));
    }

    private static InvoiceRecipientContext CreateRecipient()
    {
        return InvoiceRecipientContext.Individual(
            "customer-42",
            "Ada Lovelace",
            new InvoiceAddressContext(
                "Example address",
                "Kadikoy",
                "Istanbul",
                "Turkey"),
            email: "ada@example.test");
    }

    private static void AssertFailure(
        InvoiceGatewayOutcome outcome,
        InvoiceGatewayErrorCode errorCode,
        string? message)
    {
        Assert.Equal(InvoiceGatewayOutcome.Failed, outcome);
        Assert.Equal(InvoiceGatewayErrorCode.ProviderNotConfigured, errorCode);
        Assert.Equal(DisabledInvoiceGateway.NotConfiguredMessage, message);
    }
}
