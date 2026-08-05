namespace AutoPartsStore.API.Services;

public sealed class InventoryReservationExpiryOptions
{
    public const string ConfigurationSectionName = "InventoryReservationExpiry";

    /// <summary>
    /// Fail-closed by default. Production must explicitly set
    /// InventoryReservationExpiry:Enabled=true after registering the scoped processor.
    /// </summary>
    public bool Enabled { get; init; }

    public int BatchSize { get; init; } = 100;
    public int MaxBatchesPerPoll { get; init; } = 4;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (BatchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize),
                "Batch size must be between 1 and 100.");
        }

        if (MaxBatchesPerPoll is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBatchesPerPoll),
                "Maximum batches per poll must be between 1 and 100.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) ||
            PollInterval > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                "Poll interval must be between 100 milliseconds and one hour.");
        }
    }
}

public sealed class InventoryReservationExpiryProcessor
{
    private readonly InventoryReservationService _reservationService;
    private readonly InventoryReservationExpiryOptions _options;

    public InventoryReservationExpiryProcessor(
        InventoryReservationService reservationService,
        InventoryReservationExpiryOptions options)
    {
        _reservationService = reservationService;
        _options = options;
        _options.Validate();
    }

    public bool IsEnabled => _options.Enabled;

    public Task<int> ExpireBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return Task.FromResult(0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _reservationService.ExpireDueAsync(_options.BatchSize, cancellationToken);
    }
}

public sealed class InventoryReservationExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InventoryReservationExpiryOptions _options;
    private readonly ILogger<InventoryReservationExpiryWorker> _logger;

    public InventoryReservationExpiryWorker(
        IServiceScopeFactory scopeFactory,
        InventoryReservationExpiryOptions options,
        ILogger<InventoryReservationExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Inventory reservation expiry worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Never attach the exception or its message: those may contain database
                // payloads or customer data. Only the stable exception type is logged.
                _logger.LogError(
                    "Inventory reservation expiry poll failed. ExceptionType: {ExceptionType}",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> ProcessPollAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var expiredTotal = 0;
        for (var batch = 0; batch < _options.MaxBatchesPerPoll; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider
                .GetRequiredService<InventoryReservationExpiryProcessor>();
            var expired = await processor.ExpireBatchAsync(cancellationToken);
            expiredTotal += expired;

            if (expired < _options.BatchSize)
            {
                break;
            }
        }

        return expiredTotal;
    }
}
