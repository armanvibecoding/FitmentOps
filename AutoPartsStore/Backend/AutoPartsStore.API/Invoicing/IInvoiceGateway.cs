namespace AutoPartsStore.API.Invoicing;

/// <summary>
/// Provider-neutral boundary for issuing and tracking Turkish electronic invoices.
/// Provider credentials, UBL-TR XML and signing material belong in an adapter and
/// must not cross this application contract.
/// </summary>
public interface IInvoiceGateway
{
    string ProviderName { get; }
    bool IsEnabled { get; }

    Task<InvoiceCreationResult> CreateAsync(
        CreateInvoiceCommand command,
        CancellationToken cancellationToken = default);

    Task<InvoiceQueryResult> QueryAsync(
        QueryInvoiceCommand command,
        CancellationToken cancellationToken = default);

    Task<InvoiceCancellationResult> CancelAsync(
        CancelInvoiceCommand command,
        CancellationToken cancellationToken = default);

    Task<InvoiceObjectionResult> SubmitObjectionAsync(
        SubmitInvoiceObjectionCommand command,
        CancellationToken cancellationToken = default);
}
