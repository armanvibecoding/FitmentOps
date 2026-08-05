namespace AutoPartsStore.API.Payments;

public interface IPaymentGateway
{
    string ProviderName { get; }
    bool IsEnabled { get; }

    Task<PaymentInitializationResult> InitializeAsync(
        InitializePaymentCommand command,
        CancellationToken cancellationToken = default);

    Task<PaymentConfirmationResult> ConfirmAsync(
        ConfirmPaymentCommand command,
        CancellationToken cancellationToken = default);

    Task<PaymentRetrievalResult> RetrieveAsync(
        RetrievePaymentCommand command,
        CancellationToken cancellationToken = default);

    Task<PaymentWebhookVerificationResult> VerifyWebhookAsync(
        VerifyPaymentWebhookCommand command,
        CancellationToken cancellationToken = default);

    Task<PaymentRefundResult> RefundAsync(
        RefundPaymentCommand command,
        CancellationToken cancellationToken = default);

    Task<PaymentInquiryResult> InquireAsync(
        InquirePaymentCommand command,
        CancellationToken cancellationToken = default);
}
