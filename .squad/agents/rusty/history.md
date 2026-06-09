# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

### 2026-04-10: Initial Architectural Analysis

**Service Topology:**
- 5 microservices (Basket, Catalog, Identity, Ordering, Webhooks), 2 background processors (OrderProcessor, PaymentProcessor)
- Infrastructure: PostgreSQL (pgvector), Redis, RabbitMQ
- AppHost orchestration via .NET Aspire (`src/eShop.AppHost/Program.cs`)
- Service discovery via Aspire DNS-based resolution (`http://service-name`)

**Communication Patterns:**
- **Sync:** gRPC (Basket), HTTP with service discovery (Catalog, Ordering)
- **Async:** RabbitMQ event bus with direct exchange, routing key = event type name
- **Event Bus:** `src/EventBusRabbitMQ/RabbitMQEventBus.cs` with OpenTelemetry trace propagation

**Key Patterns:**
- **Outbox:** `IntegrationEventLogEF` for transactional event publishing (Catalog, Ordering)
- **CQRS:** Ordering.API with MediatR commands, Dapper read models
- **DDD:** Ordering.Domain with aggregates (Order, Buyer), value objects, domain events
- **BFF:** YARP mobile-bff proxy at `src/eShop.AppHost/Extensions.cs` (ConfigureMobileBffRoutes)
- **Resilience:** Polly via `AddStandardResilienceHandler()` in ServiceDefaults

**Cross-Cutting Infrastructure:**
- **eShop.ServiceDefaults:** OpenTelemetry, health checks, JWT auth, service discovery
- **EventBus:** Abstract IEventBus with RabbitMQ implementation, keyed services for handlers
- **Shared:** MigrateDbContextExtensions for auto-migrations (dev only)

**Authentication Flow:**
- Identity.API (Duende IdentityServer 7.3) → JWT tokens → API services
- WebApp uses OIDC + cookies (60 min session)
- Service-to-service: JWT bearer with token propagation

**AI Integration (Optional):**
- Catalog: OpenAI/Ollama embeddings for semantic search (pgvector)
- WebApp: OpenAI/Ollama chat for customer support

**Build Configuration:**
- Central package management: `Directory.Packages.props` (Aspire 13.2, .NET 9)
- Artifacts output: `UseArtifactsOutput=true`
- API versioning via Asp.Versioning (query parameter-based)

**File Paths for Reference:**
- AppHost: `src/eShop.AppHost/Program.cs`
- Service Defaults: `src/eShop.ServiceDefaults/Extensions.cs`
- Event Bus: `src/EventBusRabbitMQ/RabbitMQEventBus.cs`, `src/EventBus/`
- Outbox: `src/IntegrationEventLogEF/Services/IntegrationEventLogService.cs`
- DDD: `src/Ordering.Domain/SeedWork/`, `src/Ordering.Domain/AggregatesModel/`
- MediatR: `src/Ordering.API/Application/Behaviors/`

**Technical Debt Notes:**
- Developer signing credentials in Identity.API (not prod-ready)
- Auto-migrations on startup (should use SQL scripts in prod)
- Security warnings suppressed for transitive packages (`NU1901-NU1904`)

### 2026-04-10: Product Recommendations Architecture

**AI Infrastructure Deep Dive:**
- **ICatalogAI / CatalogAI**: Mature abstraction for embedding generation
  - Configurable backend: OpenAI (text-embedding-3-small) or Ollama (all-minilm)
  - Configured via AppHost Extensions.cs (AddOpenAI / AddOllama methods)
  - Embedding dimensions: 384 floats
  - IsEnabled pattern for graceful degradation
  - Microsoft.Extensions.AI abstraction (IEmbeddingGenerator<string, Embedding<float>>)
- **pgvector Integration**: Postgres extension for vector storage
  - CatalogItem.Embedding is Pgvector.Vector type
  - Cosine distance queries: `c.Embedding!.CosineDistance(vector)`
  - Indexed for performance
  - Auto-populated on item create/update via CatalogAI
- **Existing Semantic Search**: `/api/catalog/items/withsemanticrelevance` endpoint
  - Generates embedding from search text
  - Orders by cosine similarity
  - Falls back to name search if AI disabled

**Session/State Management Patterns:**
- **BasketState Pattern**: Scoped service with in-memory cache, cache invalidation on mutations
- **User Identity**: "sub" claim from JWT (buyerId), retrieved via AuthenticationStateProvider
- **Authentication Check**: `HttpContext?.User.Identity?.IsAuthenticated`
- **Redis Already Available**: Used by Basket.API, can be shared with Catalog.API for browsing history

