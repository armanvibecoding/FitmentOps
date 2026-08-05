using System.Collections.Immutable;
using System.Text.Json;
using AutoPartsStore.API.Payments;
using Xunit;

namespace AutoPartsStore.API.Tests.Payments;

public sealed class DisabledPaymentGatewayTests
{
    private readonly DisabledPaymentGateway _gateway = new();

    [Fact]
    public async Task AllOperationsFailClosedWithTheSameDeterministicError()
    {
        var expected = CreateExpectedSnapshot();
        var initialize = await _gateway.InitializeAsync(new InitializePaymentCommand(
            expected,
            new Uri("https://merchant.example/payments/callback"),
            new Uri("https://merchant.example/orders/ORDER-42"),
            "checkout-42"));
        var confirm = await _gateway.ConfirmAsync(new ConfirmPaymentCommand(
            expected,
            "hosted-token"));
        var retrieve = await _gateway.RetrieveAsync(new RetrievePaymentCommand(
            expected,
            ProviderPaymentId: "provider-payment-42"));
        var webhook = await _gateway.VerifyWebhookAsync(new VerifyPaymentWebhookCommand(
            "{}"u8.ToArray(),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty));
        var refund = await _gateway.RefundAsync(new RefundPaymentCommand(
            expected,
            "provider-payment-42",
            5_000,
            "TRY",
            "refund-42",
            "customer_request"));
        var inquiry = await _gateway.InquireAsync(new InquirePaymentCommand(
            expected,
            "provider-payment-42"));

        AssertFailure(initialize.Outcome, initialize.ErrorCode, initialize.UserSafeMessage);
        AssertFailure(confirm.Outcome, confirm.ErrorCode, confirm.UserSafeMessage);
        AssertFailure(retrieve.Outcome, retrieve.ErrorCode, retrieve.UserSafeMessage);
        AssertFailure(webhook.Outcome, webhook.ErrorCode, webhook.UserSafeMessage);
        AssertFailure(refund.Outcome, refund.ErrorCode, refund.UserSafeMessage);
        AssertFailure(inquiry.Outcome, inquiry.ErrorCode, inquiry.UserSafeMessage);

        Assert.Null(initialize.HostedPaymentPageUri);
        Assert.Null(initialize.HostedPaymentToken);
        Assert.Null(initialize.ProviderPaymentId);
        Assert.Null(confirm.Payment);
        Assert.Null(retrieve.Payment);
        Assert.Null(webhook.ProviderEventId);
        Assert.Null(webhook.Payment);
        Assert.Null(refund.ProviderRefundId);
        Assert.Null(inquiry.Payment);
    }

    [Fact]
    public void ContractsCarryExactMerchantSnapshotAndRawWebhookEnvelope()
    {
        var expected = CreateExpectedSnapshot();
        var body = "{\"status\":\"success\"}"u8.ToArray();
        var headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty
            .Add("x-provider-signature", ["signature-value"]);
        var webhook = new VerifyPaymentWebhookCommand(body, headers);

        Assert.Equal(42, expected.PaymentId);
        Assert.Equal("f8aeed1a-6960-47bb-b86d-de8e81ed60c0", expected.ConversationId);
        Assert.Equal("ORDER-42", expected.OrderNumber);
        Assert.Equal(12_500, expected.AmountMinor);
        Assert.Equal("TRY", expected.Currency);
        Assert.Equal(2, expected.BasketItems.Length);
        Assert.Equal(12_500, expected.BasketItems.Sum(item => item.LineTotalMinor));
        Assert.True(webhook.RawBody.Span.SequenceEqual(body));
        Assert.Equal("signature-value", webhook.Headers["x-provider-signature"].Single());
    }

    [Fact]
    public void PublicContractsDoNotAcceptCardOrProviderCredentialFields()
    {
        var forbiddenTerms = new[]
        {
            "CardNumber", "Pan", "Cvv", "Cvc", "Expiry", "ApiKey", "SecretKey",
            "PrivateKey", "Credential"
        };
        var contractTypes = typeof(IPaymentGateway).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(IPaymentGateway).Namespace);

        var forbiddenProperties = contractTypes
            .SelectMany(type => type.GetProperties())
            .Where(property => forbiddenTerms.Any(term =>
                property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
            .ToArray();

        Assert.Empty(forbiddenProperties);
    }

    [Fact]
    public void InitializationCommandDoesNotSerializeOrPrintSensitiveBuyerData()
    {
        var buyer = new PaymentBuyerContext(
            "buyer-42",
            "Ada",
            "Lovelace",
            "ada@example.test",
            "+905550000000",
            "sensitive-identity",
            "203.0.113.10");
        var address = new PaymentAddressContext(
            "Ada Lovelace",
            "Sensitive address",
            "Istanbul",
            "Turkey",
            "34000");
        var command = new InitializePaymentCommand(
            CreateExpectedSnapshot(),
            new Uri("https://merchant.example/payments/callback"),
            new Uri("https://merchant.example/orders/ORDER-42"),
            "sensitive-idempotency-key",
            buyer,
            address,
            address);

        var json = JsonSerializer.Serialize(command);

        Assert.DoesNotContain("sensitive-identity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive address", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-idempotency-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-idempotency-key", command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-identity", command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ada", buyer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive address", address.ToString(), StringComparison.Ordinal);

        var confirm = new ConfirmPaymentCommand(CreateExpectedSnapshot(), "sensitive-hosted-token");
        var retrieve = new RetrievePaymentCommand(
            CreateExpectedSnapshot(),
            "provider-payment-42",
            "sensitive-hosted-token");
        var initialization = new PaymentInitializationResult(
            PaymentGatewayOutcome.Succeeded,
            PaymentGatewayErrorCode.None,
            HostedPaymentPageUri: new Uri("https://provider.example/checkout?sensitive-token"),
            HostedPaymentToken: "sensitive-hosted-token");
        var webhook = new VerifyPaymentWebhookCommand(
            "sensitive-webhook-body"u8.ToArray(),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add(
                "x-signature",
                ["sensitive-webhook-signature"]));
        var refund = new RefundPaymentCommand(
            CreateExpectedSnapshot(),
            "provider-payment-42",
            1_000,
            "TRY",
            "sensitive-refund-idempotency",
            "customer_request");

        Assert.DoesNotContain("sensitive-hosted-token", confirm.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-hosted-token", retrieve.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-hosted-token", initialization.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-webhook-body", webhook.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-webhook-signature", webhook.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-refund-idempotency", refund.ToString(), StringComparison.Ordinal);
    }

    private static PaymentRequestSnapshot CreateExpectedSnapshot()
    {
        return new PaymentRequestSnapshot(
            42,
            "f8aeed1a-6960-47bb-b86d-de8e81ed60c0",
            "ORDER-42",
            12_500,
            "TRY",
            [
                new PaymentBasketItemSnapshot(
                    "line-1",
                    "SKU-BRAKE-1",
                    "Brake pad",
                    1,
                    7_500,
                    7_500),
                new PaymentBasketItemSnapshot(
                    "line-2",
                    "SKU-FILTER-1",
                    "Oil filter",
                    2,
                    2_500,
                    5_000)
            ]);
    }

    private static void AssertFailure(
        PaymentGatewayOutcome outcome,
        PaymentGatewayErrorCode errorCode,
        string? message)
    {
        Assert.Equal(PaymentGatewayOutcome.Failed, outcome);
        Assert.Equal(PaymentGatewayErrorCode.ProviderNotConfigured, errorCode);
        Assert.Equal(DisabledPaymentGateway.NotConfiguredMessage, message);
    }
}
