using System.Threading.Channels;

namespace eShop.Catalog.API.Services;

public sealed record ViewTrackingWorkItem(string UserId, int ItemId);

public interface IViewTrackingQueue
{
    ValueTask QueueAsync(ViewTrackingWorkItem workItem, CancellationToken cancellationToken = default);
}

public sealed class ViewTrackingBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<ViewTrackingBackgroundService> logger) : IHostedService, IAsyncDisposable, IViewTrackingQueue
{
    private readonly Channel<ViewTrackingWorkItem> _channel = Channel.CreateBounded<ViewTrackingWorkItem>(new BoundedChannelOptions(512)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private Task? _processingTask;

    public ValueTask QueueAsync(ViewTrackingWorkItem workItem, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(workItem, cancellationToken);

    internal ValueTask QueueAsync(string userId, int itemId, CancellationToken cancellationToken = default) =>
        QueueAsync(new ViewTrackingWorkItem(userId, itemId), cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processingTask = ProcessQueueAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        if (_processingTask is not null)
        {
            await _processingTask.WaitAsync(cancellationToken);
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var workItem in _channel.Reader.ReadAllAsync())
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var recommendationService = scope.ServiceProvider.GetRequiredService<IRecommendationService>();
                await recommendationService.RecordViewAsync(workItem.UserId, workItem.ItemId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to process queued product view for user {UserId}, item {ItemId}",
                    CatalogSecurity.FormatUserIdForLogging(workItem.UserId),
                    workItem.ItemId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
