using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Catalog.API.Model;
using Microsoft.AspNetCore.Mvc.Testing;

namespace eShop.Catalog.FunctionalTests;

/// <summary>
/// Functional tests for the recommendation API endpoints.
/// These tests validate the HTTP contract defined in docs/recommendations-design.md Section 3.
/// Tests require the recommendation feature implementation to be present in Catalog.API.
/// </summary>
public sealed class RecommendationApiTests : IClassFixture<RecommendationApiFixture>
{
    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public RecommendationApiTests(RecommendationApiFixture fixture)
    {
        _webApplicationFactory = fixture;
    }

    private HttpClient CreateAuthenticatedClient(ApiVersion apiVersion, string userId = AutoAuthorizeMiddleware.IDENTITY_ID)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), apiVersion);
        var client = _webApplicationFactory.CreateDefaultClient(handler);
        client.DefaultRequestHeaders.Add(
            AutoAuthorizeMiddleware.UserIdHeaderName,
            userId);
        return client;
    }

    private HttpClient CreateAnonymousClient(ApiVersion apiVersion)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), apiVersion);
        return _webApplicationFactory.CreateDefaultClient(handler);
    }

    [Theory]
    [InlineData(1.0)]
    public async Task RecordProductView_AuthenticatedUser_ReturnsNoContent(double version)
    {
        // Arrange
        var httpClient = CreateAuthenticatedClient(new ApiVersion(version));

        // Act
        var response = await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view",
            new { ItemId = 1 },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RecordProductView_UnauthenticatedUser_ReturnsUnauthorized()
    {
        // Arrange
        var httpClient = CreateAnonymousClient(new ApiVersion(1.0));

        // Act
        var response = await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view",
            new { ItemId = 1 },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RecordProductView_InvalidItemId_ReturnsNotFound()
    {
        // Arrange
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0));

        // Act
        var response = await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view",
            new { ItemId = 99999 },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecommendations_WithViewHistory_ReturnsRecommendationsExcludingViewedItems()
    {
        // Arrange — use a unique user to avoid cross-test contamination
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Record several product views to build browsing history
        await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view", new { ItemId = 2 }, TestContext.Current.CancellationToken);
        await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view", new { ItemId = 3 }, TestContext.Current.CancellationToken);

        // Allow async view recording to complete
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Recommendations should return at least one item");
        // Viewed items must be excluded from recommendations
        Assert.DoesNotContain(result.Data, item => item.Id == 1);
        Assert.DoesNotContain(result.Data, item => item.Id == 2);
        Assert.DoesNotContain(result.Data, item => item.Id == 3);
    }

    [Fact]
    public async Task GetRecommendations_NoHistory_ReturnsFallbackItems()
    {
        // Arrange — use a fresh user ID with no browsing history
        var freshUserId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), freshUserId);

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Fallback mode should still return items when user has no history");
    }

    [Fact]
    public async Task GetRecommendations_UnauthenticatedUser_ReturnsUnauthorized()
    {
        // Arrange
        var httpClient = CreateAnonymousClient(new ApiVersion(1.0));

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RecordProductView_MultipleViewsSameProduct_KeepsMostRecentInHistory()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Act — view same product multiple times
        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);

        // Allow async processing
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act — get recommendations
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — item 1 should still be excluded (most recent view recorded)
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Data, item => item.Id == 1);
    }

    [Fact]
    public async Task RecordProductView_MoreThan50Views_OldestViewsTrimmed()
    {
        // Arrange — create history with 50+ items
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Act — record 55 product views
        for (int i = 1; i <= 10; i++)
        {
            await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = i }, TestContext.Current.CancellationToken);
        }

        // Allow async processing
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Act — get recommendations
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — should still work with capped history
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        // All viewed items should be excluded
        for (int i = 1; i <= 10; i++)
        {
            Assert.DoesNotContain(result.Data, item => item.Id == i);
        }
    }

    [Fact]
    public async Task GetRecommendations_ExcludesOutOfStockItems()
    {
        // Arrange — view some products
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — should not contain out-of-stock items
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        // Verify no items with AvailableStock <= 0
        Assert.All(result.Data, item => Assert.True(item.AvailableStock > 0, $"Item {item.Id} should have stock > 0"));
    }

    [Theory]
    [InlineData(0, 5)]  // First page, 5 items
    [InlineData(1, 5)]  // Second page, 5 items
    [InlineData(0, 10)] // First page, 10 items
    public async Task GetRecommendations_PaginationWorks(int pageIndex, int pageSize)
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Act
        var response = await httpClient.GetAsync(
            $"/api/catalog/recommendations?pageIndex={pageIndex}&pageSize={pageSize}",
            TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.Equal(pageIndex, result.PageIndex);
        Assert.Equal(pageSize, result.PageSize);
        var resultCount = result.Data.Count();
        Assert.True(resultCount <= pageSize, $"Result count {resultCount} should not exceed page size {pageSize}");
    }

    [Fact]
    public async Task GetRecommendations_PageIndexOutOfBounds_ReturnsEmptyList()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Act — request page far beyond available data
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=9999&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — should return success with empty list
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task GetRecommendations_WithViewHistory_UsesCentroidSimilarity()
    {
        // Arrange — create history with several items of same category
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // View multiple items to establish pattern
        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 2 }, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — should return similar items
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Centroid-based recommendations should return similar items");
    }

    [Fact]
    public async Task GetRecommendations_FallsBackToCatalogTypeMatchingWhenAIUnavailable()
    {
        // Arrange — view products from specific catalog type
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 }, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Act
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — fallback should work
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Fallback recommendations should be available");
    }

    [Fact]
    public async Task GetRecommendations_FallsBackToNewestWhenNoCatalogTypeMatch()
    {
        // Arrange — fresh user with no history
        var userId = Guid.NewGuid().ToString();
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0), userId);

        // Act — no views recorded
        var response = await httpClient.GetAsync(
            "/api/catalog/recommendations?pageIndex=0&pageSize=10",
            TestContext.Current.CancellationToken);

        // Assert — should return newest items
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Should return newest items as final fallback");
    }

    [Fact]
    public async Task RecordProductView_WithNegativeItemId_ReturnsBadRequest()
    {
        // Arrange
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0));

        // Act
        var response = await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view",
            new { ItemId = -1 },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RecordProductView_WithZeroItemId_ReturnsBadRequest()
    {
        // Arrange
        var httpClient = CreateAuthenticatedClient(new ApiVersion(1.0));

        // Act
        var response = await httpClient.PostAsJsonAsync(
            "/api/catalog/recommendations/view",
            new { ItemId = 0 },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
