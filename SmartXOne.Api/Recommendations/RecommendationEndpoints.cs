using SmartXOne.Api.Infrastructure;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Recommendations;

/// <summary>Endpoints for the Predictive Action &amp; Recommendation Engine.</summary>
public static class RecommendationEndpoints
{
    public static void MapRecommendationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/recommendations");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RecommendationEndpoints));

        // The client calls this (debounced) whenever the Command Stream page's
        // search box settles on a new value - this is the "log user searches"
        // half of the event history the engine learns from.
        group.MapPost("/events/search", (LogSearchRequest request, RecommendationEngine recommendations) =>
        {
            try
            {
                recommendations.LogSearch(request.Term);
                return Results.Accepted();
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Log search", request.Term);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Log search", request.Term);
            }
        });

        group.MapGet("/suggestions", (RecommendationEngine recommendations) =>
        {
            try
            {
                return Results.Ok(recommendations.GetSuggestions());
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Build suggestions", subject: null);
            }
        });
    }
}
