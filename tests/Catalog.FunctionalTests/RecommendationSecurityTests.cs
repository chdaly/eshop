#nullable enable
using System.Reflection;
using System.Threading;
using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API.Model;
using eShop.Catalog.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace eShop.Catalog.FunctionalTests;

[Trait("Category", "Security")]
public sealed class RecommendationSecurityTests : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _redisDb;
    private readonly ICatalogAI _catalogAI;
    private readonly TestLogger<RecommendationService> _logger;
    private readonly IOptions<RecommendationOptions> _options;
    private readonly CatalogContext _context;
    private readonly ServiceProvider _serviceProvider;

    public RecommendationSecurityTests()
    {
        _redis = Substitute.For<IConnectionMultiplexer>();
        _redisDb = Substitute.For<IDatabase>();
        _redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redisDb);

        _catalogAI = Substitute.For<ICatalogAI>();
        _logger = new TestLogger<RecommendationService>();
        _options = Options.Create(new RecommendationOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new DbContextOptionsBuilder<CatalogContext>()
            .UseInMemoryDatabase($"RecommendationSecurity_{Guid.NewGuid()}")
            .Options);
        services.AddScoped<CatalogContext>(provider => ActivatorUtilities.CreateInstance<SecurityTestCatalogContext>(provider));
        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<CatalogContext>();

        SeedCatalog();
    }

    [Theory]
    [InlineData("user:admin")]
    [InlineData("user\nadmin")]
    [InlineData("user\r\nadmin")]
    [InlineData("user admin")]
    [InlineData("user|admin")]
    public async Task RecordViewAsync_InvalidUserId_IsRejected(string userId)
    {
        var service = CreateService();

        var exception = await Record.ExceptionAsync(() => service.RecordViewAsync(userId, 1, TestContext.Current.CancellationToken));

        Assert.NotNull(exception);
        Assert.DoesNotContain(_redisDb.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IDatabase.ListLeftPushAsync));
    }

    [Theory]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF")]
    [InlineData("AlphaNumericUser123")]
    public async Task RecordViewAsync_ValidUserId_IsAccepted(string userId)
    {
        var service = CreateService();

        await service.RecordViewAsync(userId, 1, TestContext.Current.CancellationToken);

        await _redisDb.Received(1).ListLeftPushAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"browsing_history:{userId}"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public void UserIdLoggingHelper_MasksIdsWhileKeepingDebugPrefix()
    {
        var method = TryGetUserIdLogHelper(typeof(RecommendationService))
            ?? SecurityTestSupport.GetRequiredStaticMethod(typeof(CatalogSecurity), "FormatUserIdForLogging");
        const string userId = "ABCDEF12-3456-7890-ABCD-EF1234567890";

        var maskedUserId = Assert.IsType<string>(SecurityTestSupport.InvokeStatic(method, userId));

        Assert.StartsWith("ABCDEF12", maskedUserId, StringComparison.Ordinal);
        Assert.NotEqual(userId, maskedUserId);
        Assert.DoesNotContain("3456-7890-ABCD-EF1234567890", maskedUserId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewTrackingBackgroundService_QueuedItems_AreProcessed()
    {
        await using var harness = CreateBackgroundServiceHarness(TimeSpan.Zero);

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);
        await harness.QueueAsync("user1", 1, TestContext.Current.CancellationToken);
        await harness.QueueAsync("user2", 2, TestContext.Current.CancellationToken);
        await harness.Recorder.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
        await harness.Service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(harness.Recorder.RecordedViews, view => view == ("user1", 1));
        Assert.Contains(harness.Recorder.RecordedViews, view => view == ("user2", 2));
    }

    [Fact]
    public async Task ViewTrackingBackgroundService_StopAsync_DrainsPendingItems()
    {
        await using var harness = CreateBackgroundServiceHarness(TimeSpan.FromMilliseconds(100));

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);
        await harness.QueueAsync("shutdownuser", 10, TestContext.Current.CancellationToken);
        await harness.QueueAsync("shutdownuser", 11, TestContext.Current.CancellationToken);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await harness.Service.StopAsync(stopCts.Token);

        Assert.Contains(harness.Recorder.RecordedViews, view => view == ("shutdownuser", 10));
        Assert.Contains(harness.Recorder.RecordedViews, view => view == ("shutdownuser", 11));
    }

    public void Dispose()
    {
        _context.Dispose();
        _serviceProvider.Dispose();
    }

    private RecommendationService CreateService()
        => new(_redis, _context, _catalogAI, _logger, _options);

    private void SeedCatalog()
    {
        _context.CatalogItems.AddRange(
            new CatalogItem("Alpine Explorer Tent") { Id = 1, CatalogTypeId = 1, CatalogBrandId = 1, AvailableStock = 10, Price = 199.99m },
            new CatalogItem("Summit Pro Backpack") { Id = 2, CatalogTypeId = 1, CatalogBrandId = 1, AvailableStock = 5, Price = 89.99m },
            new CatalogItem("Trail Runner Shoes") { Id = 3, CatalogTypeId = 2, CatalogBrandId = 2, AvailableStock = 15, Price = 129.99m });
        _context.SaveChanges();
    }

    private static ViewTrackingHarness CreateBackgroundServiceHarness(TimeSpan processingDelay)
    {
        var serviceType = typeof(RecommendationService).Assembly.GetTypes().SingleOrDefault(type => type.Name == "ViewTrackingBackgroundService")
            ?? throw new Xunit.Sdk.XunitException("Expected ViewTrackingBackgroundService to exist in Catalog.API.");

        if (!typeof(IHostedService).IsAssignableFrom(serviceType))
        {
            throw new Xunit.Sdk.XunitException("ViewTrackingBackgroundService must implement IHostedService.");
        }

        var recorder = new SpyRecommendationService(processingDelay);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRecommendationService>(recorder);
        RegisterOptionalConstructorDependencies(serviceType, services);

        var serviceProvider = services.BuildServiceProvider();
        var serviceInstance = ActivatorUtilities.CreateInstance(serviceProvider, serviceType);
        var queueMethod = GetQueueMethod(serviceType);

        return new ViewTrackingHarness(
            serviceProvider,
            (IHostedService)serviceInstance,
            recorder,
            (userId, itemId, cancellationToken) => InvokeQueueMethodAsync(queueMethod, serviceInstance, userId, itemId, cancellationToken));
    }

    private static void RegisterOptionalConstructorDependencies(Type serviceType, IServiceCollection services)
    {
        var constructor = serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .FirstOrDefault();

        if (constructor is null)
        {
            throw new Xunit.Sdk.XunitException("ViewTrackingBackgroundService must expose a constructor.");
        }

        foreach (var parameter in constructor.GetParameters())
        {
            if (services.Any(descriptor => descriptor.ServiceType == parameter.ParameterType))
            {
                continue;
            }

            if (parameter.ParameterType == typeof(TimeProvider))
            {
                services.AddSingleton(TimeProvider.System);
                continue;
            }

            if (parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(System.Threading.Channels.Channel<>))
            {
                var workItemType = parameter.ParameterType.GetGenericArguments()[0];
                var channelFactory = typeof(System.Threading.Channels.Channel)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(method => method.Name == "CreateBounded"
                        && method.IsGenericMethodDefinition
                        && method.GetParameters() is [{ ParameterType: var firstParameterType }]
                        && firstParameterType == typeof(int));
                var channelInstance = channelFactory.MakeGenericMethod(workItemType).Invoke(null, [512])!;
                services.AddSingleton(parameter.ParameterType, channelInstance);
                continue;
            }

            if (parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IOptions<>))
            {
                var optionType = parameter.ParameterType.GetGenericArguments()[0];
                var optionValue = Activator.CreateInstance(optionType)
                    ?? throw new Xunit.Sdk.XunitException($"Could not instantiate {optionType.FullName} for background service testing.");
                var optionsInstance = typeof(Options)
                    .GetMethod(nameof(Options.Create))!
                    .MakeGenericMethod(optionType)
                    .Invoke(null, [optionValue])!;
                services.AddSingleton(parameter.ParameterType, optionsInstance);
            }
        }
    }

    private static MethodInfo GetQueueMethod(Type serviceType)
    {
        var stringQueueMethod = serviceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!candidate.Name.Contains("queue", StringComparison.OrdinalIgnoreCase)
                    && !candidate.Name.Contains("enqueue", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length is 2 or 3
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(int)
                    && (parameters.Length == 2 || parameters[2].ParameterType == typeof(CancellationToken));
            });

        if (stringQueueMethod is not null)
        {
            return stringQueueMethod;
        }

        var workItemQueueMethod = serviceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!candidate.Name.Contains("queue", StringComparison.OrdinalIgnoreCase)
                    && !candidate.Name.Contains("enqueue", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length is 1 or 2
                    && parameters[0].ParameterType.Name == "ViewTrackingWorkItem"
                    && (parameters.Length == 1 || parameters[1].ParameterType == typeof(CancellationToken));
            });

        return workItemQueueMethod ?? throw new Xunit.Sdk.XunitException("Expected ViewTrackingBackgroundService to expose a queue/enqueue method for either (string userId, int itemId[, CancellationToken]) or ViewTrackingWorkItem.");
    }

    private static async Task InvokeQueueMethodAsync(MethodInfo method, object instance, string userId, int itemId, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        object?[] args = parameters switch
        {
            [{ ParameterType: var firstParameterType }, _, _] when firstParameterType == typeof(string) => [userId, itemId, cancellationToken],
            [{ ParameterType: var firstParameterType }, _] when firstParameterType == typeof(string) => [userId, itemId],
            [{ ParameterType: var firstParameterType }, _] => [Activator.CreateInstance(firstParameterType, userId, itemId)!, cancellationToken],
            [{ ParameterType: var firstParameterType }] => [Activator.CreateInstance(firstParameterType, userId, itemId)!],
            _ => throw new Xunit.Sdk.XunitException("Queue method signature was not recognized.")
        };
        var result = method.Invoke(instance, args);

        switch (result)
        {
            case null:
                return;
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                throw new Xunit.Sdk.XunitException($"Queue method returned unsupported type {result.GetType().FullName}.");
        }
    }

    private static MethodInfo? TryGetUserIdLogHelper(Type type)
    {
        return type
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (candidate.ReturnType != typeof(string))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                {
                    return false;
                }

                return candidate.Name.Contains("user", StringComparison.OrdinalIgnoreCase)
                    && (candidate.Name.Contains("mask", StringComparison.OrdinalIgnoreCase)
                        || candidate.Name.Contains("sanitize", StringComparison.OrdinalIgnoreCase)
                        || candidate.Name.Contains("safe", StringComparison.OrdinalIgnoreCase));
            });
    }

    private sealed class ViewTrackingHarness(
        ServiceProvider serviceProvider,
        IHostedService service,
        SpyRecommendationService recorder,
        Func<string, int, CancellationToken, Task> queueAsync) : IAsyncDisposable
    {
        public ServiceProvider ServiceProvider { get; } = serviceProvider;
        public IHostedService Service { get; } = service;
        public SpyRecommendationService Recorder { get; } = recorder;
        public Func<string, int, CancellationToken, Task> QueueAsync { get; } = queueAsync;

        public ValueTask DisposeAsync()
        {
            ServiceProvider.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
