# Implementation Plan: Catalog API Authentication & Authorization

**Security Finding #4 — Catalog API write operations require no authentication**

**Author:** Rusty (Lead/Architect)  
**Risk Level:** LOW (auth), MEDIUM (mass assignment)  
**Status:** Planning  
**Created:** 2025-01-08

---

## 1. Problem Statement

### What is Broken

The Catalog API exposes three mutating endpoints with **no authentication or authorization requirements**:

1. **CreateItem** (`POST /api/catalog/items`) — Line 103-106, `CatalogApi.cs`
2. **UpdateItem** (`PUT /api/catalog/items/{id}`) — Line 98-102, `CatalogApi.cs` (v2.0)
3. **UpdateItemV1** (`PUT /api/catalog/items`) — Line 93-97, `CatalogApi.cs` (v1.0)
4. **DeleteItemById** (`DELETE /api/catalog/items/{id}`) — Line 107-110, `CatalogApi.cs`

The route group registration in `Program.cs:20` calls `app.MapCatalogApi()` without any `.RequireAuthorization()` on the mutating endpoints.

### Security Risk

**Authentication Gap:**
- Any unauthenticated caller (including malicious actors, scrapers, or misconfigured clients) can:
  - Create fraudulent catalog items
  - Modify existing item prices, stock levels, or descriptions
  - Delete legitimate products from the catalog
  - Disrupt business operations and data integrity

**Mass Assignment Vulnerability (MEDIUM risk):**
- `UpdateItem` (line 324-363) directly binds the incoming `CatalogItem` entity to the database entity via `SetValues(productToUpdate)` (line 341)
- This allows clients to manipulate **internal-only fields** that should never be user-controlled:
  - `Embedding` (Vector) — AI-generated semantic embedding for search; should be computed server-side only
  - `Id` — Primary key manipulation
  - `OnReorder` — Internal supply chain flag
  - Any future internal fields added to the model
- An attacker could inject malicious embeddings to poison semantic search results or manipulate business logic flags

### Files Affected

- `src/Catalog.API/Apis/CatalogApi.cs` — Lines 93-110 (endpoint registration), 310-404 (handler implementations)
- `src/Catalog.API/Program.cs` — Line 20 (route group registration)
- `tests/Catalog.FunctionalTests/CatalogApiTests.cs` — Lines 50-422 (tests assume anonymous access)

---

## 2. Scope

### In Scope

1. **Add authentication/authorization to write operations:**
   - `CreateItem`, `UpdateItem`, `UpdateItemV1`, `DeleteItemById`
   
2. **Fix mass assignment vulnerability in UpdateItem:**
   - Replace direct entity binding with a DTO
   - Explicitly map only user-editable fields
   - Keep `Embedding` generation server-side only

3. **Update functional tests:**
   - Add authenticated HTTP client to test fixture
   - Verify 401 Unauthorized for unauthenticated requests
   - Update existing tests to use authenticated client

4. **Configuration alignment:**
   - Define appropriate scope/policy in Identity.API if needed
   - Update Catalog.API configuration to reference Identity.API

### Out of Scope

1. **Read operations** — Remain publicly accessible (no auth required):
   - `GetAllItems`, `GetItemById`, `GetItemsByName`, etc.
   - This aligns with eShop's "browse catalog anonymously" UX

2. **Recommendation API** — Already correctly protected (line 21, 28 in `RecommendationApi.cs`)

3. **Database migrations** — No schema changes required

4. **Event Bus integration** — No changes to `ProductPriceChangedIntegrationEvent` flow

5. **Swagger UI configuration** — Leave for separate task (will require OAuth2 flow setup)

6. **Catalog seeding/migration scripts** — Address separately if they need service accounts

---

## 3. Authorization Strategy

### Analysis of Existing Identity Configuration

From `Identity.API/Configuration/Config.cs`:
- **Defined scopes:** `orders`, `basket`, `webhooks` (lines 20-26)
- **No "catalog" scope exists** — This is an architectural gap
- **Clients with scope access:**
  - `maui` client (line 46-75): Has `orders`, `basket`, `webhooks`, `mobileshoppingagg`
  - `webapp` client (line 76-111): Has `orders`, `basket`, `webhooks`, `webshoppingagg`
  - `webhooksclient` (line 112-143): Has `webhooks` only

### Recommended Strategy

