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

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
