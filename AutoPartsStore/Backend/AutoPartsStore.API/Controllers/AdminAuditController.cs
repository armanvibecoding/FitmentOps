using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Admin/audit")]
[Authorize(Policy = AdminPolicyNames.SuperAdmin)]
public sealed class AdminAuditController : ControllerBase
{
    private readonly AdminAuditService _auditService;

    public AdminAuditController(AdminAuditService auditService)
    {
        _auditService = auditService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminAuditEventMetadata>>> GetEvents(
        [FromQuery] int pageSize = 100,
        [FromQuery] long? beforeSequence = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > AdminAuditService.MaxQueryPageSize ||
            beforeSequence is <= 0)
        {
            return BadRequest(new { message = "Invalid audit pagination." });
        }

        return Ok(await _auditService.GetMetadataAsync(
            pageSize,
            beforeSequence,
            cancellationToken));
    }

    [HttpGet("verify")]
    public async Task<ActionResult<AdminAuditChainVerificationResult>> VerifyChain(
        CancellationToken cancellationToken)
    {
        var result = await _auditService.VerifyChainAsync(cancellationToken);
        return result.IsValid
            ? Ok(result)
            : StatusCode(StatusCodes.Status409Conflict, result);
    }
}
