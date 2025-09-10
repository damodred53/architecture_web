namespace ApiElasticSearch.Worker;

public sealed class Worker : BackgroundService
{
    private readonly IBackgroundTaskQueue _queue;
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public Worker(
        IBackgroundTaskQueue queue,
        ILogger<Worker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background worker démarré");
        await foreach (var workItem in _queue.DequeueAsync(stoppingToken))
        {
            try
            {
            
                using var scope = _scopeFactory.CreateScope();
                var ct = stoppingToken;
                await workItem(ct);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Arrêt du worker demandé.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur dans un job background.");
            }
        }
    }
}