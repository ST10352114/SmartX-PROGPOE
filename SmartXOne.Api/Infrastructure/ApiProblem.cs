namespace SmartXOne.Api.Infrastructure;

// Code Attribution
// Purpose: Returning RFC 7807 ProblemDetails from Minimal API endpoints with
//          Results.Problem instead of bare status codes or plain strings.
// Author: Microsoft
// Title: Handle errors in ASP.NET Core APIs
// Year: 2026c
// URL/DOI: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api
// Accessed: 10 November 2026

/// <summary>
/// Shared error-result helpers for every endpoint's try/catch, so all of them
/// answer failures with the same ProblemDetails shape (<c>title</c>,
/// <c>status</c>, <c>detail</c>) that the client knows how to read, and log
/// with the same context (which operation, on which device or sensor).
/// </summary>
internal static class ApiProblem
{
    /// <summary>400 with a client-facing explanation.</summary>
    public static IResult BadRequest(string detail)
        => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Bad request");

    /// <summary>404 with a client-facing explanation.</summary>
    public static IResult NotFound(string detail)
        => Results.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    /// <summary>409 with a client-facing explanation.</summary>
    public static IResult Conflict(string detail)
        => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    /// <summary>
    /// An expected failure (bad input, unknown id, invalid state): logged as a
    /// warning and returned with the exception's own message, which for these
    /// types is written to be safe to show to the caller.
    /// </summary>
    public static IResult Handled(ILogger logger, Exception exception, int statusCode, string operation, string? subject)
    {
        logger.LogWarning(exception, "{Operation} failed for {Subject}: {Message}", operation, subject ?? "(none)", exception.Message);
        return Results.Problem(exception.Message, statusCode: statusCode, title: operation + " failed");
    }

    /// <summary>
    /// The caller went away (or the host is stopping) mid-request. Logged at
    /// information level - nothing is broken - and answered with 499.
    /// </summary>
    public static IResult Cancelled(ILogger logger, string operation, string? subject)
    {
        logger.LogInformation("{Operation} for {Subject} was cancelled by the caller.", operation, subject ?? "(none)");
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }

    /// <summary>
    /// Anything not anticipated: logged as an error with the full exception, but
    /// the response only carries a generic message so internals never leak.
    /// </summary>
    public static IResult Unexpected(ILogger logger, Exception exception, string operation, string? subject)
    {
        logger.LogError(exception, "Unexpected error during {Operation} for {Subject}.", operation, subject ?? "(none)");
        return Results.Problem(
            $"An unexpected error occurred during '{operation}'. The gateway has logged the details.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Server error");
    }
}
