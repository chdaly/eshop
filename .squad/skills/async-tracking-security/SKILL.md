# Async Tracking Security Pattern

## Context
Fire-and-forget async operations (e.g., analytics, telemetry, view tracking) that run outside the HTTP request/response cycle.

## Security Risks

### 1. Silent Failure Cascade
- **Pattern**: `Task.Run(() => { try { await service.Track(); } catch (Exception ex) { logger.LogError(ex); } })`
- **Risk**: Errors logged but not surfaced → silent degradation, difficult troubleshooting
- **Example**: `RecommendationApi.cs:86-99` — Redis failures hidden from users and monitoring

### 2. Resource Exhaustion
- **Pattern**: No rate limiting or backpressure on fire-and-forget operations
- **Risk**: Malicious users spam endpoint → unbounded Task.Run spawning → thread pool/connection pool exhaustion
- **Example**: `/api/catalog/recommendations/view` lacks rate limiting, can spawn unlimited Redis operations

### 3. Data Poisoning
- **Pattern**: User-controlled input stored without validation in fire-and-forget path
- **Risk**: Invalid/malicious data persisted, contaminates downstream analytics or recommendations
- **Example**: `userId` from JWT used directly as Redis key without sanitization

## Testing Checklist

### Functional Tests
- [ ] **Rate Limiting**: Verify excessive requests return 429 Too Many Requests
- [ ] **Graceful Degradation**: Mock service failure, verify API returns success but tracking fails silently
- [ ] **Input Validation**: Send malicious input (XSS, path traversal chars), verify sanitized before storage
- [ ] **Authorization**: Verify tracking endpoint requires authentication if user-specific

### Unit Tests
- [ ] **Error Handling**: Mock service exception, verify logged and not propagated
- [ ] **Backpressure**: Simulate high load, verify circuit breaker or queue limits enforced
- [ ] **Data Sanitization**: Test special characters, max lengths, injection attempts in tracked data

### Observability Tests
- [ ] **Metrics**: Verify tracking failures increment error counters (not just logs)
- [ ] **Alerting**: Verify sustained tracking failures trigger alerts
- [ ] **Tracing**: Verify distributed trace context propagated to fire-and-forget operation

## Recommended Patterns

### Pattern 1: Circuit Breaker
```csharp
// Wrap fire-and-forget with circuit breaker
if (circuitBreaker.IsOpen)
{
    logger.LogWarning("Tracking circuit breaker open, skipping view record");
    return TypedResults.NoContent();
}

_ = Task.Run(async () =>
{
    try
    {
        await service.RecordViewAsync(userId, itemId);
        circuitBreaker.RecordSuccess();
    }
    catch (Exception ex)
    {
        circuitBreaker.RecordFailure();
        logger.LogError(ex, "Failed to record view");
    }
});
```

### Pattern 2: Rate Limiting
```csharp
// Add rate limiting middleware or attribute
[EnableRateLimiting("tracking")]
api.MapPost("/view", RecordView);

// Or inline check
var rateLimitKey = $"view_rate:{userId}";
var requestCount = await redis.StringIncrementAsync(rateLimitKey, 1);
if (requestCount == 1)
{
    await redis.KeyExpireAsync(rateLimitKey, TimeSpan.FromMinutes(1));
}
if (requestCount > 100) // Max 100 views per minute
{
    return TypedResults.StatusCode(429);
}
```

### Pattern 3: Input Sanitization
```csharp
// Sanitize user ID before using as Redis key
private static string SanitizeUserId(string userId)
{
    // Remove path traversal, special chars, enforce max length
    if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128)
    {
        throw new ArgumentException("Invalid user ID");
    }
    
    // Allow only alphanumeric, dash, underscore
    var sanitized = Regex.Replace(userId, @"[^a-zA-Z0-9\-_]", "");
    
    if (sanitized != userId)
    {
        logger.LogWarning("Sanitized user ID from '{Original}' to '{Sanitized}'", userId, sanitized);
    }
    
    return sanitized;
}
```

### Pattern 4: Observability
```csharp
// Emit metrics, not just logs
_ = Task.Run(async () =>
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await service.RecordViewAsync(userId, itemId);
        metrics.RecordTrackingSuccess(stopwatch.Elapsed);
    }
    catch (Exception ex)
    {
        metrics.RecordTrackingFailure(ex.GetType().Name);
        logger.LogError(ex, "Failed to record view");
    }
});
```

## Detection Strategies

### Runtime Detection
- Monitor error rate of fire-and-forget operations (should be < 1% under normal load)
- Track operation duration (P99 latency should be bounded)
- Alert on circuit breaker open state lasting > 5 minutes
- Alert on rate limit violations (potential attack)

### Load Testing
- Simulate 10x normal traffic to tracking endpoint
- Verify graceful degradation (HTTP 429, not 500)
- Verify application doesn't crash or exhaust thread pool
- Verify tracking service connection pool doesn't exhaust

### Penetration Testing
- Inject malicious payloads in user IDs (path traversal, XSS, long strings)
- Verify sanitization before storage
- Attempt to pollute other users' tracking data
- Verify authorization prevents unauthorized tracking

## References
- eShop: `src/Catalog.API/Apis/RecommendationApi.cs:86-99`
- eShop: `src/Catalog.API/Services/RecommendationService.cs:20-36`
- Pattern: Microsoft.Extensions.RateLimiting
- Pattern: Polly Circuit Breaker
