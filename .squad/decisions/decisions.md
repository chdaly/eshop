# eShop Architecture & Technical Decisions

**Date:** 2026-04-10  
**Status:** Initial Exploration Complete  
**Contributing Agents:** Rusty (Architecture), Linus (Backend), Livingston (Frontend), Basher (Testing)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Architecture Overview](#architecture-overview)
3. [Service Topology](#service-topology)
4. [Backend Architecture](#backend-architecture)
5. [Frontend Architecture](#frontend-architecture)
6. [Test Infrastructure](#test-infrastructure)
7. [Cross-Cutting Concerns](#cross-cutting-concerns)
8. [Technical Debt & Recommendations](#technical-debt--recommendations)
9. [Decision Points for Future Work](#decision-points-for-future-work)

---

## Executive Summary

eShop is a reference .NET 9 microservices application demonstrating modern cloud-native patterns using .NET Aspire for orchestration. The architecture follows Domain-Driven Design (DDD) principles, event-driven communication, and the Outbox pattern for reliable messaging. It comprises 11 microservices, 2 background processors, and 4 frontend applications serving as a comprehensive learning resource for building scalable, maintainable cloud-native applications.

**Key Stats:**
- 11 microservices + 2 background processors
- ~35% codebase test coverage (92 .NET tests + 3 E2E tests)
- 12 integration events orchestrating eventual consistency
- Multi-database strategy: PostgreSQL, Redis, in-memory
- Three-tier frontend: Blazor Server, MAUI native, MAUI hybrid

---

## Architecture Overview

### Infrastructure Components (Aspire-Orchestrated)

**Core Infrastructure:**
- **PostgreSQL** (ankane/pgvector): Multi-tenant RDBMS with vector search support
  - `catalogdb`: Catalog service data + pgvector embeddings for semantic search
  - `identitydb`: IdentityServer4/Duende users, roles, clients
  - `orderingdb`: Ordering domain aggregates, outbox table
  - `webhooksdb`: Webhook subscriptions and delivery tracking
- **Redis**: Distributed cache for basket state (key-value store)
- **RabbitMQ**: Event bus for async inter-service communication (direct exchange, persistent messages)

**Technology Stack:**
- .NET 9 with C# 12
- .NET Aspire 13.2 for orchestration
- Entity Framework Core 10.0.1
- Duende IdentityServer 7.3
- OpenTelemetry for distributed tracing

---

## Service Topology

### Microservices (HTTP/gRPC APIs)

#### 1. Identity.API (Auth/Federation)
- **Tech:** Duende IdentityServer 7.3, ASP.NET Identity, MVC
- **Database:** PostgreSQL (`identitydb`)
- **Purpose:** OAuth2/OIDC provider, user management, client registration
- **Patterns:** Cookie-based session management, developer signing credentials (dev only)
- **Exposes:** External endpoints for login/logout flows
- **Notes:** Centralized identity for all services; cyclic references to all clients for callback URLs
- **Status:** No auth/authz tests (critical gap)

#### 2. Catalog.API (Product Catalog)
- **Tech:** Minimal APIs, EF Core, Pgvector
- **Database:** PostgreSQL (`catalogdb`) with vector extension
- **Purpose:** Product catalog CRUD, AI-powered semantic search
- **API Versions:** v1, v2 (versioned via Asp.Versioning)
- **AI Integration:** Optional OpenAI/Ollama for text embeddings (all-minilm/text-embedding-3-small)
- **Integration Events:**
  - **Subscribes:** `OrderStatusChangedToAwaitingValidationIntegrationEvent`, `OrderStatusChangedToPaidIntegrationEvent`
  - **Publishes:** `ProductPriceChangedIntegrationEvent`, `OrderStockConfirmedIntegrationEvent`, `OrderStockRejectedIntegrationEvent`
- **Patterns:** Outbox pattern via `IntegrationEventLogEF`, auto-migrations on startup
- **Test Coverage:** ✅ 15 theory-based functional tests covering v1.0/v2.0 API versioning

#### 3. Basket.API (Shopping Cart)
- **Tech:** gRPC service, Redis
- **Database:** Redis (ephemeral state)
- **Purpose:** User shopping cart management (add, update, get, delete)
- **Communication:** gRPC-only (no REST endpoints)
- **Integration Events:**
  - **Subscribes:** `OrderStartedIntegrationEvent` (clears basket when order placed)
- **Auth:** JWT bearer tokens, optional anonymous basket retrieval
- **Notes:** Stateless service; all state in Redis
- **Test Coverage:** ⚠️ Only 3 tests for gRPC service (minimal coverage)

#### 4. Ordering.API (Order Management)
- **Tech:** Minimal APIs, MediatR, FluentValidation, EF Core
- **Database:** PostgreSQL (`orderingdb`)
- **Purpose:** Order lifecycle management (create, cancel, ship), CQRS with read/write separation
- **Domain Model:** DDD with aggregates (`Order`, `Buyer`), value objects, domain events
- **API Versions:** v1
- **Patterns:**
  - **MediatR Pipeline:** Logging → Validation → Transaction behaviors
  - **CQRS:** Separate read models (Dapper queries) and write models (MediatR commands)
  - **Outbox Pattern:** `IntegrationEventLogEF` ensures transactional event publishing
  - **Idempotency:** `RequestManager` prevents duplicate command processing
- **Integration Events:**
  - **Subscribes:** `GracePeriodConfirmedIntegrationEvent`, `OrderStockConfirmedIntegrationEvent`, `OrderStockRejectedIntegrationEvent`, `OrderPaymentSucceededIntegrationEvent`, `OrderPaymentFailedIntegrationEvent`
  - **Publishes:** Order status change events (submitted, paid, shipped, cancelled, awaiting validation, stock confirmed)
- **Auth:** JWT bearer, user identity extraction via `IIdentityService`
- **Test Coverage:** ✅ 8 test classes covering aggregates, commands, handlers, domain events

#### 5. Webhooks.API (Webhook Subscriptions)
- **Tech:** Minimal APIs, EF Core
- **Database:** PostgreSQL (`webhooksdb`)
- **Purpose:** Webhook registration, delivery, and retry logic
- **Integration Events:**
  - **Subscribes:** `ProductPriceChangedIntegrationEvent`, `OrderStatusChangedToShippedIntegrationEvent`, `OrderStatusChangedToPaidIntegrationEvent`
- **Services:** `IWebhooksRetriever`, `IWebhooksSender`, `IGrantUrlTesterService`
- **Status:** No webhook delivery tests (critical gap)

### Background Processors (Hosted Services)

#### 6. OrderProcessor (Grace Period Worker)
- **Tech:** Hosted service (no HTTP endpoints)
- **Database:** PostgreSQL (`orderingdb`) via raw ADO.NET (`NpgsqlDataSource`)
- **Purpose:** Background task to confirm orders after grace period expires
- **Integration Events:**
  - **Publishes:** `GracePeriodConfirmedIntegrationEvent`
- **Patterns:** Polling database for orders in grace period, publishes event when ready
- **Notes:** Waits for `Ordering.API` to apply EF migrations before starting
- **Status:** No integration tests (critical gap)

#### 7. PaymentProcessor (Payment Simulation)
- **Tech:** Hosted service (no HTTP endpoints)
- **Purpose:** Simulates payment gateway, auto-approves/rejects based on config
- **Integration Events:**
  - **Subscribes:** `OrderStatusChangedToStockConfirmedIntegrationEvent`
  - **Publishes:** `OrderPaymentSucceededIntegrationEvent`, `OrderPaymentFailedIntegrationEvent`
- **Config:** `PaymentOptions` controls success rate
- **Status:** No integration tests (critical gap)

### Front-End Applications

#### 8. WebApp (Blazor E-Commerce UI)
- **Tech:** Blazor Server (interactive rendering), Razor Components
- **Purpose:** Customer-facing web store
- **Services:** Calls Basket (gRPC), Catalog (HTTP), Ordering (HTTP)
- **Auth:** OpenID Connect (cookie + OIDC), session lifetime 60 min
- **AI Integration:** Optional OpenAI/Ollama chat for customer support
- **Integration Events:**
  - **Subscribes:** All order status change events (for real-time UI updates via SignalR)
- **Patterns:** Service discovery, HTTP resilience (Polly), gRPC with auth token propagation
- **State Management:** BasketState pattern with change notifications
- **Styling:** Scoped CSS with design tokens (Plus Jakarta Sans, Open Sans)
- **Test Coverage:** ❌ No Blazor component tests (only 3 E2E tests)

#### 9. WebhookClient (Webhook Demo/Test Client)
- **Tech:** Blazor Server
- **Purpose:** Demonstrates webhook subscription and consumption

#### 10. HybridApp (MAUI Hybrid)
- **Tech:** .NET MAUI with Blazor WebView
- **Purpose:** Cross-platform mobile/desktop client using shared Blazor components
- **Components:** Shares from WebAppComponents library
- **Auth Status:** Requires investigation for production scenarios

#### 11. ClientApp (Native MAUI)
- **Tech:** .NET MAUI (Xamarin successor)
- **Purpose:** Native mobile client with MVVM pattern
- **Architecture:** `ViewModelBase` with `ObservableObject` from CommunityToolkit
- **Test Coverage:** ✅ 55 tests for ViewModels and Services with mock dependencies

### API Gateway / BFF

**mobile-bff** (YARP Reverse Proxy)
- **Tech:** YARP (Yet Another Reverse Proxy)
- **Purpose:** Backend-for-Frontend pattern for mobile clients
- **Routes:** Exposes subset of Catalog, Ordering, Identity endpoints with path transformation
- **Pattern:** `/catalog-api/api/catalog/items` → `catalog-api/api/catalog/items` (prefix removal)
- **Notes:** Query parameter-based API versioning enforcement

---

## Backend Architecture

### Domain-Driven Design (Ordering Service)

The Ordering domain implements full DDD tactical patterns:

- **Aggregates:** `Order` and `Buyer` as aggregate roots with strong consistency boundaries
- **Value Objects:** `Address` with structural equality
- **Domain Events:** Seven domain events for order lifecycle
  - `OrderStartedDomainEvent`
  - `OrderStatusChangedToAwaitingValidationDomainEvent`
  - `OrderStatusChangedToStockConfirmedDomainEvent`
  - `OrderStatusChangedToPaidDomainEvent`
  - `OrderStatusChangedToCancelledDomainEvent`
  - `OrderShippedDomainEvent`
  - `OrderCancelledDomainEvent`
- **Entities:** Rich entities with encapsulated behavior, not anemic data holders
- **Repositories:** Abstract data access, expose `IUnitOfWork` for transactional boundaries

### CQRS Pattern

Ordering.API separates:

- **Commands:** MediatR command handlers with behaviors (logging, validation, transactions)
- **Queries:** Direct read models via Dapper-based `OrderQueries`, optimized for presentation
- **Idempotency:** `IdentifiedCommand` pattern prevents duplicate execution via `ClientRequests` table

### Event-Driven Architecture

**RabbitMQ Event Bus:**
- Direct topic exchange with durable queues
- Manual acknowledgment
- Transactional Outbox Pattern via `IntegrationEventLogService` ensures at-least-once delivery
- 12 Integration Events flow between services
- Event States: `NotPublished` → `InProgress` → `Published`/`PublishedFailed`

### Data Access Patterns

| Service | Pattern | Technology |
|---------|---------|-----------|
| Catalog & Webhooks | Direct EF Core (no repository abstraction) | PostgreSQL |
| Basket | Repository pattern | Redis |
| Ordering | Repository + UnitOfWork | PostgreSQL |
| Identity | Standard ASP.NET Core Identity | PostgreSQL |

### Order Lifecycle Flows

#### Order Creation Flow
1. `CreateOrderCommand` → Creates Order aggregate
2. `OrderStartedDomainEvent` → Validates/creates Buyer
3. `OrderStartedIntegrationEvent` → Basket.API clears basket
4. Grace period timer starts

#### Order Fulfillment Flow
1. `GracePeriodConfirmedIntegrationEvent` → Order moves to AwaitingValidation
2. `OrderStatusChangedToAwaitingValidationIntegrationEvent` → Catalog checks stock
3. `OrderStockConfirmedIntegrationEvent` → Order moves to StockConfirmed
4. `OrderStatusChangedToStockConfirmedIntegrationEvent` → PaymentProcessor processes payment
5. `OrderPaymentSucceededIntegrationEvent` → Order moves to Paid
6. `OrderStatusChangedToPaidIntegrationEvent` → Catalog/Webhooks notified
7. `ShipOrderCommand` → Order ships
8. `OrderStatusChangedToShippedIntegrationEvent` → Webhooks notified

### Reliability Patterns

1. **Transactional Outbox:** Events saved in same transaction as domain changes
2. **Idempotency:** Request tracking via `ClientRequests` table prevents duplicate execution
3. **Retry with Backoff:** Polly policies on RabbitMQ publish (exponential backoff)
4. **Manual Ack:** Events removed from queue only after successful processing
5. **Domain Event Dispatch:** Before transaction commit to maintain consistency

---

## Frontend Architecture

### Three-Tier Frontend Strategy

The project employs a sophisticated three-tier frontend architecture:

1. **WebApp (Blazor Server)** — Primary web experience with server-side rendering and real-time updates
2. **ClientApp (MAUI Native)** — Full MVVM native mobile app with platform-specific features
3. **HybridApp (MAUI Blazor)** — Native wrapper around Blazor components for unified web/native experience

### Component Sharing Pattern

**WebAppComponents** serves as the shared component library:

- Contains reusable Razor components (`CatalogListItem`, `CatalogSearch`)
- Provides service abstractions (`ICatalogService`, `IProductImageUrlProvider`)
- Enables code reuse between WebApp and HybridApp
- Uses parameter-based composition with `[EditorRequired]` attributes

### Key Frontend Patterns

#### 1. State Management

**BasketState Pattern:**
- Scoped service with cached basket data
- Change notification via EventCallback subscriptions
- `NotifyOnChange()` returns IDisposable for component lifecycle management
- Cache invalidation on mutations

**Real-time Updates:**
- EventBus integration for order status changes
- `OrderStatusNotificationService` pushes updates to connected clients
- `OrdersRefreshOnStatusChange` component auto-refreshes on events

#### 2. Authentication Flow

**OpenID Connect Integration:**
- Identity.API as authorization server
- Cookie-based sessions with configurable lifetime (60 min)
- `ServerAuthenticationStateProvider` for Blazor auth
- Auth tokens automatically attached to HTTP/gRPC clients via interceptors
- Cascading `AuthenticationState` for components

#### 3. API Integration

**Typed HTTP Clients:**
- Service-specific typed clients (CatalogService, OrderingService)
- Service discovery via Aspire (`https+http://catalog-api`)
- API versioning middleware (`AddApiVersion(2.0)`)
- Automatic auth token injection via `AddAuthToken()`

**gRPC for Basket:**
- Direct gRPC integration with Basket.API
- Performance-optimized for frequent basket operations
- Auth token propagation via custom interceptor

#### 4. Progressive Enhancement

**Stream Rendering:**
- `[StreamRendering]` attribute on pages for progressive loading
- Shows "Loading..." state while fetching data
- Improves perceived performance

**Enhanced Navigation:**
- `data-enhance` forms for Stateful Page Rendering (SPR) behavior
- Prevents full page reloads on form posts
- Maintains Blazor interactivity

#### 5. Styling Architecture

**Scoped CSS Pattern:**
- Component-specific `.razor.css` files
- Automatic scoping via Blazor CSS isolation
- Global styles in `wwwroot/css/app.css`

**Design Tokens:**
- Font families: Plus Jakarta Sans (primary), Open Sans (secondary)
- Color scheme: Black (#000) primary, white (#FFF) secondary
- Consistent button variants and spacing

### Component Inventory

**WebApp Pages:**
- `Catalog.razor` — Product listing with pagination and filters
- `ItemPage.razor` — Product detail with add-to-cart
- `CartPage.razor` — Shopping basket with quantity management
- `Checkout.razor` — Order placement with address form
- `Orders.razor` — Order history with status indicators

**Layout Components:**
- `MainLayout.razor` — Root layout with header, footer, chatbot button
- `HeaderBar.razor` — Navigation with dynamic hero images
- `UserMenu.razor` — Auth dropdown with orders/logout
- `CartMenu.razor` — Cart icon with badge count

**Shared Components (WebAppComponents):**
- `CatalogListItem.razor` — Product card
- `CatalogSearch.razor` — Brand/type filters
- `Chatbot.razor` — AI assistant (optional)

### ClientApp (MAUI) MVVM Patterns

- `ViewModelBase` with `ObservableObject` from CommunityToolkit
- Async initialization via `IAsyncRelayCommand`
- Navigation service abstraction
- Mock/real service switching for development
- Conditional compilation for platform-specific code

---

## Test Infrastructure

### Test Coverage Assessment

**Overall Stats:**
- 92 .NET test methods + 3 E2E tests
- ~35% codebase coverage
- Multiple frameworks: MSTest (66 methods), xUnit (26 cases), Playwright (3 scenarios)

### Tested Services (Good Coverage)

✅ **Catalog.API**
- 15 theory-based functional tests
- Covers v1.0/v2.0 API versioning
- Uses Aspire hosting with PostgreSQL containers

✅ **Ordering.Domain**
- 8 test classes
- Covers aggregates, commands, handlers, domain events
- Strong DDD pattern validation

✅ **ClientApp**
- 55 tests for ViewModels and Services
- Mock dependencies with NSubstitute
- Property change notification verification

### Tested Services (Minimal Coverage)

⚠️ **Basket.API**
- Only 3 tests for gRPC service
- Needs expanded functional test coverage

### Untested Services (Critical Gaps)

❌ **Identity.API** — No auth/authz tests  
❌ **Webhooks.API** — No webhook delivery tests  
❌ **OrderProcessor/PaymentProcessor** — No background service tests  
❌ **WebApp/WebAppComponents** — No Blazor component tests (only 3 E2E tests)  
❌ **EventBus/RabbitMQ** — No messaging infrastructure tests  
❌ **IntegrationEventLogEF** — No event sourcing tests

### Test Infrastructure Patterns

**Functional Test Setup (Impressive):**
- Uses Aspire Hosting with WebApplicationFactory to spin up real dependencies
- Postgres containers via test fixtures (CatalogApiFixture, OrderingApiFixture)
- Tests require Docker running
- AutoAuthorizeMiddleware bypasses auth in Ordering functional tests
- Proper async lifecycle management (IAsyncLifetime)

**Unit Test Patterns:**
- Arrange-Act-Assert consistently applied
- Builder pattern for complex test data (OrderBuilder, AddressBuilder)
- NSubstitute for mocking repositories, mediator, services
- Domain events validated in aggregate tests
- Command idempotency tested via IdentifiedCommandHandler

**E2E Test Setup:**
- Playwright configured to auto-start Aspire AppHost (webServer command)
- Authentication state managed via login.setup.ts
- Separate projects for authenticated vs non-authenticated tests
- 5-minute timeout on CI for app startup

### CI/CD Integration Status

**Current State:**
- Pipeline only runs `dotnet build eShop.Web.slnf`
- **NO test execution in CI pipeline** — **Major gap**
- No code coverage collection
- No test result reporting

### Testing Standards

- Test class naming: `*Tests` or `*Test` suffix
- Test method naming: Descriptive sentences (e.g., `Handle_return_false_if_order_is_not_persisted`)
- Global usings to reduce boilerplate
- Parallel test execution enabled where safe
- Proper async/await patterns in all async tests

---

## Cross-Cutting Concerns

### eShop.ServiceDefaults

Shared library providing:

- **OpenTelemetry:** Traces, metrics, logs with OTLP exporter support
- **Health Checks:** `/health` (all checks), `/alive` (liveness only)
- **Service Discovery:** DNS-based resolution for service-to-service calls
- **HTTP Resilience:** Polly retry policies via `AddStandardResilienceHandler()`
- **Authentication:** JWT bearer setup with configurable Identity URL
- **Forwarded Headers:** Middleware for proxy scenarios

### EventBus Abstraction

- **Interface:** `IEventBus.PublishAsync(IntegrationEvent)`
- **Implementation:** `RabbitMQEventBus` (direct exchange, routing key = event type name)
- **Patterns:**
  - **OpenTelemetry:** Automatic trace propagation via message headers (W3C Trace Context)
  - **Resilience:** Polly retry on publish failures (configurable retry count)
  - **Subscription:** Keyed services pattern for multiple handlers per event type
- **Configuration:** `EventBusOptions` (retry count, subscription client name), JSON serialization options

### IntegrationEventLogEF (Outbox Pattern)

**Purpose:** Transactional outbox for reliable event publishing

**Storage:** `IntegrationEventLogEntry` table in service's DbContext

**Workflow:**
1. Save domain changes + event log entry in same transaction
2. Commit transaction
3. Retrieve pending events by `TransactionId`
4. Publish to event bus
5. Mark as published/failed in log

**State Machine:** `NotPublished` → `InProgress` → `Published`/`PublishedFailed`

### Shared Infrastructure

- **Migrations:** Auto-apply EF migrations on startup via `AddMigration<TContext, TSeed>`
- **API Versioning:** `Asp.Versioning` with query parameter-based versioning (`?api-version=1.0`)
- **OpenAPI:** Scalar UI for interactive API docs (`/scalar/v1`, `/openapi/v1.json`)
- **Service-to-Service Auth:** Token propagation via `AddAuthToken()` extension

---

## Technical Debt & Recommendations

### Known Issues

1. **Identity.API:** Developer signing credentials (not production-ready)
2. **Security Warnings:** Transitive package vulnerabilities suppressed (`NU1901-NU1904`)
3. **Auto-Migrations:** Enabled for dev convenience, should use SQL scripts in production
4. **HTTP Endpoints:** Test-only HTTP mode via `ESHOP_USE_HTTP_ENDPOINTS=1` (Playwright CI)

### Immediate Priorities

1. **Add Tests to CI:** Add `dotnet test` step to ci.yml with result publishing
2. **Identity.API Tests:** Create unit + functional tests for OAuth flows
3. **Background Service Tests:** Add integration tests for OrderProcessor and PaymentProcessor
4. **EventBus Tests:** Add reliability and retry logic tests for RabbitMQ
5. **Webhooks.API Tests:** Add delivery and retry tests

### Strategic Improvements

1. **Keep DDD patterns strong** in Ordering domain
2. **Implement event versioning strategy** for integration events
3. **Add Dead Letter Queue** for failed event processing
4. **Standardize test framework** (decide MSTest vs xUnit)
5. **Expand WebAppComponents** for better frontend code reuse
6. **Add Blazor component tests** with bUnit for WebApp/WebAppComponents
7. **Expand E2E test suite** beyond current 3 scenarios

### Component Testing

- WebAppComponents (Blazor) has zero test coverage
- Consider bUnit for Blazor component testing
- Opportunity to extract more components from WebApp to WebAppComponents

### Monitoring & Observability

- Ensure `IntegrationEventLog` table is monitored for `PublishedFailed` events
- Implement alerting for event publishing failures
- Track grace period timeouts and payment processing latency

---

## Decision Points for Future Work

### When Adding a New Microservice

1. Reference `eShop.ServiceDefaults` for cross-cutting setup
2. Use `AddBasicServiceDefaults()` or `AddServiceDefaults()` based on HTTP client needs
3. Add database reference in `eShop.AppHost/Program.cs`
4. Subscribe to relevant integration events via `AddRabbitMqEventBus().AddSubscription<T, TH>()`
5. Publish events via `IEventBus.PublishAsync()` (consider outbox pattern for transactional events)

### When Adding Integration Events

1. Define event record inheriting `IntegrationEvent` in service's `IntegrationEvents/Events/` folder
2. Create handler implementing `IIntegrationEventHandler<T>` in `IntegrationEvents/EventHandling/`
3. Register subscription in service's `Extensions.cs`
4. If event must be transactional with domain changes, use `IntegrationEventLogEF` and publish in `TransactionBehavior`

### When Modifying Service Communication

- **Sync:** Use HTTP client (Catalog, Ordering) or gRPC (Basket) with service discovery
- **Async:** Use RabbitMQ integration events for eventual consistency
- **Never:** Direct database access across service boundaries

### When Scaling

- **Stateless Services:** Catalog, Ordering, Webhooks scale horizontally (DB connection pooling)
- **Stateful Services:** Basket requires Redis clustering for multi-instance deployment
- **Background Processors:** Single instance only (no distributed locking implemented)

### Event Versioning Strategy (To Be Decided)

- Should we implement event versioning now or wait for first breaking change?
- Is the grace period configurable per environment? (Production likely needs different timing)
- Do we need compensating transactions for failed orders or is cancel acceptable?

### Testing Framework Standardization (To Be Decided)

- Should we standardize on xUnit vs MSTest, or keep both?
- What's the target code coverage percentage for this project?
- How should we test OrderProcessor/PaymentProcessor without impacting development workflow?
- Should functional tests use shared or isolated databases?

---

## Related Documentation

- **AppHost Orchestration:** `src/eShop.AppHost/Program.cs`
- **Service Defaults:** `src/eShop.ServiceDefaults/Extensions.cs`
- **Event Bus:** `src/EventBusRabbitMQ/RabbitMQEventBus.cs`
- **Outbox Pattern:** `src/IntegrationEventLogEF/Services/IntegrationEventLogService.cs`
- **DDD Building Blocks:** `src/Ordering.Domain/SeedWork/`
- **MediatR Behaviors:** `src/Ordering.API/Application/Behaviors/`

---

**Document Status:** Complete — Initial exploration findings merged and deduplicated.  
**Last Updated:** 2026-04-10  
**Next Review:** After team discussion and prioritization of recommendations.
