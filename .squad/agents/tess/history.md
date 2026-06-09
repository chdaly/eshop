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

---

### 2026-05-28: Security Skills Council Convened

**Context:** Mullins feedback revealed team identified vulnerabilities but did not remediate them.

**Key Learnings:**

1. **Gap Between Detection and Remediation**
   - Team can identify security issues but lacks implementation patterns for fixes
   - "Unsafe" method naming convention indicates awareness without accountability
   - Need: Approved remediation patterns + automated enforcement

2. **Critical Security Pattern Gaps**
   - Path traversal: Missing `Path.GetFileName()` validation and allowlisting
   - Mass assignment: Missing DTO mapping and property allowlisting
   - Cache injection: Missing input sanitization even for authenticated sources
   - Fire-and-forget async: Missing lifecycle management and background job patterns
   - PII in logs: Missing GDPR-compliant logging practices (masking, hashing)

3. **Root Cause: Process + Training Gaps**
   - No mandatory security review gates in PR process
   - No automated static analysis in CI/CD pipeline
   - No team-wide OWASP Top 10 training
   - Security knowledge concentrated in one person (Tess)

4. **Proposed Solution: 4-Phase Security Improvement Plan**
   - Phase 1: Immediate remediation of critical vulnerabilities (Sprint 1)
   - Phase 2: Team-wide OWASP Top 10 workshop (Sprint 1-2)
   - Phase 3: Process integration (security gates, automated scanning, test templates) (Sprint 2-3)
   - Phase 4: Continuous improvement (monthly retros, security champions, threat modeling) (Ongoing)

5. **Success Metrics**
   - 100% vulnerability remediation within 1 sprint
   - ≥80% security test coverage for sensitive endpoints
   - Zero critical/high findings in CI/CD
   - 100% team trained on OWASP Top 10
   - MTTD < 1 day via PR/automated scanning

**Files Reviewed:**
- `src/Catalog.API/Apis/CatalogApi.cs` (path traversal, mass assignment, PII logging)
- `src/Catalog.API/Apis/RecommendationApi.cs` (fire-and-forget async)
- `src/Catalog.API/Services/RecommendationService.cs` (cache injection, PII logging)

**Deliverable:** `.squad/decisions/inbox/tess-security-skills-council.md` - Consensus proposal for Chris

**Next Actions:**
- Chris: Approve 4-phase plan
- Tess: Create `.squad/security-patterns.md` remediation guide
- Linus: Implement path traversal fix with tests
- Tess: Implement mass assignment fix with tests
- Basher: Build security test framework templates

### 2026-06-09: Catalog.API security remediation completed

**Architecture / Patterns:**
- Catalog image path resolution now canonicalizes file names with `Path.GetFileName()` and rejects traversal/absolute path input before serving files.
- Recommendation view tracking now uses `ViewTrackingBackgroundService` + bounded in-memory channel instead of `Task.Run` fire-and-forget work.
- Recommendation user IDs are validated before Redis key construction and redacted before logging; search text is sanitized before debug logging.
- Catalog updates continue through explicit allowlisted property mapping only; unsafe mass-assignment helper was removed.

**Key File Paths:**
- `src/Catalog.API/Apis/CatalogApi.cs`
- `src/Catalog.API/Apis/RecommendationApi.cs`
- `src/Catalog.API/Services/CatalogSecurity.cs`
- `src/Catalog.API/Services/RecommendationService.cs`
- `src/Catalog.API/Services/ViewTrackingBackgroundService.cs`
- `src/Catalog.API/Extensions/Extensions.cs`
- `tests/Catalog.FunctionalTests/CatalogSecurityTests.cs`
- `tests/Catalog.FunctionalTests/RecommendationSecurityTests.cs`
- `tests/Catalog.FunctionalTests/RecommendationServiceTests.cs`

**Validation:**
- `dotnet build .\\src\\Catalog.API\\Catalog.API.csproj`
- `dotnet test --project .\\tests\\Catalog.FunctionalTests\\Catalog.FunctionalTests.csproj --filter-class eShop.Catalog.FunctionalTests.CatalogApiSecurityUnitTests --filter-class eShop.Catalog.FunctionalTests.RecommendationServiceTests --filter-class eShop.Catalog.FunctionalTests.RecommendationSecurityTests`
- Full Catalog.FunctionalTests remains environment-limited here because Docker/container-backed Aspire fixtures are unavailable.
