# Product Recommendation Feature - Architecture Design

**Author:** Rusty (Lead Architect)  
**Date:** 2026-04-10  
**Status:** Ready for Implementation

---

## Executive Summary

This document specifies the architecture for a product recommendation feature in the eShop reference application. The feature will leverage existing AI embedding infrastructure to provide personalized product suggestions based on browsing history.

**Key Decisions:**
- Add recommendation endpoints to existing Catalog.API (not a new microservice)
- Store browsing history in Redis with session-scoped keys
- Use existing pgvector embeddings for similarity-based ranking
- Implement graceful fallback when AI is disabled
- Display recommendations in a carousel component on product detail pages

---

## 1. Repo Inspection Summary

### 1.1 AI & Embedding Infrastructure

**Location:** `src/Catalog.API/Services/`

The catalog service has mature AI infrastructure:

- **ICatalogAI / CatalogAI**: Interface for embedding generation with pluggable AI backends
  - Supports OpenAI and Ollama (configured via AppHost)
  - Optional - `IsEnabled` property indicates availability
  - `GetEmbeddingAsync(string)` - generates embedding from text
  - `GetEmbeddingAsync(CatalogItem)` - generates embedding from item (name + description)
  
- **Embedding Model**: Uses 384-dimensional vectors (text-embedding-3-small for OpenAI, all-minilm for Ollama)

- **Storage**: CatalogItem.Embedding is a `Pgvector.Vector` field, stored in PostgreSQL with pgvector extension

- **Existing Usage**: 
  - Semantic search endpoint (`/api/catalog/items/withsemanticrelevance`)
  - Embeddings auto-generated on item create/update
  - Cosine distance for similarity ranking

**Configuration Pattern** (`src/Catalog.API/Extensions/Extensions.cs`):
```csharp
if (builder.Configuration["OllamaEnabled"] is string ollamaEnabled && bool.Parse(ollamaEnabled))
{
    builder.AddOllamaApiClient("embedding").AddEmbeddingGenerator();
}
else if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("textEmbeddingModel")))
{
    builder.AddOpenAIClientFromConfiguration("textEmbeddingModel").AddEmbeddingGenerator();
}
builder.Services.AddScoped<ICatalogAI, CatalogAI>();
```

### 1.2 Product Detail Page Flow

**Location:** `src/WebApp/Components/Pages/Item/ItemPage.razor`

Current flow:
1. Route: `/item/{itemId:int}`
2. Injected services: CatalogService, BasketState, NavigationManager
3. Authentication check: `HttpContext?.User.Identity?.IsAuthenticated`
4. Fetches single item: `CatalogService.GetCatalogItem(ItemId)`
5. Displays: image, description, brand, price, add-to-cart button

**Key Insight:** Page has access to user authentication state and item ID - perfect place to:
- Record a view event (authenticated or anonymous)
- Fetch and display recommendations

### 1.3 Catalog API Patterns

**Location:** `src/Catalog.API/Apis/CatalogApi.cs`

Consistent patterns observed:
- **Endpoint registration**: `api.MapGet/MapPost` with versioning via `HasApiVersion(1, 0)`
- **Service injection**: `[AsParameters] CatalogServices services` provides Context, CatalogAI, Logger, EventService
- **Response types**: `Results<Ok<T>, NotFound, BadRequest<ProblemDetails>>`
- **Pagination**: `PaginationRequest` with PageIndex/PageSize
- **AI fallback**: Semantic search falls back to name search if AI disabled

**Model:** `CatalogItem` has Id, Name, Description, Price, CatalogBrand, Embedding

### 1.4 Session & User Handling

**WebApp Authentication** (`src/WebApp/Extensions/Extensions.cs`):
- OIDC + Cookie auth with 60-minute session lifetime
- `AuthenticationStateProvider` provides user claims
- `GetBuyerIdAsync()` extension retrieves "sub" claim (unique user ID)
- Anonymous users: `IsAuthenticated == false`, no buyerId

**State Management Pattern** (`src/WebApp/Services/BasketState.cs`):
- Scoped service with cached data (`_cachedBasket`)
- Clears cache on mutations
- Differentiates authenticated vs anonymous users
- BasketService uses gRPC to Basket.API with auth token propagation

**Redis Available:** AppHost configures Redis (`builder.AddRedis("redis")`) used by Basket.API

