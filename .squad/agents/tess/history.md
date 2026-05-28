# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

### 2026-05-28: Added dedicated C# security specialist role for threat review and hardening.

### 2026-05-28: Security Review - Catalog API and Recommendations

**Files Reviewed:**
- `src/Catalog.API/Apis/CatalogApi.cs`
- `src/Catalog.API/Apis/RecommendationApi.cs`
- `src/Catalog.API/Services/RecommendationService.cs`

**Critical Findings:**

1. **Path Traversal Vulnerability (CatalogApi.cs, line 218 & 434-435)**
   - `ResolveUnsafePicturePath` concatenates `contentRootPath` with user-controlled `PictureFileName` without validation
   - Attacker could provide `../../../etc/passwd` or similar to read arbitrary files from the server
   - Method name admits the risk ("Unsafe"), but protection is missing
   - Impact: Arbitrary file read, potential sensitive data exposure

2. **Mass Assignment Risk (CatalogApi.cs, line 431-432)**
   - `ApplyUnsafeCatalogUpdate` uses `SetValues()` to blindly copy all properties from request to entity
   - No allowlist of updatable fields - attacker could modify protected fields (e.g., Id, audit timestamps, or other internal properties)
   - Method name admits the risk ("Unsafe")
   - Impact: Unauthorized modification of protected entity properties

3. **User-Controlled Data in Cache Keys (RecommendationService.cs, line 220)**
   - `GetBrowsingHistoryKey` directly concatenates user ID from claims into Redis key: `browsing_history:{userId}`
   - While userId comes from authenticated claims (sub), no sanitization of special characters
   - If userId contains Redis command delimiters or special chars, could lead to cache pollution or key confusion
   - Impact: Cache key collision, potential data leakage between users

4. **Unsafe Fire-and-Forget Async (RecommendationApi.cs, lines 86-99)**
   - `QueueViewTracking` uses `Task.Run` without awaiting or proper lifecycle management
   - Exceptions are logged but failures are silent to caller - no retry mechanism
   - Task could be abandoned during app shutdown, losing tracking data
   - Impact: Data loss, inconsistent state, memory leaks from abandoned tasks

5. **Structured Logging with User Input (CatalogApi.cs, line 423-426)**
   - `EmitSearchDiagnostics` logs raw user search text without sanitization: `{SearchText}`
   - While structured logging helps, search text could contain sensitive PII or injection attempts captured in logs
   - Logs may be forwarded to external systems where this data persists
   - Impact: PII exposure in logs, log injection attacks

6. **User Input in Logs (RecommendationService.cs, lines 35, 54, 73)**
   - Raw `userId` (from claims) logged in multiple error scenarios
   - While userId is typically a GUID, if claims are misconfigured or spoofed, could log sensitive identifiers
   - Impact: PII exposure in centralized logging systems

**Recommendations:**
- Implement path validation/allowlisting for PictureFileName (e.g., regex check, Path.GetFileName())
- Replace mass assignment with explicit property mapping or DTOs with only allowed fields
- Sanitize/validate userId before using in Redis keys (alphanumeric check)
- Replace fire-and-forget with background job queue (IHostedService, BackgroundService) or at minimum use Task continuation
- Consider log masking or hashing for user-controlled search terms and user identifiers

---

### 2026-05-28: Security Review Complete (with Basher)

**Outcome:** Security review findings documented and consolidated.

**Orchestration Log:** `.squad/orchestration-log/2026-05-28-121408-security-review.md`

**Session Log:** `.squad/log/2026-05-28-121408-security-review.md`
