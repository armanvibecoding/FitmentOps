namespace AutoPartsStore.API.Invoicing;

/// <summary>
/// Safe default until a licensed integration path and provider are configured.
/// It never fabricates an issued, queried or cancelled invoice.
/// </summary>
public sealed class DisabledInvoiceGateway : IInvoiceGateway
{
    public const string NotConfiguredMessage = "Invoice provider is not configured.";

    public string ProviderName => "Disabled";
    public bool IsEnabled => false;

    public Task<InvoiceCreationResult> CreateAsync(
        CreateInvoiceCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InvoiceCreationResult.Failed(
            InvoiceGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<InvoiceQueryResult> QueryAsync(
        QueryInvoiceCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InvoiceQueryResult.Failed(
            InvoiceGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<InvoiceCancellationResult> CancelAsync(
        CancelInvoiceCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InvoiceCancellationResult.Failed(
            InvoiceGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }

    public Task<InvoiceObjectionResult> SubmitObjectionAsync(
        SubmitInvoiceObjectionCommand command,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InvoiceObjectionResult.Failed(
            InvoiceGatewayErrorCode.ProviderNotConfigured,
            NotConfiguredMessage));
    }
}