### 1.5 AppHost Orchestration

**Location:** `src/eShop.AppHost/Program.cs`

Infrastructure:
- PostgreSQL with pgvector (`ankane/pgvector:latest`)
- Redis (persistent container)
- RabbitMQ event bus
- Service discovery via Aspire DNS

AI Configuration (currently disabled by default):
```csharp
bool useOpenAI = false;
if (useOpenAI) {
    builder.AddOpenAI(catalogApi, webApp, OpenAITarget.OpenAI);
}
```

Catalog.API has access to:
- PostgreSQL database (`catalogdb`)
- RabbitMQ event bus
- AI services (if enabled)

---

## 2. Architecture Decision

### 2.1 Service Placement

**Decision:** Add recommendation logic to **Catalog.API**, not a separate microservice.

**Rationale:**
- Recommendations are tightly coupled to catalog data (items, embeddings)
- No independent scaling requirements distinct from catalog
- Simpler for a reference application (teaching microservices, not over-engineering)
- Recommendations can reuse existing CatalogContext, CatalogAI, and pgvector infrastructure
- Future: Could split if recommendation logic becomes complex (ML models, real-time signals)

### 2.2 Browsing History Storage

**Decision:** Store browsing history in **Redis** with a session-scoped key pattern.

**Key Schema:**
```
browsing_history:{userId}         // Authenticated users
browsing_history:anon:{sessionId} // Anonymous users (future enhancement - skip for v1)
```

**Data Structure:** Redis List (LPUSH for recent-first, LTRIM to cap length)
```json
[
  {"itemId": 42, "timestamp": "2026-04-10T14:23:00Z"},
  {"itemId": 15, "timestamp": "2026-04-10T14:20:00Z"}
]
```

**Configuration:**
- Max history length: 50 items (configurable via `RecommendationOptions`)
- TTL: 30 days (sliding window on writes)

**Rationale:**
- Redis is already in the stack (Basket.API uses it)
- Fast reads/writes for recent history
- Automatic expiration
- Simple list operations (LPUSH, LRANGE, LTRIM)
- No schema migrations needed
- For v1: **Only track authenticated users** (skip anonymous for simplicity)

**Alternative Considered:** PostgreSQL table
- Rejected: Adds DB load for high-frequency writes (every product view)
- Rejected: Requires migrations and schema evolution
- Future: Could aggregate to Postgres for long-term analytics

### 2.3 Recommendation Algorithm

**Decision:** Embedding-based similarity ranking with business rules.

**Algorithm (v1):**
1. Fetch user's browsing history from Redis (last 10 items max)
2. For each viewed item, get its embedding from database
3. Compute average embedding vector (centroid of viewed items)
4. Query CatalogItems ordered by cosine similarity to centroid
5. Apply exclusion rules:
   - Exclude items already viewed (from full history, not just last 10)
   - Exclude out-of-stock items (AvailableStock <= 0)
6. Return top N items (default 10)

**Fallback (when AI disabled):**
- Recommend items from same CatalogType as most recently viewed item
- If no history, recommend newest items by Id descending

**Rationale:**
- Centroid approach balances user's interests across multiple views
- Leverages existing embeddings (no new infrastructure)
- Cosine similarity is fast with pgvector indexes
- Business rules prevent poor UX (recommending viewed/unavailable items)

### 2.4 Feature Flag

**Decision:** Always-on if AI is enabled, graceful fallback if disabled. No separate feature flag.

**Configuration:** Inherit from existing `ICatalogAI.IsEnabled`

**Rationale:**
- Recommendations are a natural extension of semantic search (same AI dependency)
- Adding a separate flag complicates config for marginal benefit
- Fallback logic ensures feature is useful even without AI

---

## 3. API Contract

### 3.1 Record a Product View

**Endpoint:** `POST /api/catalog/recommendations/view`

**Request:**
```csharp
public record RecordProductViewRequest(
    [Required] int ItemId
);
```

**Response:** `204 No Content` (fire-and-forget)

**Error Cases:**
- `400 Bad Request` if ItemId <= 0
- `404 Not Found` if item doesn't exist
- `401 Unauthorized` if user is not authenticated (v1 only supports authenticated users)

