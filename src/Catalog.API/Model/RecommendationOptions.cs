namespace eShop.Catalog.API.Model;

public class RecommendationOptions
{
    public int MaxHistoryLength { get; set; } = 50;
    public int HistoryTtlDays { get; set; } = 30;
    public int CentroidSampleSize { get; set; } = 10;
}
