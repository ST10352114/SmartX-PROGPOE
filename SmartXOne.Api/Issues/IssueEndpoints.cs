using SmartXOne.Shared.Contracts;

namespace SmartXOne.Api.Issues;

/// <summary>Endpoints backing the dashboard's user-reported issues list (Fix C).</summary>
public static class IssueEndpoints
{
    public static void MapIssueEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/issues");

        group.MapPost("/", (CreateIssueRequest request, IssueRepository repo) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return Results.BadRequest("Description is required.");
            }

            return Results.Ok(repo.Create(request));
        });

        // status/severity are bound from the query string (?status=Open&severity=High);
        // either, both, or neither may be supplied.
        group.MapGet("/", (IssueStatus? status, IssueSeverity? severity, IssueRepository repo)
            => Results.Ok(repo.GetAll(status, severity)));

        group.MapPost("/{id}/acknowledge", (string id, IssueRepository repo)
            => repo.SetStatus(id, IssueStatus.Acknowledged) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound($"No issue with id '{id}'."));

        group.MapPost("/{id}/resolve", (string id, IssueRepository repo)
            => repo.SetStatus(id, IssueStatus.Resolved) is { } dto
                ? Results.Ok(dto)
                : Results.NotFound($"No issue with id '{id}'."));
    }
}
