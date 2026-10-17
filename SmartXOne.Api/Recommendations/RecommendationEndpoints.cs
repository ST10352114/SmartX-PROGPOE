using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Recommendations;

/// <summary>Endpoints for the Predictive Action &amp; Recommendation Engine.</summary>
public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/recommendations");

        // The client calls this (debounced) whenever the Command Stream page's
        // search box settles on a new value - this is the "log user searches"
        // half of the event history the engine learns from.
        group.MapPost("/events/search", (LogSearchRequest request, RecommendationEngine recommendations) =>
        {
            recommendations.LogSearch(request.Term);
            return Results.Accepted();
        });

        group.MapGet("/suggestions", (RecommendationEngine recommendations) => Results.Ok(recommendations.GetSuggestions()));
    }
}
