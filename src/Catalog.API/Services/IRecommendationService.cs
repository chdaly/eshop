namespace eShop.Catalog.API.Services;

public interface IRecommendationService
{
    /// <summary>Records that a user viewed a product.</summary>
    Task RecordViewAsync(string userId, int itemId, CancellationToken cancellationToken = default);

    /// <summary>Gets personalized recommendations for a user.</summary>
    Task<PaginatedItems<CatalogItem>> GetRecommendationsAsync(
        string userId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
