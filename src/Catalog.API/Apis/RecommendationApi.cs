using System.ComponentModel.DataAnnotations;
using eShop.Catalog.API.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace eShop.Catalog.API;

public static class RecommendationApi
{
    public static IEndpointRouteBuilder MapRecommendationApi(this IEndpointRouteBuilder app)
    {
        var api = app.NewVersionedApi("Recommendations")
            .MapGroup("api/catalog/recommendations")
            .HasApiVersion(1, 0);

        api.MapPost("/view", RecordView)
            .WithName("RecordProductView")
            .WithSummary("Record a product view")
            .WithDescription("Records that the authenticated user viewed a product, for recommendation tracking.")
            .WithTags("Recommendations")
            .RequireAuthorization();

        api.MapGet("/", GetRecommendations)
            .WithName("GetRecommendations")
            .WithSummary("Get product recommendations")
            .WithDescription("Gets personalized product recommendations based on the user's browsing history.")
            .WithTags("Recommendations")
            .RequireAuthorization();

        return app;
    }

    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public static async Task<Results<NoContent, NotFound, BadRequest<ProblemDetails>, UnauthorizedHttpResult>> RecordView(
        HttpContext httpContext,
        CatalogContext context,
        IRecommendationService recommendationService,
        ILogger<RecommendationService> logger,
        RecordProductViewRequest request)
    {
        if (request.ItemId <= 0)
        {
            return TypedResults.BadRequest<ProblemDetails>(new()
            {
                Detail = "Item id is not valid."
            });
        }

        var userId = httpContext.User.FindFirst("sub")?.Value;
        if (userId is null)
        {
            return TypedResults.Unauthorized();
        }

        var item = await context.CatalogItems.FindAsync(request.ItemId);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        // Fire-and-forget: record view without blocking the response
        _ = Task.Run(async () =>
        {
            try
            {
                await recommendationService.RecordViewAsync(userId, request.ItemId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record product view for user {UserId}, item {ItemId}", userId, request.ItemId);
            }
        });

        return TypedResults.NoContent();
    }

    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public static async Task<Results<Ok<PaginatedItems<CatalogItem>>, UnauthorizedHttpResult>> GetRecommendations(
        HttpContext httpContext,
        IRecommendationService recommendationService,
        [AsParameters] PaginationRequest paginationRequest)
    {
        var userId = httpContext.User.FindFirst("sub")?.Value;
        if (userId is null)
        {
            return TypedResults.Unauthorized();
        }

        var recommendations = await recommendationService.GetRecommendationsAsync(
            userId,
            paginationRequest.PageIndex,
            paginationRequest.PageSize);

        return TypedResults.Ok(recommendations);
    }
}

public record RecordProductViewRequest([property: Required] int ItemId);
