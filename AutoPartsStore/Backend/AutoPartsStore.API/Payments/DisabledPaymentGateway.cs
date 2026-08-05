namespace AutoPartsStore.API.Payments;

/// <summary>
/// Safe default used until a real hosted-payment provider is configured. Every
/// operation fails closed and never fabricates a successful provider response.
/// </summary>
public sealed class DisabledPaymentGateway : IPaymentGateway
{
    public const string NotConfiguredMessage = "Payment provider is not configured.";

    public string ProviderName => "Disabled";
    public bool IsEnabled => false;

    public Task<PaymentInitializationResult> InitializeAsync(
        InitializePaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentInitializationResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<PaymentConfirmationResult> ConfirmAsync(
        ConfirmPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentConfirmationResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<PaymentRetrievalResult> RetrieveAsync(
        RetrievePaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentRetrievalResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<PaymentWebhookVerificationResult> VerifyWebhookAsync(
        VerifyPaymentWebhookCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentWebhookVerificationResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<PaymentRefundResult> RefundAsync(
        RefundPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentRefundResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<PaymentInquiryResult> InquireAsync(
        InquirePaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PaymentInquiryResult(
            PaymentGatewayOutcome.Failed,
            PaymentGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }
}
