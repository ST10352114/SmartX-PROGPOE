using SmartXOne.Api.Infrastructure;
using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Issues;

/// <summary>Endpoints backing the dashboard's user-reported issues list (Fix C).</summary>
public static class IssueEndpoints
{
    public static void MapIssueEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/issues");
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IssueEndpoints));

        group.MapPost("/", (CreateIssueRequest request, IssueRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return ApiProblem.BadRequest("Description is required.");
            }

            try
            {
                return Results.Ok(repo.Create(request));
            }
            catch (ArgumentException ex)
            {
                return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, "Create issue", request.SensorId);
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "Create issue", request.SensorId);
            }
        });

        // status/severity are bound from the query string (?status=Open&severity=High);
        // either, both, or neither may be supplied.
        group.MapGet("/", (IssueStatus? status, IssueSeverity? severity, IssueRepository repo) =>
        {
            try
            {
                return Results.Ok(repo.GetAll(status, severity));
            }
            catch (Exception ex)
            {
                return ApiProblem.Unexpected(logger, ex, "List issues", $"status={status}, severity={severity}");
            }
        });

        group.MapPost("/{id}/acknowledge", (string id, IssueRepository repo)
            => SetStatus(id, IssueStatus.Acknowledged, repo, logger));

        group.MapPost("/{id}/resolve", (string id, IssueRepository repo)
            => SetStatus(id, IssueStatus.Resolved, repo, logger));
    }

    private static IResult SetStatus(string id, IssueStatus status, IssueRepository repo, ILogger logger)
    {
        try
        {
            return repo.SetStatus(id, status) is { } dto
                ? Results.Ok(dto)
                : ApiProblem.NotFound($"No issue with id '{id}'.");
        }
        catch (ArgumentException ex)
        {
            return ApiProblem.Handled(logger, ex, StatusCodes.Status400BadRequest, $"Set issue status to {status}", id);
        }
        catch (Exception ex)
        {
            return ApiProblem.Unexpected(logger, ex, $"Set issue status to {status}", id);
        }
    }
}
