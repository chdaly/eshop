# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Backend Architecture Deep Dive (2026-04-10)

**API Services Architecture:**
- **Basket.API**: gRPC service using `BasketService` with three operations (GetBasket, UpdateBasket, DeleteBasket). Stores data in Redis via `RedisBasketRepository`. Uses auth via `GetUserIdentity()` from ServerCallContext. Subscribes to `OrderStartedIntegrationEvent` to clear baskets after order placement.
- **Catalog.API**: REST API with minimal API endpoints in `CatalogApi.cs`. Uses PostgreSQL via `CatalogContext` with pgvector extension for AI embeddings. Exposes v1 and v2 versioned endpoints. Supports semantic search with embeddings via `CatalogAI`. Publishes `ProductPriceChangedIntegrationEvent` when prices change. Subscribes to order validation and paid events.
- **Identity.API**: Uses IdentityServer4 with ASP.NET Core Identity. PostgreSQL backend via `ApplicationDbContext`. MVC-based UI with controllers and views. Manages users, clients, scopes, and API resources. Issues JWT tokens for service authentication.
- **Ordering.API**: REST API with MediatR CQRS pattern. PostgreSQL via `OrderingContext`. Exposes order management endpoints (create, cancel, ship, query). Uses domain events and integration events. Requires authorization on all endpoints.
- **Webhooks.API**: REST API for webhook subscriptions. PostgreSQL via `WebhooksContext`. Subscribes to product price changes and order status changes. Validates webhook URLs with grant URL pattern.

**Domain-Driven Design (Ordering.Domain):**
- Two aggregates: `Order` (AggregateRoot) and `Buyer` (AggregateRoot)
- **Order aggregate**: Contains OrderItems collection, Address value object, OrderStatus enumeration. Enforces invariants through methods like `AddOrderItem()`, `SetPaidStatus()`, `SetShippedStatus()`. Domain events: OrderStarted, OrderStatusChangedToAwaitingValidation, OrderStatusChangedToPaid, OrderShipped, OrderCancelled.
- **Buyer aggregate**: Contains PaymentMethods collection. Method `VerifyOrAddPaymentMethod()` ensures payment method uniqueness and raises `BuyerAndPaymentMethodVerifiedDomainEvent`.
- **Base classes**: `Entity` with domain events collection, `ValueObject` with structural equality, `IAggregateRoot` marker interface
- Rich domain model with encapsulation - private setters, readonly collections, behavior methods

**CQRS Pattern (Ordering.API):**
- Commands: `CreateOrderCommand`, `CancelOrderCommand`, `ShipOrderCommand`, etc. Handled by command handlers using MediatR.
- Queries: `OrderQueries` class with direct EF Core queries for read models. Separate from command side.
- Behaviors: `LoggingBehavior`, `ValidatorBehavior` (FluentValidation), `TransactionBehavior` (wraps commands in transactions)
- Idempotency: `IdentifiedCommand<T>` wrapper with `RequestManager` to prevent duplicate command execution using request IDs

**Event-Driven Architecture:**
- **Event Bus abstraction**: `IEventBus` with single `PublishAsync` method
- **RabbitMQ implementation**: `RabbitMQEventBus` with direct topic exchange, durable queues, manual ack, retry with exponential backoff (Polly), OpenTelemetry tracing
- **Integration events**: Named with past-tense (e.g., OrderStartedIntegrationEvent, OrderStatusChangedToPaidIntegrationEvent). Inherit from `IntegrationEvent` base class.
- **Event log pattern**: `IntegrationEventLogService` persists events to database in same transaction as domain changes. States: NotPublished, InProgress, Published, PublishedFailed. Ensures at-least-once delivery.
- **Event handlers**: Implement `IIntegrationEventHandler<T>`. Registered via `AddSubscription<TEvent, THandler>()` in service configuration.

**Background Processors:**
- **OrderProcessor**: `GracePeriodManagerService` polls database every N seconds. Queries orders in Submitted status past grace period. Publishes `GracePeriodConfirmedIntegrationEvent`. Uses raw ADO.NET with Npgsql for querying.
- **PaymentProcessor**: Subscribes to `OrderStatusChangedToStockConfirmedIntegrationEvent`. Simulates payment processing (configurable success/failure). Publishes `OrderPaymentSucceededIntegrationEvent` or `OrderPaymentFailedIntegrationEvent`.

**Data Access Patterns:**
- **Catalog**: Direct EF Core usage, no repository pattern. `CatalogContext` with DbSets. Uses pgvector for embeddings.
- **Basket**: Repository pattern with `IBasketRepository` and `RedisBasketRepository`. Direct Redis access via StackExchange.Redis.
- **Ordering**: Repository pattern with `IOrderRepository` and `IBuyerRepository`. Repositories expose `IUnitOfWork` property. `OrderingContext` implements `IUnitOfWork` with `SaveEntitiesAsync()` that dispatches domain events via MediatR before saving.
- **Identity**: Direct EF Core with `ApplicationDbContext`. Standard ASP.NET Core Identity repositories.
- **Webhooks**: Direct EF Core with `WebhooksContext`.

**Database Technologies:**
- **PostgreSQL**: Catalog, Ordering, Identity, Webhooks - all use Npgsql provider
- **Redis**: Basket storage - uses StackExchange.Redis
- All use schema-per-service (e.g., "ordering" schema in Ordering database)

**Transaction Management:**
- `TransactionBehavior` wraps MediatR commands in transactions
- Integration events saved in same transaction via `IntegrationEventLogService`
- Published after transaction commits to ensure consistency
- Execution strategy with retries for transient failures

**Service Configuration Pattern:**
- Each service has `Extensions.cs` with `AddApplicationServices()` method
- Registers repositories, event handlers, DbContexts, authentication, event bus subscriptions
- Uses `.AddServiceDefaults()` from eShop.ServiceDefaults for common config (telemetry, health checks, auth)