**API Design Consistency:**
- Endpoint registration: `api.MapGet/MapPost` with `HasApiVersion(1, 0)`
- Service injection: `[AsParameters] CatalogServices` pattern (bundles Context, AI, Logger, EventService)
- Response types: `Results<Ok<T>, NotFound, BadRequest<ProblemDetails>>`
- Pagination: Reusable `PaginationRequest` and `PaginatedItems<T>` types
- Error handling: ProblemDetails for 400/404, graceful fallback for AI failures

**Architecture Decision - Recommendations v1:**
- **Service Placement**: Add to Catalog.API (not new microservice) - recommendations tightly coupled to catalog data
- **Storage**: Redis LIST for browsing history (key: `browsing_history:{userId}`, 50 item cap, 30-day TTL)
- **Algorithm**: Centroid-based similarity (average embeddings of viewed items, query by cosine distance)
- **Exclusions**: Viewed items, out-of-stock items (AvailableStock <= 0)
- **Fallback**: Same CatalogType as recent view (AI disabled) or newest items (no history)
- **User Scope**: Authenticated users only (v1), defer anonymous tracking to v2
- **Frontend**: ProductRecommendations.razor carousel component on ItemPage, below product details
- **Testing**: Add to Catalog.FunctionalTests, test auth, fallback, exclusions, AI disabled modes

**Key Files for Recommendations:**
- Backend: `RecommendationService.cs`, `RecommendationApi.cs`, `RecommendationOptions.cs`
- Frontend: `ProductRecommendations.razor`, updates to `ItemPage.razor` and `CatalogService.cs`
- Infrastructure: Redis reference added to catalogApi in AppHost, config in appsettings.json
- Tests: Functional tests in `Catalog.FunctionalTests/CatalogApiTests.cs`

### 2026-04-10: Recommendations Feature Architecture Design

**Team Leadership:**
Designed and documented comprehensive product recommendations feature architecture for implementation by Linus (backend), Livingston (frontend), and Basher (testing).

**Architecture Decisions Documented:**
- Service placement in Catalog.API with Redis browsing history (50-item cap, 30-day TTL)
- Centroid-based similarity algorithm using pgvector cosine distance
- Fallback chain: AI embeddings → same CatalogType → newest items
- Authenticated users only scope (v1), defer anonymous tracking to v2
- ProductRecommendations carousel component on ItemPage below product details

**File Structure Provided:**
- Backend: 5 new files (IRecommendationService, RecommendationService, RecommendationOptions, BrowsingHistoryItem, RecommendationApi) + 5 modified files (Program.cs, Extensions.cs, appsettings.json, Catalog.API.csproj, AppHost Program.cs)
- Frontend: 2 new components (ProductRecommendations.razor, ProductRecommendations.razor.css) + 3 service/page modifications
- Tests: 2 test files (6 functional + 4 unit tests)

**Outcome:** Clear API contracts, implementation roadmap, and team coordination completed. All agents successfully delivered on scope.

### 2026-05-19: Architecture Security Review & Catalog Auth Planning

**Role:** Lead Architect conducting enterprise security review with specialized security-focused agents (rusty-security, linus-security, livingston-security, basher-security). Additional planning agents for Catalog API authorization (rusty-plan-catalog-auth, linus-catalog-auth-analysis).

**Security Findings (4 Critical, 10 High/Medium):**
- Critical: Card data in JWT claims (PII exposure), IDOR on GetOrderAsync, tempkey.jwk in git history, missing Catalog authorization
- High: No authorization tests, hardcoded secrets, open redirect, mass assignment, webhook SSRF

**Catalog API Authorization Plan (Confidence: 88%):**
- Drafted comprehensive implementation plan to docs/security/plan-catalog-api-auth.md
- Plan covers: Identity.API scope additions, Catalog.API auth configuration, test infrastructure updates, mass assignment risk mitigation
- Confidence adjusted from 92% to 88% based on complexity revealed during deep-dive analysis

**Architecture Leadership Output:**
- Coordinated 6-agent security fan-out for comprehensive coverage
- Synthesized findings into 5 critical remediation priorities
- Provided clear implementation roadmap for security hardening
- Documented architectural patterns for future reference

### 2026-05-28: Security Skills Enhancement Council - Consensus Proposal

**Role:** Lead Architect convening cross-functional council to address security skills gaps identified in May 2026 red-team exercise.

