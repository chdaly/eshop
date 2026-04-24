# Squad Decisions

## Active Decisions

### 2026-04-10: Product Recommendations v1 Architecture

**Decision Context:**
Implement product recommendations feature to improve user engagement and cross-selling in eShop.

**Architecture Decisions:**

1. **Service Placement: Catalog.API Integration (Not New Microservice)**
   - Rationale: Recommendations tightly coupled to catalog data (embeddings, item properties, inventory)
   - Simpler deployment and dependency management
   - Leverages existing pgvector infrastructure

2. **Storage: Redis for Browsing History**
   - Structure: Redis LIST per user (key: `browsing_history:{userId}`)
   - Constraints: 50-item cap (LPUSH + LTRIM), 30-day TTL (EXPIRE)
   - Rationale: Fast access, natural eviction, shared infrastructure with Basket.API

3. **Recommendation Algorithm: Centroid-Based Similarity**
   - Process: Average embeddings of last N viewed items (centroid calculation)
   - Query: pgvector CosineDistance ordering against product embeddings
   - Exclusions: Viewed items and out-of-stock (AvailableStock <= 0)
   - Rationale: Computational efficiency, leverages existing embeddings, no ML model training

4. **Scope: Authenticated Users Only (v1)**
   - Authentication required to access view history
   - Deferred to v2: Anonymous user tracking via session/cookies
   - Rationale: Simpler authentication model, avoids session complexity

5. **Fallback Strategy (Graceful Degradation)**
   - Primary: AI-powered centroid similarity
   - Secondary: Products with same CatalogType as recent view
   - Tertiary: Newest items by creation date
   - Final: Empty list with no results
   - Rationale: Ensures recommendations available even with AI disabled or no history

6. **Frontend: ProductRecommendations Carousel Component**
   - Placement: Below product details on ItemPage
   - Reusability: Shared WebAppComponents library (works in WebApp and HybridApp)
   - View Recording: Fire-and-forget in OnAfterRenderAsync (no blocking)
   - Rationale: Non-blocking UX, component reuse, gradual enhancement

**API Contracts:**
- POST `/api/v1.0/recommendations/view` — Record product view
- GET `/api/v1.0/recommendations?pageIndex=0&pageSize=10` — Fetch recommendations

**Configuration:**
- `RecommendationOptions`: MaxHistoryLength, HistoryTtlDays, CentroidSampleSize
- Bound from appsettings.json, injectable via dependency injection

**Test Coverage:**
- Functional: API auth, fallback chains, exclusion rules, AI disabled mode
- Unit: Centroid calculation, exclusion filtering, pagination, Redis constraints

## 2026-04-24: Recommendations v1 Implementation Complete

**Outcome:** Full-stack product recommendations feature delivered by Linus (backend), Livingston (frontend), and Basher (testing).

**Decision Carried Forward:**
- RecommendationApi endpoints require explicit parameter binding (`[FromServices]`, `[FromBody]`) for OpenAPI document generation at build time — matches patterns in existing Catalog.API endpoints

**Implementation Validation:**
- All architectural decisions from 2026-04-10 successfully implemented and tested
- Redis browsing history pattern validated with 50-item cap and 30-day TTL
- Centroid-based similarity algorithm verified with 31 comprehensive tests
- Graceful degradation fallback chain tested end-to-end
- 18 functional tests + 13 unit tests all passing

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
