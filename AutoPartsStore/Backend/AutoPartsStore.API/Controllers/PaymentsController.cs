using System.Collections.Immutable;
using System.Text.Json;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AutoPartsStore.API.Controllers;

[Route("api/[controller]")]
[ApiController]
public sealed class PaymentsController : ControllerBase
{
    private readonly IPaymentGateway _paymentGateway;
    private readonly PaymentCallbackReconciliationService? _reconciliationService;

    public PaymentsController(
        IPaymentGateway paymentGateway,
        PaymentCallbackReconciliationService? reconciliationService = null)
    {
        _paymentGateway = paymentGateway;
        _reconciliationService = reconciliationService;
    }

    [HttpGet("capabilities")]
    [AllowAnonymous]
    public ActionResult<PaymentCapabilitiesDto> GetCapabilities()
    {
        return Ok(new PaymentCapabilitiesDto
        {
            PayAtDelivery = true,
            OnlineCard = _paymentGateway.IsEnabled,
            OnlineProvider = _paymentGateway.IsEnabled ? _paymentGateway.ProviderName : null,
            HostedCardEntryOnly = true
        });
    }

    [HttpPost("callback")]
    [AllowAnonymous]
    [EnableRateLimiting("payment-callback")]
    public async Task<ActionResult<PaymentReconciliationResponseDto>> ConfirmCallback(
        CancellationToken cancellationToken)
    {
        // Avoid reading a token-bearing body while online payments are disabled.
        if (!_paymentGateway.IsEnabled || _reconciliationService == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Online payment is not configured." });
        }

        var body = await ReadBoundedBodyAsync(
            PaymentCallbackReconciliationService.MaxCallbackBodyBytes,
            cancellationToken);
        if (body == null)
        {
            return BadRequest(new { message = "The callback body is invalid or too large." });
        }

        PaymentCallbackHttpRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PaymentCallbackHttpRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "The callback body is invalid." });
        }

        if (request == null)
        {
            return BadRequest(new { message = "The callback body is invalid." });
        }

        var result = await _reconciliationService.ConfirmCallbackAsync(
            new PaymentCallbackCommand(request.PaymentId, request.HostedPaymentToken),
            cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("webhooks/{provider}")]
    [AllowAnonymous]
    [EnableRateLimiting("payment-webhook")]
    public async Task<ActionResult<PaymentReconciliationResponseDto>> HandleWebhook(
        string provider,
        CancellationToken cancellationToken)
    {
        // Fail before reading or retaining any untrusted provider payload.
        if (!_paymentGateway.IsEnabled || _reconciliationService == null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Online payment is not configured." });
        }

        if (!string.Equals(
                provider?.Trim(),
                _paymentGateway.ProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var body = await ReadBoundedBodyAsync(
            PaymentCallbackReconciliationService.MaxWebhookBodyBytes,
            cancellationToken);
        if (body == null || !TryGetBoundedHeaders(out var headers))
        {
            return BadRequest(new { message = "The webhook request is invalid or too large." });
        }

        var result = await _reconciliationService.HandleWebhookAsync(
            body,
            headers,
            cancellationToken);
        return ToActionResult(result);
    }

    private async Task<byte[]?> ReadBoundedBodyAsync(
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is > 0 && Request.ContentLength > maximumBytes)
        {
            return null;
        }

        await using var buffer = new MemoryStream(capacity: Math.Min(maximumBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await Request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private bool TryGetBoundedHeaders(
        out ImmutableDictionary<string, ImmutableArray<string>> headers)
    {
        if (Request.Headers.Count > 100)
        {
            headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
            return false;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var header in Request.Headers)
        {
            if (header.Key.Length is < 1 or > 128 ||
                header.Value.Count > 20 ||
                header.Value.Any(value => value == null || value.Length > 2048))
            {
                headers = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
                return false;
            }

            builder[header.Key] = header.Value
                .Select(value => value!)
                .ToImmutableArray();
        }

        headers = builder.ToImmutable();
        return true;
    }

    private ActionResult<PaymentReconciliationResponseDto> ToActionResult(
        PaymentReconciliationResult result)
    {
        var response = new PaymentReconciliationResponseDto
        {
            Outcome = result.Outcome.ToString(),
            PaymentStatus = result.PaymentStatus,
            AttemptStatus = result.AttemptStatus,
            Message = result.Message
        };
        return result.Outcome switch
        {
            PaymentReconciliationOutcome.Succeeded => Ok(response),
            PaymentReconciliationOutcome.Replayed => Ok(response),
            PaymentReconciliationOutcome.PendingReconciliation => Accepted(response),
            PaymentReconciliationOutcome.Failed =>
                StatusCode(StatusCodes.Status402PaymentRequired, response),
            PaymentReconciliationOutcome.ProviderDisabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, response),
            PaymentReconciliationOutcome.VerificationFailed => Unauthorized(response),
            PaymentReconciliationOutcome.NotFound => NotFound(response),
            PaymentReconciliationOutcome.Conflict => Conflict(response),
            PaymentReconciliationOutcome.InvalidRequest => BadRequest(response),
            _ => StatusCode(StatusCodes.Status500InternalServerError, response)
        };
    }
}

public sealed class PaymentCapabilitiesDto
{
    public bool PayAtDelivery { get; set; }
    public bool OnlineCard { get; set; }
    public string? OnlineProvider { get; set; }
    public bool HostedCardEntryOnly { get; set; }
}
