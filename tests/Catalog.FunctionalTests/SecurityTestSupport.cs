#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API.Model;
using eShop.Catalog.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace eShop.Catalog.FunctionalTests;

internal static class SecurityTestSupport
{
    public static MethodInfo GetRequiredStaticMethod(Type type, params string[] candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            var method = type.GetMethod(candidateName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method is not null)
            {
                return method;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected one of [{string.Join(", ", candidateNames)}] on {type.FullName}.");
    }

    public static MethodInfo GetRequiredUserIdLogHelper(Type type)
    {
        var method = type
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

        return method ?? throw new Xunit.Sdk.XunitException($"Expected a userId masking helper on {type.FullName}.");
    }

    public static object? InvokeStatic(MethodInfo method, params object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

internal sealed class TestLogger : ILogger
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    public IReadOnlyCollection<TestLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new TestLogEntry(logLevel, formatter(state, exception), exception));
    }

    internal sealed record TestLogEntry(LogLevel LogLevel, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly TestLogger _inner = new();

    public IReadOnlyCollection<TestLogger.TestLogEntry> Entries => _inner.Entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

internal sealed class SecurityTestCatalogContext(DbContextOptions<CatalogContext> options, IConfiguration configuration)
    : CatalogContext(options, configuration)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<CatalogItem>().Ignore(item => item.Embedding);
    }
}

internal sealed class SpyRecommendationService(TimeSpan processingDelay) : IRecommendationService
{
    private readonly ConcurrentQueue<(string UserId, int ItemId)> _recordedViews = new();

    public IReadOnlyCollection<(string UserId, int ItemId)> RecordedViews => _recordedViews.ToArray();

    public async Task RecordViewAsync(string userId, int itemId, CancellationToken cancellationToken = default)
    {
        if (processingDelay > TimeSpan.Zero)
        {
            await Task.Delay(processingDelay, cancellationToken);
        }

        _recordedViews.Enqueue((userId, itemId));
    }

    public Task<PaginatedItems<CatalogItem>> GetRecommendationsAsync(string userId, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
        => Task.FromResult(new PaginatedItems<CatalogItem>(pageIndex, pageSize, 0, Array.Empty<CatalogItem>()));

    public async Task WaitForCountAsync(int expectedCount, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (_recordedViews.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {expectedCount} queued view(s). Observed {_recordedViews.Count}.");
    }
}
