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
}
