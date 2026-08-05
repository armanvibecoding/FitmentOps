using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public sealed class LegalCheckoutOptions
{
    public string[] RequiredDocumentTypes { get; init; } =
    [
        LegalDocumentTypes.PreliminaryInformation,
        LegalDocumentTypes.DistanceSalesAgreement
    ];

    public void Validate()
    {
        if (RequiredDocumentTypes is not { Length: > 0 and <= 10 } ||
            RequiredDocumentTypes.Distinct(StringComparer.Ordinal).Count() != RequiredDocumentTypes.Length ||
            RequiredDocumentTypes.Any(type => !LegalDocumentTypes.All.Contains(type, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("LegalCheckout:RequiredDocumentTypes is invalid.");
        }
    }
}

public enum LegalConsentValidationOutcome
{
    Valid,
    ConfigurationUnavailable,
    InvalidAcceptance
}

public sealed record LegalConsentValidationResult(
    LegalConsentValidationOutcome Outcome,
    IReadOnlyList<LegalDocumentVersion> Documents,
    string? Message = null);

public sealed class LegalConsentService
{
    private readonly AutoPartsDbContext _context;
    private readonly LegalCheckoutOptions _options;

    public LegalConsentService(AutoPartsDbContext context, LegalCheckoutOptions options)
    {
        _context = context;
        _options = options;
        _options.Validate();
    }

    public async Task<LegalConsentValidationResult> ValidateAsync(
        IReadOnlyCollection<LegalAcceptanceDto>? submitted,
        CancellationToken cancellationToken = default)
    {
        var documents = await _context.LegalDocumentVersions
            .AsNoTracking()
            .Where(document =>
                document.Status == LegalDocumentStatuses.Published &&
                document.PublishedAtUtc != null &&
                _options.RequiredDocumentTypes.Contains(document.DocumentType))
            .OrderBy(document => document.DocumentType)
            .ToListAsync(cancellationToken);
        if (documents.Count != _options.RequiredDocumentTypes.Length ||
            _options.RequiredDocumentTypes.Any(type =>
                documents.Count(document => document.DocumentType == type) != 1))
        {
            return new LegalConsentValidationResult(
                LegalConsentValidationOutcome.ConfigurationUnavailable,
                [],
                "Checkout legal documents are not fully published.");
        }

        if (submitted == null || submitted.Count != documents.Count)
        {
            return Invalid(documents);
        }

        var normalized = submitted
            .GroupBy(
                acceptance => (acceptance.DocumentType ?? string.Empty).Trim(),
                StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (!normalized.TryGetValue(document.DocumentType, out var matches) ||
                matches.Length != 1 ||
                !matches[0].Accepted ||
                !string.Equals((matches[0].Version ?? string.Empty).Trim(), document.Version, StringComparison.Ordinal) ||
                !string.Equals(
                    (matches[0].ContentSha256 ?? string.Empty).Trim(),
                    document.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(documents);
            }
        }

        return new LegalConsentValidationResult(LegalConsentValidationOutcome.Valid, documents);
    }

    public void AttachToOrder(
        Order order,
        IEnumerable<LegalDocumentVersion> documents,
        int? userId,
        string checkoutIdempotencyKey,
        DateTime acceptedAtUtc)
    {
        foreach (var document in documents)
        {
            order.LegalAcceptances.Add(LegalAcceptance.Create(
                document,
                userId,
                checkoutIdempotencyKey,
                acceptedAtUtc));
        }
    }

    public async Task<IReadOnlyList<LegalDocumentVersion>?> GetRequiredPublishedAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await _context.LegalDocumentVersions
            .AsNoTracking()
            .Where(document =>
                document.Status == LegalDocumentStatuses.Published &&
                document.PublishedAtUtc != null &&
                _options.RequiredDocumentTypes.Contains(document.DocumentType))
            .OrderBy(document => document.DocumentType)
            .ToListAsync(cancellationToken);
        return documents.Count == _options.RequiredDocumentTypes.Length &&
               _options.RequiredDocumentTypes.All(type =>
                   documents.Count(document => document.DocumentType == type) == 1)
            ? documents
            : null;
    }

    private static LegalConsentValidationResult Invalid(
        IReadOnlyList<LegalDocumentVersion> documents) => new(
            LegalConsentValidationOutcome.InvalidAcceptance,
            documents,
            "The current required legal documents must be explicitly accepted.");
}
