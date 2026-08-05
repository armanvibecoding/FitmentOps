using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Controllers;

[Route("api/b2b")]
[ApiController]
[Authorize]
public sealed class B2bController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly DealerApplicationService _applicationService;
    private readonly BulkQuoteService _quoteService;

    public B2bController(
        AutoPartsDbContext context,
        DealerApplicationService applicationService,
        BulkQuoteService quoteService)
    {
        _context = context;
        _applicationService = applicationService;
        _quoteService = quoteService;
    }

    [HttpPost("applications")]
    [EnableRateLimiting("b2b-write")]
    public async Task<IActionResult> SubmitApplication(
        DealerApplicationDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var result = await _applicationService.SubmitAsync(
            new DealerApplicationCommand(
                userId,
                idempotencyKey ?? string.Empty,
                dto.CompanyName,
                dto.TaxNumber,
                dto.ContactName,
                dto.ContactEmail,
                dto.ContactPhone),
            cancellationToken);
        var response = new
        {
            result.ApplicationId,
            result.Status,
            result.Message,
            Replayed = result.Outcome == DealerApplicationOutcome.Replayed
        };
        return result.Outcome switch
        {
            DealerApplicationOutcome.Submitted => StatusCode(StatusCodes.Status201Created, response),
            DealerApplicationOutcome.Replayed => Ok(response),
            DealerApplicationOutcome.NotFound => NotFound(response),
            DealerApplicationOutcome.Conflict => Conflict(response),
            DealerApplicationOutcome.InvalidRequest => BadRequest(response),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("application")]
    public async Task<IActionResult> GetApplication(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var application = await _context.DealerApplications
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.CompanyName,
                candidate.Status,
                CustomerGroup = candidate.CustomerGroup == null
                    ? null
                    : candidate.CustomerGroup.Name,
                candidate.CreatedAtUtc,
                candidate.ReviewedAtUtc,
                TaxNumberLast4 = candidate.TaxNumber.Length >= 4
                    ? candidate.TaxNumber.Substring(candidate.TaxNumber.Length - 4)
                    : null
            })
            .SingleOrDefaultAsync(cancellationToken);
        return application == null ? NotFound() : Ok(application);
    }

    [HttpPost("quotes")]
    [EnableRateLimiting("b2b-write")]
    public async Task<IActionResult> SubmitQuote(
        SubmitBulkQuoteDto dto,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var result = await _quoteService.SubmitAsync(
            new SubmitBulkQuoteCommand(
                userId,
                idempotencyKey ?? string.Empty,
                "TRY",
                dto.Lines.Select(line => new BulkQuoteInputLine(
                    line.Identifier,
                    line.Quantity)).ToArray()),
            cancellationToken);
        var response = new
        {
            result.RequestId,
            result.RequestNumber,
            result.Status,
            result.Replayed,
            result.Message
        };
        return result.Outcome switch
        {
            BulkQuoteOutcome.Submitted => StatusCode(StatusCodes.Status201Created, response),
            BulkQuoteOutcome.Replayed => Ok(response),
            BulkQuoteOutcome.NotEligible => StatusCode(StatusCodes.Status403Forbidden, response),
            BulkQuoteOutcome.Conflict => Conflict(response),
            BulkQuoteOutcome.InvalidRequest => BadRequest(response),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("quotes")]
    public async Task<IActionResult> GetQuotes(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var requests = await _context.BulkQuoteRequests
            .AsNoTracking()
            .Where(request => request.UserId == userId)
            .OrderByDescending(request => request.CreatedAtUtc)
            .Take(100)
            .Select(request => new
            {
                request.Id,
                request.RequestNumber,
                request.Status,
                request.Currency,
                request.CreatedAtUtc,
                request.QuoteValidUntilUtc,
                LineCount = request.Lines.Count
            })
            .ToListAsync(cancellationToken);
        return Ok(requests);
    }

    [HttpGet("quotes/{id:long}")]
    public async Task<IActionResult> GetQuote(long id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var request = await _context.BulkQuoteRequests
            .AsNoTracking()
            .Where(candidate => candidate.Id == id && candidate.UserId == userId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.RequestNumber,
                candidate.Status,
                candidate.Currency,
                candidate.CreatedAtUtc,
                candidate.QuoteValidUntilUtc,
                Lines = candidate.Lines
                    .OrderBy(line => line.LineNumber)
                    .Select(line => new
                    {
                        line.Id,
                        line.LineNumber,
                        line.RequestedIdentifier,
                        line.RequestedQuantity,
                        line.Status,
                        line.QuotedUnitPrice,
                        line.AvailableQuantity,
                        line.LeadTimeDays
                    })
            })
            .SingleOrDefaultAsync(cancellationToken);
        return request == null ? NotFound() : Ok(request);
    }

    [HttpPost("quotes/{id:long}/accept")]
    [EnableRateLimiting("b2b-write")]
    public async Task<IActionResult> AcceptQuote(long id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var result = await _quoteService.AcceptAsync(id, userId, cancellationToken);
        var response = new
        {
            result.RequestId,
            result.RequestNumber,
            result.Status,
            result.Replayed,
            result.Message
        };
        return result.Outcome switch
        {
            BulkQuoteOutcome.Updated => Ok(response),
            BulkQuoteOutcome.Replayed => Ok(response),
            BulkQuoteOutcome.Expired => Conflict(response),
            BulkQuoteOutcome.Conflict => Conflict(response),
            BulkQuoteOutcome.NotFound => NotFound(response),
            BulkQuoteOutcome.InvalidRequest => BadRequest(response),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private bool TryGetUserId(out int userId) =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId > 0;
}

public sealed class DealerApplicationDto
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string CompanyName { get; set; } = string.Empty;

    [Required, StringLength(32, MinimumLength = 5)]
    public string TaxNumber { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 2)]
    public string ContactName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string ContactEmail { get; set; } = string.Empty;

    [Required, Phone, StringLength(20)]
    public string ContactPhone { get; set; } = string.Empty;

    public override string ToString() => $"{nameof(DealerApplicationDto)} {{ Sensitive = true }}";
}

public sealed class SubmitBulkQuoteDto
{
    [Required, MinLength(1), MaxLength(BulkQuoteService.MaxLines)]
    public List<SubmitBulkQuoteLineDto> Lines { get; set; } = new();
}

public sealed class SubmitBulkQuoteLineDto
{
    [Required, StringLength(80, MinimumLength = 1)]
    public string Identifier { get; set; } = string.Empty;

    [Range(1, 100_000)]
    public int Quantity { get; set; }
}
