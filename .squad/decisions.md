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

## 2026-05-28: Security Vulnerability Skills Improvement Council Decision

**Decision Context:**
Mullins (Code Disrupter) reported that team security review showed promise but missed injected vulnerabilities, indicating incomplete vulnerability detection methodology and uneven skill distribution. Council convened to design systematic team security capability improvement.

**Council Participants:**
Rusty (Architect), Linus (Backend), Livingston (Frontend), Basher (QA), Tess (Security Specialist), Mullins (Code Disrupter), Ralph (Monitoring), Scribe (Facilitation)

**Consensus Decision: Four-Pillar Security Improvement Program**

**Pillar 1: Vulnerability Detection Methodology (Lead: Tess + Rusty)**
- Implement STRIDE threat modeling workshops (bi-weekly)
- Monthly OWASP Top 10 deep-dive rotation
- Linus leads backend canonicalization audit (path traversal, injection patterns)
- Rusty maps all microservices endpoints for authorization requirements
- Rationale: Systematic vulnerability classification prevents blind spots; STRIDE and OWASP provide shared vocabulary

**Pillar 2: Security Test Infrastructure (Lead: Basher)**
- Develop CatalogApiFixture.AutoAuthorizeMiddleware for authorization testing
- Build 15+ security-focused test cases per API (authorization, injection, data validation)
- Implement mutation testing for invalid token/scope rejection
- Integrate security tests into CI/CD pipeline with coverage dashboard
- Rationale: Automated tests prevent regression; test-first prevents knowledge loss

**Pillar 3: Cross-Tier Pairing (Lead: Scribe Coordination)**
- Tess + Linus (4 weeks): API Authentication & Authorization deep-dive
- Tess + Livingston (3 weeks): Frontend secrets, XSS, CSRF prevention
- Livingston + Linus (2 weeks): Data flow security (JWT usage, sensitive field handling)
- Basher + All (weekly): "Vulnerability hunt" CTF-style exercises
- Rationale: Direct knowledge transfer builds consistent security practices; pairing creates team ownership

**Pillar 4: Continuous Learning & Accountability (Lead: Mullins + Ralph)**
- Mullins injects 3–5 vulnerabilities bi-weekly; team discovers within 48-hour window
- Monthly post-mortems on missed vulnerabilities (skill gap, detection method analysis)
- Quarterly difficulty escalation: obvious bugs → subtle logic flaws → supply chain issues
- Ralph maintains monthly security metrics dashboard (detection rate, time-to-discover, severity)
- Rationale: Sustained practice prevents skill decay; difficulty escalation matches team growth

**Timeline:**
- Week 1 (May 28–Jun 3): Threat modeling kickoff, CatalogApiFixture automation
- Week 2–4 (Jun 4–24): Auth pairing sessions, OWASP workshops, security test baseline
- Week 5–6 (Jun 25–Jul 8): Frontend pairing, test completion
- Week 7+ (Jul 9+): Mullins bi-weekly injections, monthly drills, metrics review

**Success Criteria:**
1. Skill parity: All team members independently identify ≥80% OWASP Top 10 issues
2. Detection rate: ≥90% of Mullins-injected vulnerabilities discovered within 48 hours (Jun 15 checkpoint)
3. Test coverage: ≥50 security-focused unit/integration tests; 0 regression on previously-fixed vulnerabilities
4. Metrics: Monthly 1-page dashboard tracking vulnerabilities found, detection speed, severity distribution

**Team Commitments:**
- **Tess:** Curriculum design, cross-tier pairing, threat modeling facilitation
- **Rusty:** Architecture mapping, STRIDE workshops, endpoint authorization
- **Linus:** Canonicalization audit, API test infrastructure, auth testing
- **Livingston:** Frontend secret handling review, XSS/CSRF test cases
- **Basher:** CatalogApiFixture automation, CTF exercise design
- **Mullins:** Bi-weekly vulnerability injection, difficulty escalation oversight
- **Ralph:** Metrics dashboard, progress tracking, accountability reporting
- **Scribe:** Session logging, decision capture, cross-team coordination

**Status:** Proposal submitted to Chris Daly (Project Owner) for formal approval before team commitment activation.

**Consensus:** Unanimous (Rusty, Linus, Livingston, Basher, Tess)

**Documentation:**
- Orchestration log: `.squad/orchestration-log/2026-05-28-143000-security-council.md`
- Session log: `.squad/log/2026-05-28-security-improvement-council.md`

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
