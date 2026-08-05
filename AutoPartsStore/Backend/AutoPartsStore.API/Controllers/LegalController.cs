using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/legal")]
public sealed class LegalController : ControllerBase
{
    private readonly LegalConsentService _legalConsentService;

    public LegalController(LegalConsentService legalConsentService)
    {
        _legalConsentService = legalConsentService;
    }

    [HttpGet("checkout-documents")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<IReadOnlyList<CheckoutLegalDocumentDto>>> GetCheckoutDocuments(
        CancellationToken cancellationToken)
    {
        var documents = await _legalConsentService.GetRequiredPublishedAsync(cancellationToken);
        if (documents == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Checkout legal documents are not fully published." });
        }

        return documents.Select(document => new CheckoutLegalDocumentDto
        {
            DocumentType = document.DocumentType,
            Version = document.Version,
            Title = document.Title,
            Content = document.Content,
            ContentSha256 = document.ContentSha256,
            PublishedAtUtc = document.PublishedAtUtc!.Value
        }).ToArray();
    }
}

public sealed class CheckoutLegalDocumentDto
{
    public string DocumentType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; }
}
