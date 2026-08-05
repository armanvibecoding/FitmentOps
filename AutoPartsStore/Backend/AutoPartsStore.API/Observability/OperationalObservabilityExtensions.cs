using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoPartsStore.API.Observability;

public static class OperationalObservabilityExtensions
{
    public static IServiceCollection AddOperationalObservability(
        this IServiceCollection services,
        OperationalReadinessOptions? readinessOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        readinessOptions ??= new OperationalReadinessOptions();
        readinessOptions.Validate();
        services.TryAddSingleton(readinessOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<OperationalReadinessService>();
        return services;
    }

    public static IServiceCollection AddOutboxDispatchWorker(
        this IServiceCollection services,
        OutboxWorkerOptions? workerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        workerOptions ??= new OutboxWorkerOptions();
        workerOptions.Validate();
        services.TryAddSingleton(workerOptions);
        services.TryAddScoped<OutboxService>();
        services.TryAddScoped<IOutboxLeaseStore>(provider =>
            provider.GetRequiredService<OutboxService>());
        services.TryAddScoped<OutboxBatchProcessor>();
        services.TryAddSingleton<IOutboxMessageDispatcher, DisabledOutboxMessageDispatcher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxWorker>());
        return services;
    }

    public static IApplicationBuilder UseRequestCorrelation(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.UseMiddleware<CorrelationIdMiddleware>();
    }

    public static IEndpointRouteBuilder MapOperationalHealthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health/live", static () =>
            TypedResults.Ok(OperationalReadinessService.Liveness()));
        endpoints.MapGet(
            "/health/ready",
            async Task<Results<Ok<ReadinessResponse>, JsonHttpResult<ReadinessResponse>>> (
                OperationalReadinessService service,
                CancellationToken cancellationToken) =>
            {
                var response = await service.CheckReadinessAsync(cancellationToken);
                return response.Ready
                    ? TypedResults.Ok(response)
                    : TypedResults.Json(
                        response,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            });

        return endpoints;
    }
}
