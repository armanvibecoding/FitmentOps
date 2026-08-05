using Microsoft.Extensions.Primitives;

namespace AutoPartsStore.API.Observability;

/// <summary>
/// Establishes one log-safe correlation identifier for the entire request. The
/// middleware deliberately never reads or logs request/response bodies.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(
        RequestDelegate next,
        ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = Resolve(context.Request.Headers[HeaderName]);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        context.Response.OnStarting(
            static state =>
            {
                var values = ((HttpContext Context, string CorrelationId))state;
                values.Context.Response.Headers[HeaderName] = values.CorrelationId;
                return Task.CompletedTask;
            },
            (context, correlationId));

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await _next(context);
        }
    }

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':');
    }

    private static string Resolve(StringValues suppliedValues)
    {
        if (suppliedValues.Count == 1 && IsValid(suppliedValues[0]))
        {
            return suppliedValues[0]!;
        }

        return Guid.NewGuid().ToString("N");
    }
}
