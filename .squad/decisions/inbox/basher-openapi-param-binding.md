# Decision: RecommendationApi needs explicit parameter binding for OpenAPI

**Author:** Basher (Tester)
**Date:** 2026-04-10

## Context

The `RecommendationApi.RecordView` endpoint failed OpenAPI document generation at build time because:
1. `IRecommendationService` is not registered in the DI container during `IsBuild()` mode (the `Extensions.cs` early-returns before recommendation service registration)
2. Without explicit `[FromServices]` and `[FromBody]` annotations, the OpenAPI generator could not infer parameter sources

## Decision

Added `[FromServices]` to `IRecommendationService` parameters and `[FromBody]` to `RecordProductViewRequest` in `RecommendationApi.cs`. This matches the pattern used in `CatalogServices` where `[FromServices]` is applied to `ICatalogAI`.

## Impact

- Fixes a build-blocking error (55 MSBuild errors from failed OpenAPI generation)
- All team members working on Catalog.API were affected by this
- The `Extensions.cs` `IsBuild()` early-return pattern means any new DI-registered service used in endpoint handlers needs explicit `[FromServices]` annotation