**Option A: Create a "catalog" scope** (RECOMMENDED)
- Add `catalog` to `GetApis()`, `GetApiScopes()` in `Identity.API/Configuration/Config.cs`
- Grant `catalog` scope to admin/webapp clients only (NOT to public MAUI client)
- Protects against rogue clients; aligns with eShop's scope-per-service architecture
- **Why this works:** Follows the established pattern (basket, orders, webhooks all have dedicated scopes)

**Option B: Reuse existing policy-based authorization**
- Create a custom policy (e.g., `CatalogAdminPolicy`) that checks for `role = admin` claim
- Simpler but less granular; doesn't follow eShop's scope-based model
- **Why to avoid:** Breaks consistency with other services

### Decision: Option A

**Policy Name:** `"catalog"`  
**Scope:** `"catalog"`  
**Audience:** `"catalog"` (to match other services)

**Which clients get access:**
- `webapp` — YES (admin portal needs to manage catalog)
- `webhooksclient` — NO (only needs webhooks)
- `maui` — NO (users shouldn't mutate catalog from mobile app)
- Swagger UI clients — Add `catalogswaggerui` client later (out of scope for this fix)

**Authorization requirement:**
```csharp
.RequireAuthorization(); // Defaults to checking for valid JWT with "catalog" audience
```

---

## 4. Mass Assignment Fix Strategy

### Current Vulnerability

`UpdateItem` line 341:
```csharp
catalogEntry.CurrentValues.SetValues(productToUpdate);
```

This blindly copies **all properties** from the user-supplied `CatalogItem` to the database entity, including:
- `Embedding` (Vector) — Should be server-computed only
- `Id` — Should be immutable (though route binding mitigates this somewhat)
- `OnReorder` — Internal supply chain flag

### Fix Approach

**Create a DTO: `UpdateCatalogItemRequest`**

Located in: `src/Catalog.API/Model/UpdateCatalogItemRequest.cs`

```csharp
public record UpdateCatalogItemRequest(
    [Required] string Name,
    string? Description,
    [Required] decimal Price,
    string? PictureFileName,
    [Required] int CatalogTypeId,
    [Required] int CatalogBrandId,
    [Required] int AvailableStock,
    [Required] int RestockThreshold,
    [Required] int MaxStockThreshold
);
```

**Fields EXCLUDED from DTO (internal-only):**
- `Id` — Comes from route parameter, not body
- `Embedding` — Computed server-side via `CatalogAI.GetEmbeddingAsync()`
- `OnReorder` — Managed by domain logic (`RemoveStock`, `AddStock` methods)
- `CatalogType`, `CatalogBrand` — Navigation properties, not user-editable

**Updated Mapping in UpdateItem:**

Replace line 341 with explicit property mapping:
```csharp
catalogItem.Name = productToUpdate.Name;
catalogItem.Description = productToUpdate.Description;
catalogItem.Price = productToUpdate.Price;
catalogItem.PictureFileName = productToUpdate.PictureFileName;
catalogItem.CatalogTypeId = productToUpdate.CatalogTypeId;
catalogItem.CatalogBrandId = productToUpdate.CatalogBrandId;
catalogItem.AvailableStock = productToUpdate.AvailableStock;
catalogItem.RestockThreshold = productToUpdate.RestockThreshold;
catalogItem.MaxStockThreshold = productToUpdate.MaxStockThreshold;
// Embedding regenerated below (line 343)
catalogItem.Embedding = await services.CatalogAI.GetEmbeddingAsync(catalogItem);
```

**What Happens to Embedding:**
- Server **always recomputes** embedding after updating Name/Description (line 343 already does this)
- Client **cannot** inject custom embeddings
- Semantic search integrity preserved

**OnReorder Flag:**
- Not user-editable; managed by `RemoveStock()` and `AddStock()` domain methods
- Remains unchanged during manual updates

---

## 5. Implementation Steps

Execute in order. Each step is independently committable.

### Step 1: Add "catalog" scope to Identity.API

**File:** `src/Identity.API/Configuration/Config.cs`

1. Add to `GetApis()` (after line 12):
   ```csharp
   new ApiResource("catalog", "Catalog Service"),
   ```

2. Add to `GetApiScopes()` (after line 24):
   ```csharp
   new ApiScope("catalog", "Catalog Service"),
   ```

3. Grant scope to `webapp` client (line 99, in `AllowedScopes` list):
   ```csharp
   "catalog",
   ```

**Commit message:** `security: add 'catalog' scope to Identity.API`

---

### Step 2: Enable authentication in Catalog.API

**File:** `src/Catalog.API/appsettings.json`

Add Identity configuration (after line 15):
```json
"Identity": {
  "Url": "http://identity-api",
  "Audience": "catalog"
},
```

**File:** `src/Catalog.API/Program.cs`

Ensure `builder.AddServiceDefaults()` is called (line 6) — this already wires up `AddDefaultAuthentication()` via `eShop.ServiceDefaults`.

**Verification:** Check that `AuthenticationExtensions.cs` is invoked (it is, via `AddServiceDefaults()`).

**Commit message:** `security: configure Catalog.API to use Identity.API for authentication`

---

### Step 3: Create UpdateCatalogItemRequest DTO

**File:** `src/Catalog.API/Model/UpdateCatalogItemRequest.cs` (NEW)

```csharp
using System.ComponentModel.DataAnnotations;

namespace eShop.Catalog.API.Model;

/// <summary>
/// DTO for updating catalog items. Excludes internal fields like Embedding and OnReorder.
/// </summary>
public record UpdateCatalogItemRequest(
    [Required] string Name,
    string? Description,
    [Required] decimal Price,
    string? PictureFileName,
    [Required] int CatalogTypeId,
    [Required] int CatalogBrandId,
    [Required] int AvailableStock,
    [Required] int RestockThreshold,
    [Required] int MaxStockThreshold
);
```

**Commit message:** `security: add UpdateCatalogItemRequest DTO to prevent mass assignment`

---

### Step 4: Fix mass assignment in UpdateItem

**File:** `src/Catalog.API/Apis/CatalogApi.cs`

**Change 1:** Update `UpdateItemV1` signature (line 310):
```csharp
public static async Task<Results<Created, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>> UpdateItemV1(
    HttpContext httpContext,
    [AsParameters] CatalogServices services,
    UpdateCatalogItemRequest productToUpdate) // Changed from CatalogItem
```

**Change 2:** Update `UpdateItem` signature (line 324):
```csharp
public static async Task<Results<Created, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>> UpdateItem(
    HttpContext httpContext,
    [Description("The id of the catalog item to delete")] int id,
    [AsParameters] CatalogServices services,
    UpdateCatalogItemRequest productToUpdate) // Changed from CatalogItem
```

**Change 3:** Replace mass assignment (line 339-341) with explicit mapping:
```csharp
// Update current product (explicit mapping to prevent mass assignment)
catalogItem.Name = productToUpdate.Name;
catalogItem.Description = productToUpdate.Description;
catalogItem.Price = productToUpdate.Price;
catalogItem.PictureFileName = productToUpdate.PictureFileName;
catalogItem.CatalogTypeId = productToUpdate.CatalogTypeId;
catalogItem.CatalogBrandId = productToUpdate.CatalogBrandId;
catalogItem.AvailableStock = productToUpdate.AvailableStock;
catalogItem.RestockThreshold = productToUpdate.RestockThreshold;
catalogItem.MaxStockThreshold = productToUpdate.MaxStockThreshold;
```

**Change 4:** Update price comparison (line 348):
```csharp
var priceEntry = services.Context.Entry(catalogItem).Property(i => i.Price);
if (priceEntry.IsModified)
```

**Commit message:** `security: fix mass assignment vulnerability in UpdateItem endpoint`

---

### Step 5: Add authorization to write endpoints

**File:** `src/Catalog.API/Apis/CatalogApi.cs`

Add `.RequireAuthorization()` to each mutating endpoint:

**Line 103-106 (CreateItem):**
```csharp
api.MapPost("/items", CreateItem)
    .WithName("CreateItem")
    .WithSummary("Create a catalog item")
    .WithDescription("Create a new item in the catalog")
    .RequireAuthorization();
```

**Line 93-97 (UpdateItemV1):**
```csharp
v1.MapPut("/items", UpdateItemV1)
    .WithName("UpdateItem")
    .WithSummary("Create or replace a catalog item")
    .WithDescription("Create or replace a catalog item")
    .WithTags("Items")
    .RequireAuthorization();
```

**Line 98-102 (UpdateItem):**
```csharp
v2.MapPut("/items/{id:int}", UpdateItem)
    .WithName("UpdateItem-V2")
    .WithSummary("Create or replace a catalog item")
    .WithDescription("Create or replace a catalog item")
    .WithTags("Items")
    .RequireAuthorization();
```

**Line 107-110 (DeleteItemById):**
```csharp
api.MapDelete("/items/{id:int}", DeleteItemById)
    .WithName("DeleteItem")
    .WithSummary("Delete catalog item")
    .WithDescription("Delete the specified catalog item")
    .WithTags("Items")
    .RequireAuthorization();
```

**Commit message:** `security: require authorization for Catalog API write operations`

---

### Step 6: Update functional tests

**File:** `tests/Catalog.FunctionalTests/CatalogApiTests.cs`

**Add test helper for authenticated client:**
```csharp
private HttpClient CreateAuthenticatedHttpClient(ApiVersion apiVersion)
{
    var client = CreateHttpClient(apiVersion);
    // Add mock JWT token for testing (use test fixture's auth setup)
    client.DefaultRequestHeaders.Authorization = 
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");
    return client;
}
```

**Update existing tests (lines 50-422):**

1. **UpdateCatalogItemWorksWithoutPriceUpdate** (line 50): Change `_httpClient` to use authenticated client
2. **UpdateCatalogItemWorksWithPriceUpdate** (line 85): Change `_httpClient` to use authenticated client
3. **AddCatalogItem** (line 359): Change `_httpClient` to use authenticated client
4. **DeleteCatalogItem** (line 401): Change `_httpClient` to use authenticated client

**Update test data to use DTO:**
- Line 62, 98: Change `itemToUpdate` type handling to map to `UpdateCatalogItemRequest`
- Line 370-383: Update `bodyContent` to be `UpdateCatalogItemRequest` instead of `CatalogItem`

**Add new authorization tests:**
```csharp
[Theory]
[InlineData(1.0)]
[InlineData(2.0)]
public async Task CreateItemRequiresAuthentication(double version)
{
    var _httpClient = CreateHttpClient(new ApiVersion(version)); // Unauthenticated

    var bodyContent = new UpdateCatalogItemRequest(
        Name: "TestItem",
        Description: "Test",
        Price: 10.0m,
        PictureFileName: null,
        CatalogTypeId: 1,
        CatalogBrandId: 1,
        AvailableStock: 10,
        RestockThreshold: 5,
        MaxStockThreshold: 20
    );

    var response = await _httpClient.PostAsJsonAsync("/api/catalog/items", bodyContent, TestContext.Current.CancellationToken);
    
    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
}

[Theory]
[InlineData(1.0)]
[InlineData(2.0)]
public async Task UpdateItemRequiresAuthentication(double version)
{
    var _httpClient = CreateHttpClient(new ApiVersion(version)); // Unauthenticated

    var updateRequest = new UpdateCatalogItemRequest(
        Name: "Updated",
        Description: "Test",
        Price: 10.0m,
        PictureFileName: null,
        CatalogTypeId: 1,
        CatalogBrandId: 1,
        AvailableStock: 10,
        RestockThreshold: 5,
        MaxStockThreshold: 20
    );

    var response = version switch
    {
        1.0 => await _httpClient.PutAsJsonAsync("/api/catalog/items", updateRequest, TestContext.Current.CancellationToken),
        2.0 => await _httpClient.PutAsJsonAsync("/api/catalog/items/1", updateRequest, TestContext.Current.CancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(version))
    };
    
    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
}

[Theory]
[InlineData(1.0)]
[InlineData(2.0)]
public async Task DeleteItemRequiresAuthentication(double version)
{
    var _httpClient = CreateHttpClient(new ApiVersion(version)); // Unauthenticated

    var response = await _httpClient.DeleteAsync("/api/catalog/items/1", TestContext.Current.CancellationToken);
    
    Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
}
```

**Commit message:** `tests: update Catalog functional tests for authentication requirements`

---

### Step 7: Update test fixture for authentication

**File:** `tests/Catalog.FunctionalTests/CatalogApiFixture.cs` (examine first)

Add JWT bearer test authentication to the fixture (use `Microsoft.AspNetCore.Authentication.JwtBearer` test helpers).

Example pattern from other eShop tests:
```csharp
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.ConfigureTestServices(services =>
    {
        services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });
    });
}
```

**Commit message:** `tests: add test authentication scheme to Catalog.FunctionalTests`

---

### Step 8: Documentation update

**File:** `src/Catalog.API/README.md` (create if doesn't exist)

Document the authorization requirements:
```markdown
# Catalog API

## Authorization

Write operations (Create, Update, Delete) require a valid JWT token with the `catalog` scope.

- **Scope:** `catalog`
- **Audience:** `catalog`
- **Authority:** Identity.API

Read operations remain publicly accessible.

To test with Swagger UI, configure OAuth2 authentication (see Identity.API client configuration).
```

**Commit message:** `docs: document Catalog API authorization requirements`

---

## 6. Test Updates Required

### Existing Tests That Will Break

**File:** `tests/Catalog.FunctionalTests/CatalogApiTests.cs`

1. **UpdateCatalogItemWorksWithoutPriceUpdate** (line 50-80) — Will return 401 without auth
2. **UpdateCatalogItemWorksWithPriceUpdate** (line 85-117) — Will return 401 without auth
3. **AddCatalogItem** (line 359-396) — Will return 401 without auth
4. **DeleteCatalogItem** (line 401-422) — Will return 401 without auth

**Why they break:** Currently use anonymous `HttpClient`; will fail with 401 Unauthorized after Step 5.

### New Tests Required

**Test Scenarios:**

1. **Unauthenticated requests return 401:**
   - `CreateItemRequiresAuthentication` — POST without token → 401
   - `UpdateItemRequiresAuthentication` — PUT without token → 401
   - `DeleteItemRequiresAuthentication` — DELETE without token → 401

2. **Authenticated requests succeed (200/201):**
   - Modify existing tests to use authenticated client
   - Verify CreateItem returns 201
   - Verify UpdateItem returns 201
   - Verify DeleteItem returns 204

3. **Mass assignment protection:**
   - `UpdateItemDoesNotAcceptEmbeddingField` — Verify sending `Embedding` in DTO fails with 400 (or is ignored)
   - `UpdateItemDoesNotAcceptIdField` — Verify sending `Id` in DTO is ignored (ID comes from route)
   - `UpdateItemRecomputesEmbedding` — Verify embedding is regenerated server-side after name change

4. **Read operations remain public:**
   - Verify `GetAllItems`, `GetItemById` still work without authentication (no regression)

### Test Fixture Changes

**File:** `tests/Catalog.FunctionalTests/CatalogApiFixture.cs`

- Add test authentication scheme (`TestAuthHandler`) that accepts any token
- Or use `Microsoft.AspNetCore.Mvc.Testing` with `WithWebHostBuilder` to override auth
- Provide helper method: `CreateAuthenticatedClient()` that injects mock JWT

**Pattern:**
```csharp
services.AddAuthentication(defaultScheme: "TestScheme")
    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", options => { });
```

---

## 7. Risk Assessment

### What Could Break

1. **Swagger UI / OpenAPI testing:**
   - **Impact:** Developers won't be able to test write endpoints via Swagger without OAuth2 flow
   - **Mitigation:** Document manual `curl` commands with JWT tokens; add `catalogswaggerui` client later
   - **Likelihood:** HIGH

2. **WebApp client missing "catalog" scope:**
   - **Impact:** WebApp admin portal can't manage catalog items
   - **Mitigation:** Step 1 adds scope to `webapp` client; verify in Integration tests
   - **Likelihood:** MEDIUM (only if Step 1 is incomplete)

3. **Internal services calling Catalog API mutations:**
   - **Impact:** Unknown callers may exist (data migration scripts, admin tools, CI/CD pipelines)
   - **Mitigation:** Audit codebase for `POST /api/catalog/items`, `PUT /api/catalog/items`, `DELETE /api/catalog/items` calls
   - **Likelihood:** LOW (CatalogAPI is primarily consumed via reads; mutations are rare)

4. **Functional tests become flaky:**
   - **Impact:** CI/CD pipeline breaks if test auth setup is incorrect
   - **Mitigation:** Step 7 adds proper test fixture; run locally before committing
   - **Likelihood:** MEDIUM

5. **Backward compatibility with API clients:**
   - **Impact:** External integrations (if any) using v1.0 API will break
   - **Mitigation:** eShop is reference architecture (no production external clients); document breaking change
   - **Likelihood:** LOW (internal reference app)

### Callers to Audit

**Search for:**
```bash
# POST to catalog
git grep -n "POST.*catalog/items" --or -n "PostAsJsonAsync.*catalog/items"

# PUT to catalog
git grep -n "PUT.*catalog/items" --or -n "PutAsJsonAsync.*catalog/items"

# DELETE to catalog
git grep -n "DELETE.*catalog/items" --or -n "DeleteAsync.*catalog/items"
```

**Known callers:**
- `CatalogApiTests.cs` — Covered in Step 6
- No other services in eShop mutate catalog (they subscribe to events instead)

### Verification Checklist

After implementation, verify:

1. ✅ **Unauthenticated write requests return 401**
   ```bash
   curl -X POST http://localhost:5101/api/catalog/items -d '{}' -H "Content-Type: application/json"
   # Expected: 401 Unauthorized
   ```

2. ✅ **Authenticated requests succeed**
   ```bash
   # Get token from Identity.API first
   TOKEN=$(curl -X POST http://localhost:5105/connect/token -d "client_id=webapp&client_secret=secret&grant_type=client_credentials&scope=catalog")
   curl -X POST http://localhost:5101/api/catalog/items -H "Authorization: Bearer $TOKEN" -d '{...}'
   # Expected: 201 Created
   ```

3. ✅ **Read operations still public**
   ```bash
   curl http://localhost:5101/api/catalog/items
   # Expected: 200 OK (no auth required)
   ```

4. ✅ **Mass assignment blocked**
   - Send `Embedding` field in UpdateItem → Should be ignored
   - Verify embedding is regenerated server-side (check logs or DB)

5. ✅ **All functional tests pass**
   ```bash
   dotnet test tests/Catalog.FunctionalTests/
   ```

6. ✅ **WebApp can manage catalog**
   - Login to WebApp as admin
   - Navigate to catalog management
   - Verify Create/Update/Delete work

---

## 8. Rollout Recommendation

### Deployment Order

**Phase 1: Identity.API first**
1. Deploy `Identity.API` with new "catalog" scope (Step 1)
2. Verify `webapp` client gets updated scopes
3. **No downtime:** Backward compatible (existing tokens still work)

**Phase 2: Catalog.API with feature flag (RECOMMENDED)**
1. Add feature flag: `CatalogAuthEnabled` (default: `false`)
2. Deploy Catalog.API with auth code, but disabled
3. Run integration tests against production
4. Enable flag: `CatalogAuthEnabled=true`
5. Monitor for 401 errors; rollback if unexpected callers appear

**Alternative: Big bang deployment**
1. Deploy Identity.API + Catalog.API together
2. Run smoke tests immediately after deploy
3. **Risk:** Higher chance of breaking unknown callers

**Recommendation:** Use **Phase 2 (feature flag)** if this goes to a production-like environment. For reference architecture / dev environment, **big bang is acceptable**.

### Feature Flag Implementation (Optional)

**File:** `src/Catalog.API/appsettings.json`
```json
"FeatureFlags": {
  "CatalogAuthEnabled": false
}
```

**File:** `src/Catalog.API/Apis/CatalogApi.cs`
```csharp
var authRequired = builder.Configuration.GetValue<bool>("FeatureFlags:CatalogAuthEnabled");

if (authRequired)
{
    api.MapPost("/items", CreateItem).RequireAuthorization();
    // ... etc
}
else
{
    api.MapPost("/items", CreateItem);
    // ... etc
}
```

**Rollout Timeline:**
- Day 0: Deploy with flag OFF → Monitor for issues
- Day 1: Enable flag in dev environment → Run full test suite
- Day 3: Enable flag in staging → Monitor logs for 401s
- Day 5: Enable flag in production → Monitor and keep rollback plan ready

**Rollback Plan:**
- Set `CatalogAuthEnabled=false` in configuration
- Restart Catalog.API pods/instances
- No code deployment needed

---

## Summary

This plan secures the Catalog API against two vulnerabilities:

1. **Missing Authentication (LOW risk):**
   - Add `.RequireAuthorization()` to Create, Update, Delete endpoints
   - Leverage existing eShop Identity.API infrastructure
   - Introduce "catalog" scope following eShop's scope-per-service pattern

2. **Mass Assignment (MEDIUM risk):**
   - Replace direct entity binding with `UpdateCatalogItemRequest` DTO
   - Prevent client manipulation of `Embedding` vectors
   - Preserve semantic search integrity

**Total Effort Estimate:** 4-6 hours (includes testing and verification)

**Breaking Change:** Yes — unauthenticated write requests will fail (expected behavior)

**Backward Compatibility:** Read operations unchanged; write operations now require "catalog" scope

**Next Steps:**
1. Review this plan with team
2. Create tracking issue (link this doc)
3. Execute Steps 1-8 in order
4. Run verification checklist
5. Deploy using recommended rollout strategy

---

**Reviewed by:** _[Pending]_  
**Approved by:** _[Pending]_  
**Implementation Start Date:** _[TBD]_  
**Target Completion:** _[TBD]_
