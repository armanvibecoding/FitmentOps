using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoPartsStore.API.Services;

public sealed class AdminAuditIntentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AdminAuditIntentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminAuditIntentWorker> _logger;

    public AdminAuditIntentWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AdminAuditIntentWorker> logger,
        AdminAuditIntentOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new AdminAuditIntentOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var intentService = scope.ServiceProvider
                    .GetRequiredService<AdminAuditIntentService>();
                var auditService = scope.ServiceProvider
                    .GetRequiredService<AdminAuditService>();
                var summary = await intentService.DispatchBatchAsync(
                    auditService,
                    _options,
                    stoppingToken);
                claimed = summary.Claimed;
                if (summary.Claimed > 0)
                {
                    _logger.LogInformation(
                        "Admin audit intents dispatched. Claimed: {Claimed}, Succeeded: {Succeeded}, Retried: {Retried}, Failed: {Failed}.",
                        summary.Claimed,
                        summary.Succeeded,
                        summary.RetriesScheduled,
                        summary.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Admin audit intent dispatch failed with {ExceptionType}.",
                    exception.GetType().Name);
            }

            if (claimed == 0)
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
        }
    }
}
