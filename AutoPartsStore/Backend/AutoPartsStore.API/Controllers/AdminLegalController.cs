using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Admin/legal-documents")]
[Authorize]
public sealed class AdminLegalController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly AdminAuditIntentService _auditIntentService;
    private readonly TimeProvider _timeProvider;

    public AdminLegalController(
        AutoPartsDbContext context,
        AdminAuditIntentService auditIntentService,
        TimeProvider timeProvider)
    {
        _context = context;
        _auditIntentService = auditIntentService;
        _timeProvider = timeProvider;
    }

    [HttpGet]
    [Authorize(Policy = AdminPolicyNames.AdminAccess)]
    public async Task<ActionResult<IReadOnlyList<AdminLegalDocumentDto>>> GetDocuments(
        CancellationToken cancellationToken)
    {
        var documents = await _context.LegalDocumentVersions
            .AsNoTracking()
            .OrderBy(document => document.DocumentType)
            .ThenByDescending(document => document.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return documents.Select(ToDto).ToArray();
    }

    [HttpPost]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public async Task<ActionResult<AdminLegalDocumentDto>> CreateDraft(
        CreateLegalDocumentDraftDto dto,
        CancellationToken cancellationToken)
    {
        var actor = GetActor();
        if (actor == null) return Forbid();

        LegalDocumentVersion draft;
        try
        {
            draft = LegalDocumentVersion.CreateDraft(
                dto.DocumentType,
                dto.Version,
                dto.Title,
                dto.Content,
                actor.Value.UserId,
                _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }

        var existing = await _context.LegalDocumentVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(document =>
                document.DocumentType == draft.DocumentType &&
                document.Version == draft.Version,
                cancellationToken);
        if (existing != null)
        {
            return existing.ContentSha256 == draft.ContentSha256 &&
                   existing.Title == draft.Title
                ? Ok(ToDto(existing))
                : Conflict(new { message = "This legal document type and version already exists with different content." });
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        _context.LegalDocumentVersions.Add(draft);
        await _context.SaveChangesAsync(cancellationToken);
        StageAudit(actor.Value, AdminAuditActions.LegalDocumentCreated, draft.Id);
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return CreatedAtAction(nameof(GetDocuments), new { id = draft.Id }, ToDto(draft));
    }

    [HttpPost("{id:long}/publish")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public Task<IActionResult> Publish(
        long id,
        LegalDocumentTransitionDto dto,
        CancellationToken cancellationToken) =>
        Transition(id, dto, publish: true, cancellationToken);

    [HttpPost("{id:long}/retire")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public Task<IActionResult> Retire(
        long id,
        LegalDocumentTransitionDto dto,
        CancellationToken cancellationToken) =>
        Transition(id, dto, publish: false, cancellationToken);

    private async Task<IActionResult> Transition(
        long id,
        LegalDocumentTransitionDto dto,
        bool publish,
        CancellationToken cancellationToken)
    {
        var actor = GetActor();
        if (actor == null) return Forbid();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var document = await _context.LegalDocumentVersions.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (document == null) return NotFound();
        if (document.ConcurrencyToken != dto.ConcurrencyToken)
        {
            return Conflict(new { message = "The legal document changed; reload before retrying." });
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (publish)
        {
            if (document.Status == LegalDocumentStatuses.Retired)
            {
                return Conflict(new { message = "Retired legal documents cannot be republished; create a new version." });
            }

            var current = await _context.LegalDocumentVersions
                .Where(candidate =>
                    candidate.DocumentType == document.DocumentType &&
                    candidate.Status == LegalDocumentStatuses.Published &&
                    candidate.Id != document.Id)
                .SingleOrDefaultAsync(cancellationToken);
            current?.Retire(now);
            document.Publish(actor.Value.UserId, now);
        }
        else
        {
            document.Retire(now);
        }

        StageAudit(
            actor.Value,
            publish ? AdminAuditActions.LegalDocumentPublished : AdminAuditActions.LegalDocumentRetired,
            document.Id);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "The legal document changed; reload before retrying." });
        }
    }

    private void StageAudit((int UserId, string Role) actor, string action, long aggregateId)
    {
        var result = _auditIntentService.Stage(new AdminAuditIntentStageRequest(
            Guid.NewGuid(),
            actor.UserId,
            actor.Role,
            action,
            AdminAuditAggregateTypes.LegalDocument,
            aggregateId,
            HttpContext.TraceIdentifier,
            AdminAuditOutcomes.Succeeded));
        if (result.Outcome != AdminAuditIntentStageOutcome.Staged)
        {
            throw new InvalidOperationException($"Legal document audit staging failed: {result.ErrorCode}");
        }
    }

    private (int UserId, string Role)? GetActor()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = User.FindFirstValue(ClaimTypes.Role);
        return int.TryParse(userId, out var parsedUserId) &&
               parsedUserId > 0 &&
               AdminAuditRoles.All.Contains(role ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            ? (parsedUserId, role!)
            : null;
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(
        CancellationToken cancellationToken)
    {
        return _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;
    }

    private static AdminLegalDocumentDto ToDto(LegalDocumentVersion document) => new()
    {
        Id = document.Id,
        DocumentType = document.DocumentType,
        Version = document.Version,
        Title = document.Title,
        Content = document.Content,
        ContentSha256 = document.ContentSha256,
        Status = document.Status,
        CreatedAtUtc = document.CreatedAtUtc,
        PublishedAtUtc = document.PublishedAtUtc,
        RetiredAtUtc = document.RetiredAtUtc,
        ConcurrencyToken = document.ConcurrencyToken
    };
}

public sealed class CreateLegalDocumentDraftDto
{
    [Required, StringLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, StringLength(40)]
    public string Version { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(100_000)]
    public string Content { get; set; } = string.Empty;
}

public sealed class LegalDocumentTransitionDto
{
    public Guid ConcurrencyToken { get; set; }
}

public sealed class AdminLegalDocumentDto
{
    public long Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public Guid ConcurrencyToken { get; set; }
}
