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

<!-- Append new learnings below. Each entry is something lasting about the project. -->
