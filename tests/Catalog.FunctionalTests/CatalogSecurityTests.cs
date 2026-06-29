using System.Net;
using System.Net.Http.Json;
using System.Dynamic;
using System.IO;
using System.Threading;
using System.Text.Json;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Catalog.API;
using eShop.Catalog.API.Model;
using Microsoft.AspNetCore.Mvc.Testing;

namespace eShop.Catalog.FunctionalTests;

[Trait("Category", "Security")]
public sealed class CatalogApiSecurityUnitTests
{
    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("..\\appsettings.json")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\loot.png")]
    public void ResolvePicturePath_TraversalAndAbsolutePaths_AreRejected(string pictureFileName)
    {
        var method = SecurityTestSupport.GetRequiredStaticMethod(typeof(CatalogApi), "ResolvePicturePath");

        var exception = Record.Exception(() => SecurityTestSupport.InvokeStatic(method, @"C:\catalog-root", pictureFileName));

        Assert.NotNull(exception);
    }

    [Fact]
    public void ResolvePicturePath_ValidFileName_RemainsUnderPicsDirectory()
    {
        var method = SecurityTestSupport.GetRequiredStaticMethod(typeof(CatalogApi), "ResolvePicturePath");

        var resolvedPath = Assert.IsType<string>(SecurityTestSupport.InvokeStatic(method, @"C:\catalog-root", "product1.png"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(@"C:\catalog-root", "Pics", "product1.png")),
            Path.GetFullPath(resolvedPath));
    }

    [Fact]
    public void EmitSearchDiagnostics_LongSearchText_IsSanitizedInLogOutput()
    {
        var logger = new TestLogger();
        var method = SecurityTestSupport.GetRequiredStaticMethod(typeof(CatalogApi), "EmitSearchDiagnostics");
        var longSearchText = string.Concat(Enumerable.Repeat("sensitive-search-fragment-", 32)) + Environment.NewLine + "credit-card=4111111111111111";
        dynamic result = new ExpandoObject();
        result.Item = new CatalogItem("Alpine Explorer Tent");
        result.Distance = 0.0042d;
        IEnumerable<dynamic> results = [result];

        SecurityTestSupport.InvokeStatic(method, logger, longSearchText, results);

        var logEntry = Assert.Single(logger.Entries);
        Assert.DoesNotContain(longSearchText, logEntry.Message);
        Assert.DoesNotContain("4111111111111111", logEntry.Message);
        Assert.Contains("Alpine Explorer Tent", logEntry.Message);
    }
}

[Trait("Category", "Security")]
public sealed class CatalogSecurityTests : IClassFixture<CatalogApiFixture>
{
    private static int s_nextCatalogItemId = 500_000;

    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public CatalogSecurityTests(CatalogApiFixture fixture)
    {
        _webApplicationFactory = fixture;
    }

    [Fact]
    public async Task UpdateCatalogItem_OnlyMutatesWhitelistedFields()
    {
        var httpClient = CreateAuthenticatedClient(new ApiVersion(2.0));
        var itemId = Interlocked.Increment(ref s_nextCatalogItemId);
        var createdItem = new
        {
            Id = itemId,
            Name = "Security Test Item",
            Description = "Original description",
            Price = 15.25m,
            PictureFileName = "original.png",
            CatalogTypeId = 1,
            CatalogBrandId = 1,
            AvailableStock = 12,
            RestockThreshold = 3,
            MaxStockThreshold = 20,
            OnReorder = false
        };

        try
        {
            var createResponse = await httpClient.PostAsJsonAsync("/api/catalog/items", createdItem, TestContext.Current.CancellationToken);
            createResponse.EnsureSuccessStatusCode();

            var updatePayload = new
            {
                Id = itemId + 1,
                Name = "Security Test Item Updated",
                Description = "Updated description",
                Price = 99.95m,
                PictureFileName = "updated.png",
                CatalogTypeId = 2,
                CatalogBrandId = 2,
                AvailableStock = 999,
                RestockThreshold = 444,
                MaxStockThreshold = 1_111,
                OnReorder = true
            };

            var updateResponse = await httpClient.PutAsJsonAsync($"/api/catalog/items/{itemId}", updatePayload, TestContext.Current.CancellationToken);
            updateResponse.EnsureSuccessStatusCode();

            var getResponse = await httpClient.GetAsync($"/api/catalog/items/{itemId}", TestContext.Current.CancellationToken);
            getResponse.EnsureSuccessStatusCode();
            var body = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var updatedItem = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

            Assert.NotNull(updatedItem);
            Assert.Equal(itemId, updatedItem.Id);
            Assert.Equal(updatePayload.Name, updatedItem.Name);
            Assert.Equal(updatePayload.Description, updatedItem.Description);
            Assert.Equal(updatePayload.Price, updatedItem.Price);
            Assert.Equal(updatePayload.PictureFileName, updatedItem.PictureFileName);
            Assert.Equal(updatePayload.CatalogTypeId, updatedItem.CatalogTypeId);
            Assert.Equal(updatePayload.CatalogBrandId, updatedItem.CatalogBrandId);
            Assert.Equal(createdItem.AvailableStock, updatedItem.AvailableStock);
            Assert.Equal(createdItem.RestockThreshold, updatedItem.RestockThreshold);
            Assert.Equal(createdItem.MaxStockThreshold, updatedItem.MaxStockThreshold);
            Assert.Equal(createdItem.OnReorder, updatedItem.OnReorder);

            var overwrittenItemResponse = await httpClient.GetAsync($"/api/catalog/items/{itemId + 1}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, overwrittenItemResponse.StatusCode);
        }
        finally
        {
            await httpClient.DeleteAsync($"/api/catalog/items/{itemId}", TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task SemanticSearch_WithLongInput_StillReturnsSuccessfulResponse(double version)
    {
        var httpClient = CreateHttpClient(new ApiVersion(version));
        var searchText = new string('W', 512);

        var response = version switch
        {
            1.0 => await httpClient.GetAsync($"api/catalog/items/withsemanticrelevance/{searchText}?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await httpClient.GetAsync($"api/catalog/items/withsemanticrelevance?text={searchText}&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
    }

    private HttpClient CreateHttpClient(ApiVersion apiVersion)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), apiVersion);
        return _webApplicationFactory.CreateDefaultClient(handler);
    }

    private HttpClient CreateAuthenticatedClient(ApiVersion apiVersion, string userId = AutoAuthorizeMiddleware.IDENTITY_ID)
    {
        var client = CreateHttpClient(apiVersion);
        client.DefaultRequestHeaders.Add(AutoAuthorizeMiddleware.UserIdHeaderName, userId);
        return client;
    }
}
