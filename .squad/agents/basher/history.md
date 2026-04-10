# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-04-10: Initial Test Infrastructure Analysis

**Test Frameworks:**
- MSTest SDK used for unit tests (Basket, Ordering, ClientApp) with 66 test methods
- xUnit v3 used for functional tests (Catalog, Ordering) with 11 Facts and 15 Theories (expandable)
- Playwright used for E2E tests with 3 test scenarios
- NSubstitute as primary mocking framework across all test suites

**Unit Tests Coverage:**
- **Basket.API**: 3 test methods in BasketServiceTests - tests gRPC service with user auth scenarios
- **Ordering.API/Domain**: 8 test classes covering Application layer (commands, handlers) and Domain layer (aggregates, value objects)
  - Domain tests use Builder pattern (OrderBuilder, AddressBuilder) for test data
  - Application tests mock IMediator, IOrderRepository with NSubstitute
  - Tests validate domain events, business rules, command idempotency
- **ClientApp**: 8 test classes (55 methods) covering ViewModels and Services
  - Uses mock services pattern (BasketMockService, CatalogMockService, etc.)
  - Tests view model initialization, commands, property change notifications

**Functional Tests Infrastructure:**
- Both Catalog and Ordering functional tests use Aspire Hosting with WebApplicationFactory
- Tests spin up real PostgreSQL containers via Aspire (requires Docker)
- CatalogApiFixture: Uses ankane/pgvector image for Postgres with vector search
- OrderingApiFixture: Spins up Postgres + Identity.API dependency, uses AutoAuthorizeMiddleware for auth bypass
- Tests cover API versioning (v1.0 and v2.0) with Theory-based parameterized tests
- Tests validate CRUD operations, pagination, filtering, semantic search

**E2E Tests (Playwright):**
- Configured to run against localhost:5045 (Aspire AppHost)
- 3 tests: AddItemTest (auth), RemoveItemTest (auth), BrowseItemTest (no auth)
- Uses login.setup.ts for authentication state management
- CI configuration: serial execution (workers: 1), 2 retries on failures

**Build Configuration:**
- tests/Directory.Build.props enables MSTest and xUnit runners via Microsoft Testing Platform
- CI pipeline (ci.yml) only runs `dotnet build` - NO test execution in CI currently
- Tests configured for parallel execution (Parallelize attribute in Basket tests)

**Coverage Gaps Identified:**
Critical services with NO tests:
- Identity.API (authentication/authorization)
- Webhooks.API (webhook delivery)
- OrderProcessor (background service)
- PaymentProcessor (background service)
- WebApp/WebAppComponents (Blazor server components)
- HybridApp (MAUI hybrid)

Infrastructure with NO tests:
- EventBus/EventBusRabbitMQ (messaging)
- IntegrationEventLogEF (event sourcing)
- eShop.AppHost (Aspire configuration)
- eShop.ServiceDefaults (service extensions)

**Test Patterns:**
- Arrange-Act-Assert pattern consistently used
- Test class names follow *Tests suffix convention
- GlobalUsings.cs files reduce boilerplate
- Functional tests use IClassFixture<T> for shared test context
- Tests validate both success and error scenarios (HTTP status codes, exceptions)
