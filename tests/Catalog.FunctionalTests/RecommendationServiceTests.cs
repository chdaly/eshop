using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API.Model;
using eShop.Catalog.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace eShop.Catalog.FunctionalTests;

/// <summary>
/// Unit tests for <see cref="RecommendationService"/> using mocked Redis and in-memory CatalogContext.
/// Validates core recommendation logic: view recording, fallback behavior, and exclusion rules.
/// </summary>
public sealed class RecommendationServiceTests : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _redisDb;
    private readonly ICatalogAI _catalogAI;
    private readonly ILogger<RecommendationService> _logger;
    private readonly IOptions<RecommendationOptions> _options;
    private readonly CatalogContext _context;
    private readonly ServiceProvider _serviceProvider;

    public RecommendationServiceTests()
    {
        _redis = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

        _catalogAI = Substitute.For<ICatalogAI>();
        _logger = Substitute.For<ILogger<RecommendationService>>();
        _options = Options.Create(new RecommendationOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new DbContextOptionsBuilder<CatalogContext>()
            .UseInMemoryDatabase(databaseName: $"TestCatalog_{Guid.NewGuid()}")
            .Options);
        services.AddScoped<CatalogContext>(sp =>
            ActivatorUtilities.CreateInstance<TestCatalogContext>(sp));
        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<CatalogContext>();

        SeedTestData();
    }

    private void SeedTestData()
    {
        _context.CatalogItems.AddRange(
            new CatalogItem("Alpine Explorer Tent") { Id = 1, CatalogTypeId = 1, CatalogBrandId = 1, AvailableStock = 10, Price = 199.99m },
            new CatalogItem("Summit Pro Backpack") { Id = 2, CatalogTypeId = 1, CatalogBrandId = 1, AvailableStock = 5, Price = 89.99m },
            new CatalogItem("Trail Runner Shoes") { Id = 3, CatalogTypeId = 2, CatalogBrandId = 2, AvailableStock = 15, Price = 129.99m },
            new CatalogItem("Cascade Rain Jacket") { Id = 4, CatalogTypeId = 2, CatalogBrandId = 2, AvailableStock = 20, Price = 159.99m },
            new CatalogItem("Solar Power Lantern") { Id = 5, CatalogTypeId = 3, CatalogBrandId = 3, AvailableStock = 0, Price = 49.99m }
        );
        _context.SaveChanges();
    }

    private RecommendationService CreateService()
    {
        return new RecommendationService(_redis, _context, _catalogAI, _logger, _options);
    }

    [Fact]
    public async Task RecordViewAsync_ValidItem_StoresInRedis()
    {
        // Arrange
        var service = CreateService();
        var userId = "testuser1";
        var itemId = 1;

        // Act
        await service.RecordViewAsync(userId, itemId, TestContext.Current.CancellationToken);

        // Assert — verify LPUSH was called with the correct key pattern
        await _redisDb.Received(1).ListLeftPushAsync(
            Arg.Is<RedisKey>(k => k.ToString() == $"browsing_history:{userId}"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());

        // Assert — verify LTRIM was called to cap history length
        await _redisDb.Received(1).ListTrimAsync(
            Arg.Is<RedisKey>(k => k.ToString() == $"browsing_history:{userId}"),
            Arg.Any<long>(),
            Arg.Any<long>());

        // Assert — verify EXPIRE was called to set TTL
        await _redisDb.Received(1).KeyExpireAsync(
            Arg.Is<RedisKey>(k => k.ToString() == $"browsing_history:{userId}"),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task GetRecommendationsAsync_NoHistory_ReturnsFallbackItems()
    {
        // Arrange — AI disabled, empty browsing history
        _catalogAI.IsEnabled.Returns(false);
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<RedisValue>());

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("newuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — fallback should return newest items ordered by Id descending
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Fallback should return items even without browsing history");
    }

    [Fact]
    public async Task GetRecommendationsAsync_WithHistory_ExcludesViewedItems()
    {
        // Arrange — simulate browsing history containing items 1 and 2
        _catalogAI.IsEnabled.Returns(false);

        var historyEntries = new RedisValue[]
        {
            "{\"ItemId\":1,\"Timestamp\":\"2026-04-10T14:23:00Z\"}",
            "{\"ItemId\":2,\"Timestamp\":\"2026-04-10T14:20:00Z\"}"
        };
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(historyEntries);

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("historyuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — recommendations must NOT include viewed items (1 and 2)
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.DoesNotContain(result.Data, item => item.Id == 1);
        Assert.DoesNotContain(result.Data, item => item.Id == 2);
    }

    [Fact]
    public async Task GetRecommendationsAsync_AIDisabled_UsesFallback()
    {
        // Arrange — AI is explicitly disabled
        _catalogAI.IsEnabled.Returns(false);

        var historyEntries = new RedisValue[]
        {
            "{\"ItemId\":1,\"Timestamp\":\"2026-04-10T14:23:00Z\"}"
        };
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(historyEntries);

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("fallbackuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — should return recommendations using fallback algorithm (not AI-based)
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Fallback should return recommendations even without AI");

        // Verify AI embedding was never invoked
        await _catalogAI.DidNotReceive().GetEmbeddingAsync(Arg.Any<string>());
        await _catalogAI.DidNotReceive().GetEmbeddingAsync(Arg.Any<CatalogItem>());
    }

    [Fact]
    public async Task GetRecommendationsAsync_ExcludesOutOfStockItems()
    {
        // Arrange
        _catalogAI.IsEnabled.Returns(false);
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<RedisValue>());

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("stocktestuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — Item 5 has 0 stock and should be excluded
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.DoesNotContain(result.Data, item => item.Id == 5); // Item 5 has AvailableStock = 0
        Assert.All(result.Data, item => Assert.True(item.AvailableStock > 0, $"Item {item.Id} should have stock > 0"));
    }

    [Fact]
    public async Task GetRecommendationsAsync_PaginationCorrectlyApplied()
    {
        // Arrange
        _catalogAI.IsEnabled.Returns(false);
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<RedisValue>());

        var service = CreateService();

        // Act — page 0, size 2
        var page0 = await service.GetRecommendationsAsync("paginationuser", 0, 2, TestContext.Current.CancellationToken);
        // Act — page 1, size 2
        var page1 = await service.GetRecommendationsAsync("paginationuser", 1, 2, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(page0);
        Assert.NotNull(page1);
        var page0Count = page0.Data.Count();
        var page1Count = page1.Data.Count();
        Assert.True(page0Count <= 2, "Page 0 should have at most 2 items");
        Assert.True(page1Count <= 2, "Page 1 should have at most 2 items");
        Assert.Equal(0, page0.PageIndex);
        Assert.Equal(1, page1.PageIndex);
        Assert.Equal(2, page0.PageSize);
        Assert.Equal(2, page1.PageSize);
        
        // Verify items are different (if both pages have data)
        if (page0.Data.Any() && page1.Data.Any())
        {
            var page0Ids = page0.Data.Select(i => i.Id).ToHashSet();
            var page1Ids = page1.Data.Select(i => i.Id).ToHashSet();
            Assert.False(page0Ids.Overlaps(page1Ids), "Pages should contain different items");
        }
    }

    [Fact]
    public async Task GetRecommendationsAsync_OutOfBoundsPagination_ReturnsEmptyList()
    {
        // Arrange
        _catalogAI.IsEnabled.Returns(false);
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<RedisValue>());

        var service = CreateService();

        // Act — request page far beyond available data
        var result = await service.GetRecommendationsAsync("emptypageuser", 9999, 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task RecordViewAsync_ObservesHistoryCapLimit()
    {
        // Arrange
        var service = CreateService();
        var userId = "captestuser";
        var config = _options.Value;

        // Act — record a view
        await service.RecordViewAsync(userId, 1, TestContext.Current.CancellationToken);

        // Assert — verify LTRIM called with MaxHistoryLength - 1
        await _redisDb.Received(1).ListTrimAsync(
            Arg.Is<RedisKey>(k => k.ToString() == $"browsing_history:{userId}"),
            0,
            config.MaxHistoryLength - 1);
    }

    [Fact]
    public async Task RecordViewAsync_SetsTtlCorrectly()
    {
        // Arrange
        var service = CreateService();
        var userId = "ttltestuser";
        var config = _options.Value;

        // Act
        await service.RecordViewAsync(userId, 1, TestContext.Current.CancellationToken);

        // Assert — verify EXPIRE called with correct TTL
        await _redisDb.Received(1).KeyExpireAsync(
            Arg.Is<RedisKey>(k => k.ToString() == $"browsing_history:{userId}"),
            Arg.Is<TimeSpan?>(ts => ts.HasValue && ts.Value == TimeSpan.FromDays(config.HistoryTtlDays)));
    }

    [Fact]
    public async Task GetRecommendationsAsync_FallsBackToNewestWhenNoTypeMatch()
    {
        // Arrange — user viewed item with non-existent type
        _catalogAI.IsEnabled.Returns(false);
        var historyEntries = new RedisValue[]
        {
            "{\"ItemId\":99999,\"Timestamp\":\"2026-04-10T14:23:00Z\"}" // Non-existent item
        };
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(historyEntries);

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("notypeuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — should fall back to newest items
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Should return newest items when no type match");
    }

    [Fact]
    public async Task GetRecommendationsAsync_CatalogTypeFallback_ReturnsMatchingCategory()
    {
        // Arrange — AI disabled, user viewed item 1 (CatalogTypeId = 1)
        _catalogAI.IsEnabled.Returns(false);
        var historyEntries = new RedisValue[]
        {
            "{\"ItemId\":1,\"Timestamp\":\"2026-04-10T14:23:00Z\"}"
        };
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(historyEntries);

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("typematchuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — should return items from same category (CatalogTypeId = 1)
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        if (result.Data.Any())
        {
            // Item 2 has same CatalogTypeId (1) and is in stock, should be included
            Assert.Contains(result.Data, item => item.Id == 2);
        }
    }

    [Fact]
    public async Task GetRecommendationsAsync_RespectsConfiguredMaxHistoryLength()
    {
        // Arrange — configure custom MaxHistoryLength
        var customOptions = Options.Create(new RecommendationOptions { MaxHistoryLength = 10 });
        var service = new RecommendationService(_redis, _context, _catalogAI, _logger, customOptions);

        // Act — record view
        await service.RecordViewAsync("configuser", 1, TestContext.Current.CancellationToken);

        // Assert — verify LTRIM called with custom limit
        await _redisDb.Received(1).ListTrimAsync(
            Arg.Any<RedisKey>(),
            0,
            9); // MaxHistoryLength - 1
    }

    [Fact]
    public async Task GetRecommendationsAsync_RedisFailure_ReturnsNewestItems()
    {
        // Arrange — Redis throws exception
        _catalogAI.IsEnabled.Returns(false);
        _redisDb.ListRangeAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns<RedisValue[]>(_ => throw new RedisException("Redis connection failed"));

        var service = CreateService();

        // Act
        var result = await service.GetRecommendationsAsync("redisfailuser", 0, 10, TestContext.Current.CancellationToken);

        // Assert — should gracefully fall back to newest items
        Assert.NotNull(result);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.Any(), "Should return newest items when Redis fails");
    }

    [Fact]
    public async Task RecordViewAsync_InvalidUserId_ThrowsArgumentException()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordViewAsync("bad/user", 1, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        _context.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// Test-specific CatalogContext that excludes the pgvector Embedding property,
    /// which is not supported by the InMemory database provider.
    /// </summary>
    private class TestCatalogContext(DbContextOptions<CatalogContext> options, IConfiguration config)
        : CatalogContext(options, config)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<CatalogItem>().Ignore(c => c.Embedding);
        }
    }
}
