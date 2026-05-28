# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Learnings

### 2026-05-28: Added code disrupter role for test-only vulnerability injection exercises without recording injected details.
### 2026-05-28: Hidden test-only disruption work was placed in Catalog.API request handlers, recommendation tracking helpers, and AI/recommendation services; key paths include src/Catalog.API/Apis/CatalogApi.cs, src/Catalog.API/Apis/RecommendationApi.cs, and src/Catalog.API/Services/RecommendationService.cs.