**Implementation Notes:**
- Async processing (don't wait for Redis write to complete)
- LPUSH to `browsing_history:{userId}`
- LTRIM to cap at 50 items
- EXPIRE to reset 30-day TTL

### 3.2 Get Recommendations

**Endpoint:** `GET /api/catalog/recommendations`

**Query Parameters:**
```csharp
public record GetRecommendationsRequest(
    [Range(1, 100)] int PageSize = 10,
    int PageIndex = 0
);
```

**Response:**
```csharp
// Reuse existing PaginatedItems<CatalogItem>
public record PaginatedItems<T>(
    int PageIndex,
    int PageSize,
    long Count,
    IEnumerable<T> Data
);
```

**Behavior:**
- Authenticated users: Use browsing history from Redis
- Unauthenticated users (v1): Return fallback recommendations (newest items)
- AI disabled: Use fallback algorithm (same catalog type as recent views)
- No history: Return newest items

**Performance:** Target < 200ms p95 (embedding queries are fast with pgvector index)

---

## 4. Backend Implementation Plan (Linus)

### 4.1 Files to Create

#### **`src/Catalog.API/Services/IRecommendationService.cs`**
```csharp
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
```

#### **`src/Catalog.API/Services/RecommendationService.cs`**
Implementation logic:
- Constructor: inject `IConnectionMultiplexer` (Redis), `CatalogContext`, `ICatalogAI`, `ILogger`, `IOptions<RecommendationOptions>`
- `RecordViewAsync`:
  - Serialize `{itemId, timestamp}` to JSON
  - LPUSH to `browsing_history:{userId}`
  - LTRIM to cap at MaxHistoryLength
  - EXPIRE to reset TTL
- `GetRecommendationsAsync`:
  - Fetch history from Redis (LRANGE 0 to 9 for last 10 items)
  - If AI enabled: compute centroid, query by similarity
  - If AI disabled: fallback to same-type recommendations
  - Apply exclusions (viewed items, out-of-stock)
  - Return paginated results

#### **`src/Catalog.API/Model/RecommendationOptions.cs`**
```csharp
namespace eShop.Catalog.API.Model;

public class RecommendationOptions
{
    public int MaxHistoryLength { get; set; } = 50;
    public int HistoryTtlDays { get; set; } = 30;
    public int CentroidSampleSize { get; set; } = 10; // Use last N items for centroid
}
```

#### **`src/Catalog.API/Model/BrowsingHistoryItem.cs`**
```csharp
namespace eShop.Catalog.API.Model;

public record BrowsingHistoryItem(
    int ItemId,
    DateTime Timestamp
);
```

#### **`src/Catalog.API/Apis/RecommendationApi.cs`**
```csharp
namespace eShop.Catalog.API.Apis;

public static class RecommendationApi
{
    public static IEndpointRouteBuilder MapRecommendationApi(this IEndpointRouteBuilder app)
    {
        var api = app.NewVersionedApi("Recommendations")
            .MapGroup("api/catalog/recommendations")
            .HasApiVersion(1, 0);

        api.MapPost("/view", RecordView)
            .WithName("RecordProductView")
            .RequireAuthorization(); // v1: authenticated only

        api.MapGet("", GetRecommendations)
            .WithName("GetRecommendations")
            .RequireAuthorization(); // v1: authenticated only

        return app;
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>, UnauthorizedHttpResult>> 
        RecordView(
            HttpContext httpContext,
            CatalogContext context,
            IRecommendationService recommendationService,
            [FromBody] RecordProductViewRequest request)
    {
        // Validate item exists
        var item = await context.CatalogItems.FindAsync(request.ItemId);
        if (item is null) return TypedResults.NotFound();

        // Get user ID from claims
        var userId = httpContext.User.FindFirst("sub")?.Value;
        if (userId is null) return TypedResults.Unauthorized();

        // Record view (fire-and-forget style, log errors but don't fail request)
        _ = Task.Run(async () =>
        {
            try
            {
                await recommendationService.RecordViewAsync(userId, request.ItemId);
            }
            catch (Exception ex)
            {
                // Log error but don't propagate (recommendation tracking is not critical path)
                var logger = httpContext.RequestServices.GetRequiredService<ILogger<RecommendationApi>>();
                logger.LogError(ex, "Failed to record product view for user {UserId}, item {ItemId}", userId, request.ItemId);
            }
        });

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<PaginatedItems<CatalogItem>>, UnauthorizedHttpResult>> 
        GetRecommendations(
            HttpContext httpContext,
            IRecommendationService recommendationService,
            [AsParameters] PaginationRequest paginationRequest)
    {
        var userId = httpContext.User.FindFirst("sub")?.Value;
        if (userId is null) return TypedResults.Unauthorized();

        var recommendations = await recommendationService.GetRecommendationsAsync(
            userId,
            paginationRequest.PageIndex,
            paginationRequest.PageSize);

        return TypedResults.Ok(recommendations);
    }
}

public record RecordProductViewRequest([Required] int ItemId);
```

### 4.2 Files to Modify

#### **`src/Catalog.API/Program.cs`**
Add line after `app.MapCatalogApi();`:
```csharp
app.MapRecommendationApi();
```

#### **`src/Catalog.API/Extensions/Extensions.cs`**
Add to `AddApplicationServices` method (after CatalogAI registration):
```csharp
// Redis for browsing history
builder.AddRedisClient("redis");

// Recommendation services
builder.Services.AddOptions<RecommendationOptions>()
    .BindConfiguration(nameof(RecommendationOptions));
builder.Services.AddScoped<IRecommendationService, RecommendationService>();
```

#### **`src/Catalog.API/appsettings.json`**
Add configuration section:
```json
{
  "RecommendationOptions": {
    "MaxHistoryLength": 50,
    "HistoryTtlDays": 30,
    "CentroidSampleSize": 10
  }
}
```

#### **`src/Catalog.API/Catalog.API.csproj`**
Ensure Redis client package is referenced (likely already present as a transitive dependency, but add explicitly):
```xml
<PackageReference Include="StackExchange.Redis" />
```

#### **`src/eShop.AppHost/Program.cs`**
Redis is already shared with Basket.API. Explicitly add reference for Catalog.API:
```csharp
var catalogApi = builder.AddProject<Projects.Catalog_API>("catalog-api")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(catalogDb)
    .WithReference(redis); // ADD THIS LINE
```

### 4.3 Key Implementation Details

**Centroid Calculation:**
```csharp
// Pseudo-code
var viewedItems = await context.CatalogItems
    .Where(c => historyItemIds.Contains(c.Id) && c.Embedding != null)
    .Select(c => c.Embedding)
    .ToListAsync();

if (viewedItems.Count == 0) return FallbackRecommendations();

// Average the embeddings (component-wise mean)
var centroid = new float[384];
foreach (var embedding in viewedItems)
{
    for (int i = 0; i < 384; i++)
    {
        centroid[i] += embedding[i];
    }
}
for (int i = 0; i < 384; i++)
{
    centroid[i] /= viewedItems.Count;
}
var centroidVector = new Vector(centroid);
```

**Similarity Query:**
```csharp
var recommendations = await context.CatalogItems
    .Where(c => c.Embedding != null)
    .Where(c => !allViewedItemIds.Contains(c.Id))  // Exclude viewed
    .Where(c => c.AvailableStock > 0)              // Exclude out-of-stock
    .OrderBy(c => c.Embedding!.CosineDistance(centroidVector))
    .Skip(pageSize * pageIndex)
    .Take(pageSize)
    .ToListAsync();
```

**Fallback (AI disabled):**
```csharp
// Get most recent item's type
var recentItemType = await context.CatalogItems
    .Where(c => c.Id == mostRecentItemId)
    .Select(c => c.CatalogTypeId)
    .FirstOrDefaultAsync();

var recommendations = await context.CatalogItems
    .Where(c => c.CatalogTypeId == recentItemType)
    .Where(c => !allViewedItemIds.Contains(c.Id))
    .Where(c => c.AvailableStock > 0)
    .OrderByDescending(c => c.Id)  // Newest first
    .Skip(pageSize * pageIndex)
    .Take(pageSize)
    .ToListAsync();
```

### 4.4 Testing Checklist
- Unit tests for RecommendationService (mock Redis, CatalogContext)
- Functional test for RecordView endpoint (requires auth token)
- Functional test for GetRecommendations endpoint
- Test AI enabled vs disabled scenarios
- Test empty history case
- Test exclusion rules (viewed items, out-of-stock)

---

## 5. Frontend Implementation Plan (Livingston)

### 5.1 Files to Create

#### **`src/WebAppComponents/Catalog/ProductRecommendations.razor`**
A reusable carousel component for displaying recommendations.

**Props:**
```csharp
@code {
    [Parameter, EditorRequired]
    public int CurrentItemId { get; set; }  // Exclude this from recommendations
    
    [Parameter]
    public int MaxItems { get; set; } = 10;
}
```

**Template Structure:**
```razor
@inject CatalogService CatalogService
@inject IProductImageUrlProvider ProductImages

@if (recommendations.Any())
{
    <section class="product-recommendations">
        <h2>You might also like</h2>
        <div class="recommendations-carousel">
            @foreach (var item in recommendations)
            {
                <a href="/item/@item.Id" class="recommendation-card">
                    <img src="@ProductImages.GetProductImageUrl(item)" alt="@item.Name" />
                    <h3>@item.Name</h3>
                    <span class="price">$@item.Price.ToString("0.00")</span>
                </a>
            }
        </div>
    </section>
}

@code {
    private List<CatalogItem> recommendations = new();

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var result = await CatalogService.GetRecommendations(0, MaxItems);
            recommendations = result.Data.Where(r => r.Id != CurrentItemId).Take(MaxItems).ToList();
        }
        catch (Exception ex)
        {
            // Log and gracefully degrade (don't show recommendations on error)
            // TODO: inject ILogger and log error
            recommendations = new();
        }
    }
}
```

#### **`src/WebAppComponents/Catalog/ProductRecommendations.razor.css`**
Scoped CSS for horizontal scrolling carousel:
```css
.product-recommendations {
    margin: 3rem 0;
    padding: 2rem 0;
    border-top: 1px solid #e0e0e0;
}

.product-recommendations h2 {
    font-size: 1.5rem;
    margin-bottom: 1.5rem;
    color: #333;
}

.recommendations-carousel {
    display: flex;
    gap: 1.5rem;
    overflow-x: auto;
    scroll-snap-type: x mandatory;
    padding-bottom: 1rem;
}

.recommendation-card {
    flex: 0 0 200px;
    scroll-snap-align: start;
    text-decoration: none;
    color: inherit;
    border: 1px solid #e0e0e0;
    border-radius: 8px;
    padding: 1rem;
    transition: transform 0.2s, box-shadow 0.2s;
}

.recommendation-card:hover {
    transform: translateY(-4px);
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.recommendation-card img {
    width: 100%;
    height: 200px;
    object-fit: cover;
    border-radius: 4px;
    margin-bottom: 0.75rem;
}

.recommendation-card h3 {
    font-size: 0.95rem;
    margin: 0 0 0.5rem 0;
    height: 2.4em;
    overflow: hidden;
    text-overflow: ellipsis;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
}

.recommendation-card .price {
    font-weight: bold;
    color: #d32f2f;
    font-size: 1.1rem;
}
```

### 5.2 Files to Modify

#### **`src/WebApp/Components/Pages/Item/ItemPage.razor`**

**Add at top (after existing @inject lines):**
```razor
@using eShop.WebAppComponents.Catalog
```

**Add after the closing `</div>` of the `item-details` div (around line 53):**
```razor
    @if (isLoggedIn && item is not null)
    {
        <ProductRecommendations CurrentItemId="@ItemId" MaxItems="10" />
    }
```

**Add to `@code` block (after OnInitializedAsync):**
```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (firstRender && isLoggedIn && item is not null)
    {
        // Record the view asynchronously (don't await, fire-and-forget)
        _ = RecordProductViewAsync();
    }
}

private async Task RecordProductViewAsync()
{
    try
    {
        await CatalogService.RecordProductView(ItemId);
    }
    catch (Exception)
    {
        // Silently fail - view tracking is not critical
    }
}
```

#### **`src/WebAppComponents/Services/ICatalogService.cs`**
Add method signatures:
```csharp
Task RecordProductView(int itemId);
Task<CatalogResult> GetRecommendations(int pageIndex, int pageSize);
```

#### **`src/WebAppComponents/Services/CatalogService.cs`**
Implement methods:
```csharp
public async Task RecordProductView(int itemId)
{
    var uri = $"{remoteServiceBaseUrl}recommendations/view";
    var request = new { ItemId = itemId };
    var response = await httpClient.PostAsJsonAsync(uri, request);
    response.EnsureSuccessStatusCode(); // Will throw on error
}

public async Task<CatalogResult> GetRecommendations(int pageIndex, int pageSize)
{
    var uri = $"{remoteServiceBaseUrl}recommendations?pageIndex={pageIndex}&pageSize={pageSize}";
    var result = await httpClient.GetFromJsonAsync<CatalogResult>(uri);
    return result!;
}
```

### 5.3 UX Considerations

**Placement:** Below product details, above footer. Users naturally scroll down after viewing item.

**Anonymous Users (v1):** Don't show recommendations (no data). Future: could show "Popular Items" or category-based suggestions.

**Mobile Responsive:** Horizontal scroll with snap points works well on touch devices.

**Loading State:** Component handles async load internally. Consider adding a skeleton loader in future iteration.

**Error Handling:** Fail silently - if recommendations can't load, page still works.

---

## 6. Test Plan (Basher)

### 6.1 Test Project

**Use:** `tests/Catalog.FunctionalTests` (existing project)

**Add Tests To:** `CatalogApiTests.cs` (or new file `RecommendationApiTests.cs`)

### 6.2 Test Scenarios

#### **Test 1: RecordView Endpoint (Authenticated User)**
```csharp
[Theory]
[InlineData(1.0)]
public async Task RecordProductView_AuthenticatedUser_ReturnsNoContent(double version)
{
    var httpClient = CreateAuthenticatedClient(version);
    
    var request = new { ItemId = 1 };
    var response = await httpClient.PostAsJsonAsync(
        "/api/catalog/recommendations/view", 
        request);
    
    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
}
```

#### **Test 2: RecordView Requires Authentication**
```csharp
[Fact]
public async Task RecordProductView_UnauthenticatedUser_ReturnsUnauthorized()
{
    var httpClient = CreateAnonymousClient();
    
    var request = new { ItemId = 1 };
    var response = await httpClient.PostAsJsonAsync(
        "/api/catalog/recommendations/view", 
        request);
    
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

#### **Test 3: RecordView Invalid Item Returns NotFound**
```csharp
[Fact]
public async Task RecordProductView_InvalidItem_ReturnsNotFound()
{
    var httpClient = CreateAuthenticatedClient();
    
    var request = new { ItemId = 99999 };
    var response = await httpClient.PostAsJsonAsync(
        "/api/catalog/recommendations/view", 
        request);
    
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
```

#### **Test 4: GetRecommendations Returns Items**
```csharp
[Fact]
public async Task GetRecommendations_WithHistory_ReturnsRecommendations()
{
    var httpClient = CreateAuthenticatedClient();
    
    // Record some views first
    await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 1 });
    await httpClient.PostAsJsonAsync("/api/catalog/recommendations/view", new { ItemId = 2 });
    await Task.Delay(100); // Allow async processing
    
    // Fetch recommendations
    var response = await httpClient.GetAsync(
        "/api/catalog/recommendations?pageIndex=0&pageSize=10");
    
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<PaginatedItems<CatalogItem>>();
    
    Assert.NotNull(result);
    Assert.True(result.Data.Any());
    // Recommendations should not include items 1 or 2 (viewed items)
    Assert.DoesNotContain(result.Data, item => item.Id == 1 || item.Id == 2);
}
```

#### **Test 5: GetRecommendations Without History (Fallback)**
```csharp
[Fact]
public async Task GetRecommendations_NoHistory_ReturnsFallbackItems()
{
    var httpClient = CreateAuthenticatedClient(useNewUser: true); // Fresh user
    
    var response = await httpClient.GetAsync(
        "/api/catalog/recommendations?pageIndex=0&pageSize=10");
    
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<PaginatedItems<CatalogItem>>();
    
    Assert.NotNull(result);
    Assert.True(result.Data.Any()); // Should still return items (fallback)
}
```

#### **Test 6: Recommendations Exclude Out-of-Stock Items**
```csharp
[Fact]
public async Task GetRecommendations_ExcludesOutOfStock()
{
    var httpClient = CreateAuthenticatedClient();
    
    // Set an item to out-of-stock
    var item = await GetCatalogItem(3);
    item.AvailableStock = 0;
    await httpClient.PutAsJsonAsync($"/api/catalog/items/{item.Id}", item);
    
    // Record views
    await RecordViews(httpClient, [1, 2, 4, 5]);
    
    // Fetch recommendations
    var result = await GetRecommendations(httpClient, 0, 20);
    
    // Should not recommend item 3 (out of stock)
    Assert.DoesNotContain(result.Data, r => r.Id == 3);
}
```

#### **Test 7: AI Disabled Fallback**
Set up test with AI disabled (via configuration override):
```csharp
[Fact]
public async Task GetRecommendations_AIDisabled_UsesFallbackAlgorithm()
{
    // Create test fixture with AI disabled
    var fixture = new CatalogApiFixture(configureAI: false);
    var httpClient = fixture.CreateAuthenticatedClient();
    
    await RecordViews(httpClient, [1, 2, 3]);
    var recommendations = await GetRecommendations(httpClient, 0, 10);
    
    Assert.NotNull(recommendations);
    Assert.True(recommendations.Data.Any());
    // Verify fallback logic (same catalog type as recent views)
}
```

### 6.3 Integration Test Considerations

**Redis Dependency:** Functional tests should use Redis (via Aspire test host or Testcontainers)

**User Isolation:** Each test should use a unique user ID to avoid cross-test contamination:
```csharp
private HttpClient CreateAuthenticatedClient(string userId = null)
{
    userId ??= Guid.NewGuid().ToString(); // Unique user per test
    // Add Authorization header with mock JWT containing "sub" claim
}
```

**Cleanup:** Clear Redis keys after tests (or use unique prefixes per test)

### 6.4 Manual Testing Checklist

- [ ] Enable AI (OpenAI or Ollama) in AppHost
- [ ] Log in to WebApp
- [ ] View 3-5 different products
- [ ] Navigate to a product detail page
- [ ] Verify recommendations carousel appears below product details
- [ ] Verify recommended items are NOT the current item or previously viewed
- [ ] Verify clicking a recommendation navigates to that item's page
- [ ] Disable AI in AppHost, restart
- [ ] Verify recommendations still appear (fallback mode)
- [ ] Test as anonymous user - verify recommendations do NOT appear (v1)

---

## 7. Tradeoffs and Risks

### 7.1 Tradeoffs

**Simplicity vs. Sophistication:**
- ✅ **Chose:** Simple centroid-based similarity
- ❌ **Rejected:** Advanced ML (collaborative filtering, neural networks)
- **Rationale:** Reference app should be understandable without deep ML expertise. Centroid approach is intuitive and leverages existing embeddings.

**Storage:**
- ✅ **Chose:** Redis for browsing history
- ❌ **Rejected:** PostgreSQL table, in-memory cache
- **Rationale:** Redis offers fast writes, automatic expiration, and is already deployed. Postgres would add DB load; in-memory doesn't survive restarts.

**Service Placement:**
- ✅ **Chose:** Add to Catalog.API
- ❌ **Rejected:** New Recommendations.API microservice
- **Rationale:** Recommendations are catalog-centric. Over-architecting a reference app teaches bad lessons (premature microservice decomposition).

**User Scope (v1):**
- ✅ **Chose:** Authenticated users only
- ❌ **Rejected:** Anonymous user support
- **Rationale:** Anonymous tracking requires session management (cookies, session IDs), GDPR considerations. Defer to v2 for simplicity.

### 7.2 Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Redis unavailable** | Low | Medium | Graceful degradation - if Redis is down, RecordView fails silently; GetRecommendations returns fallback items (newest). |
| **AI service slow/unavailable** | Medium | Low | Already mitigated - `ICatalogAI.IsEnabled` check, fallback to same-type recommendations. |
| **Cold start (no history)** | High | Low | Fallback to newest items or category-based recommendations. Acceptable UX for new users. |
| **Embedding quality** | Medium | Medium | Depends on AI model. text-embedding-3-small is well-tested. If quality is poor, can swap model or adjust centroid calculation. |
| **Privacy concerns** | Low | High | v1 only tracks authenticated users (explicit login = consent). Store only item IDs, not PII. Document data retention (30 days). |
| **Performance (centroid calc)** | Low | Medium | Centroid calculation is O(n) over 10 items, very fast. Similarity query is indexed (pgvector). Monitor p95 latency. |

### 7.3 Future Enhancements (Out of Scope for v1)

- **Anonymous user support:** Session-based tracking with cookie consent
- **Diversity in recommendations:** Ensure recommendations span multiple categories (not just similar items)
- **Real-time signals:** Weight recent views higher in centroid calculation
- **A/B testing framework:** Compare centroid vs. category-based vs. popularity-based algorithms
- **Analytics dashboard:** Track click-through rate on recommendations
- **Collaborative filtering:** "Users who viewed X also viewed Y"
- **Event-driven updates:** Use RabbitMQ to publish view events for async processing (decouple from API request)

---

## 8. Success Metrics

**Implementation Milestones:**
1. Backend endpoints passing functional tests
2. Frontend component rendering recommendations
3. AI-enabled and AI-disabled modes both working
4. End-to-end manual test successful

**Quality Gates:**
- All tests green (functional + unit)
- No new build warnings
- Performance: p95 latency < 200ms for GetRecommendations
- Code review approval (check Redis key naming, error handling, logging)

**Post-Deployment (if applicable):**
- Monitor Redis memory usage (browsing history should be small footprint)
- Track recommendation API call volume
- Log errors in RecordView (if Redis is failing)

---

## Appendix A: File Checklist

**Backend (Linus):**
- [ ] Create `src/Catalog.API/Services/IRecommendationService.cs`
- [ ] Create `src/Catalog.API/Services/RecommendationService.cs`
- [ ] Create `src/Catalog.API/Model/RecommendationOptions.cs`
- [ ] Create `src/Catalog.API/Model/BrowsingHistoryItem.cs`
- [ ] Create `src/Catalog.API/Apis/RecommendationApi.cs`
- [ ] Modify `src/Catalog.API/Program.cs` (add MapRecommendationApi)
- [ ] Modify `src/Catalog.API/Extensions/Extensions.cs` (register services)
- [ ] Modify `src/Catalog.API/appsettings.json` (add config section)
- [ ] Modify `src/Catalog.API/Catalog.API.csproj` (verify Redis package)
- [ ] Modify `src/eShop.AppHost/Program.cs` (add Redis reference to catalogApi)

**Frontend (Livingston):**
- [ ] Create `src/WebAppComponents/Catalog/ProductRecommendations.razor`
- [ ] Create `src/WebAppComponents/Catalog/ProductRecommendations.razor.css`
- [ ] Modify `src/WebApp/Components/Pages/Item/ItemPage.razor` (add component + RecordView call)
- [ ] Modify `src/WebAppComponents/Services/ICatalogService.cs` (add methods)
- [ ] Modify `src/WebAppComponents/Services/CatalogService.cs` (implement methods)

**Tests (Basher):**
- [ ] Add tests to `tests/Catalog.FunctionalTests/CatalogApiTests.cs` (or new file)
- [ ] Implement 7 test scenarios (auth, unauthenticated, invalid item, fallback, exclusions, AI disabled)
- [ ] Manual test checklist completion

---

## Appendix B: Redis Data Structure Reference

**Key Pattern:**
```
browsing_history:{userId}
```

**Data Type:** List (Redis LIST)

**Operations:**
- **Write:** `LPUSH browsing_history:{userId} "{\"itemId\":42,\"timestamp\":\"2026-04-10T14:23:00Z\"}"`
- **Trim:** `LTRIM browsing_history:{userId} 0 49` (keep last 50)
- **Expire:** `EXPIRE browsing_history:{userId} 2592000` (30 days in seconds)
- **Read:** `LRANGE browsing_history:{userId} 0 9` (get last 10)

**Example:**
```bash
# User views items 42, 15, 8
LPUSH browsing_history:user123 '{"itemId":42,"timestamp":"2026-04-10T14:23:00Z"}'
LPUSH browsing_history:user123 '{"itemId":15,"timestamp":"2026-04-10T14:20:00Z"}'
LPUSH browsing_history:user123 '{"itemId":8,"timestamp":"2026-04-10T14:18:00Z"}'
LTRIM browsing_history:user123 0 49
EXPIRE browsing_history:user123 2592000

# Retrieve last 10 views
LRANGE browsing_history:user123 0 9
# Returns: ["...", "...", "..."] (JSON strings, most recent first)
```

---

## Appendix C: Questions for Chris Daly

*None at this time. Proceed with implementation.*

---

**END OF ARCHITECTURE DOCUMENT**
