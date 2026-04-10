using System.Text.Json;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using StackExchange.Redis;

namespace eShop.Catalog.API.Services;

public sealed class RecommendationService(
    IConnectionMultiplexer redis,
    CatalogContext context,
    ICatalogAI catalogAI,
    ILogger<RecommendationService> logger,
    IOptions<RecommendationOptions> options) : IRecommendationService
{
    private const int EmbeddingDimensions = 384;
    private const string KeyPrefix = "browsing_history:";

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordViewAsync(string userId, int itemId, CancellationToken cancellationToken = default)
    {
        var key = $"{KeyPrefix}{userId}";
        var entry = JsonSerializer.Serialize(new BrowsingHistoryItem(itemId, DateTime.UtcNow), s_jsonOptions);
        var config = options.Value;

        try
        {
            var db = redis.GetDatabase();
            await db.ListLeftPushAsync(key, entry);
            await db.ListTrimAsync(key, 0, config.MaxHistoryLength - 1);
            await db.KeyExpireAsync(key, TimeSpan.FromDays(config.HistoryTtlDays));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record browsing history for user {UserId}, item {ItemId}", userId, itemId);
        }
    }

    public async Task<PaginatedItems<CatalogItem>> GetRecommendationsAsync(
        string userId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        List<BrowsingHistoryItem> history;

        try
        {
            history = await GetBrowsingHistoryAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch browsing history for user {UserId}, returning newest items", userId);
            return await GetNewestItemsAsync(pageIndex, pageSize, [], cancellationToken);
        }

        if (history.Count == 0)
        {
            return await GetNewestItemsAsync(pageIndex, pageSize, [], cancellationToken);
        }

        var allViewedItemIds = history.Select(h => h.ItemId).Distinct().ToList();

        if (catalogAI.IsEnabled)
        {
            try
            {
                return await GetAIRecommendationsAsync(history, allViewedItemIds, pageIndex, pageSize, config, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "AI recommendation failed for user {UserId}, falling back to type-based", userId);
            }
        }

        return await GetFallbackRecommendationsAsync(history, allViewedItemIds, pageIndex, pageSize, cancellationToken);
    }

    private async Task<List<BrowsingHistoryItem>> GetBrowsingHistoryAsync(string userId)
    {
        var key = $"{KeyPrefix}{userId}";
        var db = redis.GetDatabase();
        var values = await db.ListRangeAsync(key, 0, options.Value.MaxHistoryLength - 1);

        var history = new List<BrowsingHistoryItem>();
        foreach (var value in values)
        {
            var item = JsonSerializer.Deserialize<BrowsingHistoryItem>((string)value!, s_jsonOptions);
            if (item is not null)
            {
                history.Add(item);
            }
        }

        return history;
    }

    private async Task<PaginatedItems<CatalogItem>> GetAIRecommendationsAsync(
        List<BrowsingHistoryItem> history,
        List<int> allViewedItemIds,
        int pageIndex,
        int pageSize,
        RecommendationOptions config,
        CancellationToken cancellationToken)
    {
        // Get the last N item IDs for centroid calculation
        var sampleItemIds = history
            .Select(h => h.ItemId)
            .Distinct()
            .Take(config.CentroidSampleSize)
            .ToList();

        // Fetch embeddings for sample items
        var embeddings = await context.CatalogItems
            .Where(c => sampleItemIds.Contains(c.Id) && c.Embedding != null)
            .Select(c => c.Embedding!)
            .ToListAsync(cancellationToken);

        if (embeddings.Count == 0)
        {
            return await GetFallbackRecommendationsAsync(history, allViewedItemIds, pageIndex, pageSize, cancellationToken);
        }

        // Compute centroid (component-wise average)
        var centroid = new float[EmbeddingDimensions];
        foreach (var embedding in embeddings)
        {
            var vec = embedding.ToArray();
            for (int i = 0; i < EmbeddingDimensions && i < vec.Length; i++)
            {
                centroid[i] += vec[i];
            }
        }
        for (int i = 0; i < EmbeddingDimensions; i++)
        {
            centroid[i] /= embeddings.Count;
        }
        var centroidVector = new Vector(centroid);

        // Query items by cosine similarity, excluding viewed and out-of-stock
        var totalItems = await context.CatalogItems
            .Where(c => c.Embedding != null)
            .Where(c => !allViewedItemIds.Contains(c.Id))
            .Where(c => c.AvailableStock > 0)
            .LongCountAsync(cancellationToken);

        var items = await context.CatalogItems
            .Where(c => c.Embedding != null)
            .Where(c => !allViewedItemIds.Contains(c.Id))
            .Where(c => c.AvailableStock > 0)
            .OrderBy(c => c.Embedding!.CosineDistance(centroidVector))
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, items);
    }

    private async Task<PaginatedItems<CatalogItem>> GetFallbackRecommendationsAsync(
        List<BrowsingHistoryItem> history,
        List<int> allViewedItemIds,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Get the most recently viewed item's catalog type
        var mostRecentItemId = history[0].ItemId;
        var recentItemType = await context.CatalogItems
            .Where(c => c.Id == mostRecentItemId)
            .Select(c => c.CatalogTypeId)
            .FirstOrDefaultAsync(cancellationToken);

        if (recentItemType == 0)
        {
            return await GetNewestItemsAsync(pageIndex, pageSize, allViewedItemIds, cancellationToken);
        }

        var query = context.CatalogItems
            .Where(c => c.CatalogTypeId == recentItemType)
            .Where(c => !allViewedItemIds.Contains(c.Id))
            .Where(c => c.AvailableStock > 0);

        var totalItems = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.Id)
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, items);
    }

    private async Task<PaginatedItems<CatalogItem>> GetNewestItemsAsync(
        int pageIndex,
        int pageSize,
        List<int> excludeItemIds,
        CancellationToken cancellationToken)
    {
        var query = context.CatalogItems
            .Where(c => c.AvailableStock > 0);

        if (excludeItemIds.Count > 0)
        {
            query = query.Where(c => !excludeItemIds.Contains(c.Id));
        }

        var totalItems = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(c => c.Id)
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, items);
    }
}
