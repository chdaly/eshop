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
### 2026-05-28: Post-disruption analysis revealed team missed 4 critical patterns: (1) Path traversal via unrestricted file path composition, (2) Mass assignment vulnerabilities through EF SetValues reflection, (3) Unbounded array processing enabling DoS, (4) Fire-and-forget task execution without back-pressure. Detection rate approximately 25% indicates significant training gap.
### 2026-05-28: Key blind spots identified: (1) Naming convention trust ("Unsafe" methods not properly scrutinized), (2) Framework magic over explicit control (EF Core property reflection misunderstood), (3) Happy path bias (adversarial scenarios not considered), (4) Async/await false security (unlimited task spawning), (5) Separation of concerns obscuring attack surface (helper method isolation reduced end-to-end visibility).
### 2026-05-28: Recommended training themes for team-wide security uplift: (1) STRIDE threat modeling with data flow analysis, (2) OWASP Top 10 practical exploitation labs, (3) Secure code review checklist development, (4) Static analysis tool integration and custom rule authoring, (5) Red team vs blue team exercises, (6) Security champion rotation model. Target 80% reduction in externally-detected vulnerabilities within 6 weeks.
