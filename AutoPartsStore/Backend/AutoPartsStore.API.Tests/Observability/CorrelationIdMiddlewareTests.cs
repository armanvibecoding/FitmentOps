using AutoPartsStore.API.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AutoPartsStore.API.Tests.Observability;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task ValidIncomingId_IsPropagatedToTraceResponseAndLogScope()
    {
        const string supplied = "checkout:7f7cb030-a596-4e10";
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;
        var logger = new ScopeCapturingLogger();
        var nextCalled = false;
        var middleware = new CorrelationIdMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(supplied, context.TraceIdentifier);
        Assert.Equal(supplied, context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
        Assert.Equal(supplied, logger.ScopeValues["CorrelationId"]);
    }

    [Theory]
    [InlineData("contains whitespace")]
    [InlineData("customer@example.com")]
    [InlineData("müşteri")]
    public async Task UnsafeIncomingId_IsReplacedWithGeneratedSafeId(string supplied)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = supplied;
        var middleware = new CorrelationIdMiddleware(
            _ => Task.CompletedTask,
            new ScopeCapturingLogger());

        await middleware.InvokeAsync(context);

        Assert.NotEqual(supplied, context.TraceIdentifier);
        Assert.Equal(32, context.TraceIdentifier.Length);
        Assert.True(CorrelationIdMiddleware.IsValid(context.TraceIdentifier));
        Assert.Equal(
            context.TraceIdentifier,
            context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task OversizedOrMultipleIncomingValues_AreRejected()
    {
        var oversized = new string('a', CorrelationIdMiddleware.MaxLength + 1);
        Assert.False(CorrelationIdMiddleware.IsValid(oversized));

        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] =
            new StringValues(["first-safe", "second-safe"]);
        var middleware = new CorrelationIdMiddleware(
            _ => Task.CompletedTask,
            new ScopeCapturingLogger());

        await middleware.InvokeAsync(context);

        Assert.NotEqual("first-safe", context.TraceIdentifier);
        Assert.NotEqual("second-safe", context.TraceIdentifier);
        Assert.True(CorrelationIdMiddleware.IsValid(context.TraceIdentifier));
    }

    private sealed class ScopeCapturingLogger : ILogger<CorrelationIdMiddleware>
    {
        public Dictionary<string, object?> ScopeValues { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    ScopeValues[value.Key] = value.Value;
                }
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