**Council Participants:**
- **Tess (Security Specialist)**: Identified 6 critical vulnerabilities (path traversal, mass assignment, cache poisoning, unsafe async, PII logging, user input in logs)
- **Basher (QA/Testing)**: Documented 10 exploitable attack vectors with concrete reproduction steps, provided 8 recommended hardening test cases, identified missing authorization test infrastructure
- **Mullins (Code Disrupter)**: Injected test vulnerabilities into Catalog.API request handlers and recommendation services to support detection exercises
- **Linus (Backend)**: Expertise on API security, secrets management, input validation patterns
- **Livingston (Frontend)**: Expertise on client-side security, authentication flows, XSS prevention

**Problem Identified:**
While team successfully detected injected vulnerabilities through reactive analysis, we lacked:
- Proactive security training (knowledge concentrated in Tess, not distributed)
- Systematic detection practices (found issues only through deliberate red-team exercise)
- Clear ownership model (no security champion per domain)
- Continuous improvement mechanisms (no training cadence or skill progression tracking)

**Consensus Proposal Delivered:**
Comprehensive 12-month security skills enhancement program covering:

**1. Training Cadence:**
- Monthly 2-hour security deep-dives with rotating topics (OWASP Top 10, API security, infrastructure)
- Quarterly certification checkpoints (Security Aware → Proficient → Champion progression)
- Weekly 15-minute security stand-ups for CVE triage and tech debt tracking
- On-demand resources library (cheat sheets, recorded sessions, daily security snippets)

**2. Red-Team Exercises:**
- Quarterly full-day vulnerability injection exercises (8-12 injected flaws, 4-hour detection sprint)
- Balanced difficulty: 3 easy, 5 medium, 3 hard vulnerabilities per exercise
- Scoring metrics: Detection rate (target 90%+), time to detect (<2 hours), false positive rate (<20%)
- Monthly 30-minute "fire drill" scenarios for incident response training

**3. Detection Practices:**
- Mandatory security checklist for all PRs (developer self-review + peer verification)
- Automated SAST in CI pipeline (Security Code Scan, Semgrep, Dependabot)
- Security test patterns library (auth/authz, input validation, IDOR templates)
- Pre-merge requirements: Functional tests include 401/403 scenarios, API tests validate input sanitization

**4. Ownership Model:**
- **Rusty**: Cross-cutting architecture, threat modeling, quarterly roadmap
- **Tess**: Vulnerability research, CVE reviews, tooling, training facilitation
- **Basher**: Security test coverage, exploit validation, red-team coordination
- **Linus**: Backend API security, secrets management, SAST remediation
- **Livingston**: Client-side security, CSP, authentication flows
- **Mullins**: Red-team injection, attack simulation, emerging threat research
- Escalation path: Detection → Triage (Tess) → Remediation (domain owner) → Validation (Basher) → Retrospective (Rusty)

**5. Implementation Sequence:**
- **Phase 1 (Months 1-2)**: Foundation - Deploy CI tooling, establish baseline literacy, run first 2 training sessions
- **Phase 2 (Months 3-4)**: Operationalization - First red-team exercise, embed security in PR workflow, achieve Security Aware certification
- **Phase 3 (Months 5-6)**: Maturity - Remediate SAST baseline, run Q2 red-team with 90%+ detection rate, achieve Security Proficient certification
- **Phase 4 (Months 7-12)**: Continuous Improvement - Advanced topics, Security Champion development, sustain excellence

**Success Metrics:**
- 90% of injected vulnerabilities detected within 48 hours during quarterly exercises
- 100% of PRs include security considerations in code review checklist
- Zero critical security issues reach main branch undetected
- 80% of team achieves Security Proficient certification by Month 6

**Resource Investment:**
- Time: ~73 hours/person/year (~3.5% of annual work time)
- Tooling: Minimal cost (OSS tools + O'Reilly subscription $2K/year)
- Optional: External penetration test in Year 2 ($15-25K)

**Output:**
- Consensus proposal written to `.squad/decisions/inbox/security-skills-consensus-proposal.md`
- Awaiting Chris Daly approval for Phase 1 kickoff

**Key Architectural Insight:**
Security is a team capability, not individual expertise. Distributed ownership with clear escalation paths, combined with continuous skill development and realistic adversarial exercises, transforms reactive vulnerability hunting into proactive security-by-design culture.

<!-- Append new learnings below. Each entry is something lasting about the project. -->
