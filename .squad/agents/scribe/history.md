# Project Context

- **Owner:** Chris Daly
- **Project:** eShop (AdventureWorks) — A reference .NET application implementing an e-commerce website using a services-based architecture with .NET Aspire.
- **Stack:** .NET 9, C#, .NET Aspire, Blazor, RabbitMQ, Docker, Entity Framework
- **Architecture:** Microservices — Basket.API, Catalog.API, Identity.API, Ordering.API, Webhooks.API, OrderProcessor, PaymentProcessor, EventBus
- **Frontend:** Blazor WebApp, WebAppComponents, ClientApp, HybridApp (MAUI)
- **Created:** 2026-04-10

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-04-10

## Learnings

**2026-05-28: Security Council Convened**
Mullins' disruptive testing revealed that team security review, while effective, missed injected vulnerabilities. Council consensus: team requires systematic skill development, not ad-hoc audits. Key insight—security capability is built through repeated practice, formalized threat modeling, paired mentoring, and continuous low-stakes testing, not reactive crisis response.

**Program Design Principle:** Shift from "finding bugs" to "teaching bug detection." Four pillars—methodology (STRIDE/OWASP), automation (test infrastructure), transfer (pairing), and accountability (Mullins injection + metrics)—address root causes: methodology gaps, skill distribution, test coverage, and knowledge loss.

**Implementation Insight:** June 15 checkpoint (first Mullins injection after pairing starts) will validate skill gain. Success = ≥90% detection within 48 hours. Failure triggers curriculum adjustment. Quarterly difficulty escalation keeps team calibrated against evolving threats.

**Team Observation:** Tess (security specialist) is force multiplier; program requires her direct involvement in curriculum and pairing. Rusty (architect) bridges threat modeling to implementation. Mullins (disruptor) validates learning in safe-failure mode. Scribe role = ensure consensus is documented before execution.

Initial setup complete.
