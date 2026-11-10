using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SmartXOne.Api.Infrastructure;

// Code Attribution
// Purpose: Registering an IExceptionHandler with AddExceptionHandler and
//          writing an RFC 7807 ProblemDetails body through IProblemDetailsService,
//          including preserving BadHttpRequestException.StatusCode.
// Author: Microsoft
// Title: Handle errors in ASP.NET Core; Handle errors in ASP.NET Core APIs
// Year: 2026
// URL/DOI: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling,
//          https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api
// Accessed: 10 November 2026

/// <summary>
/// The last line of defence behind every endpoint's own try/catch: anything
/// that still escapes (a model-binding failure, a bug in a handler, a fault in
/// middleware) is logged once here and turned into a ProblemDetails response
/// instead of a bare 500 with no body. Specific exception types map to the
/// status code a client can act on; everything else is a generic 500 whose
/// detail never leaks internal messages or stack traces.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Bad request", badRequest.Message),
            JsonException => (StatusCodes.Status400BadRequest, "Malformed JSON", "The request body is not valid JSON for this endpoint."),
            ArgumentException argument => (StatusCodes.Status400BadRequest, "Invalid argument", argument.Message),
            KeyNotFoundException notFound => (StatusCodes.Status404NotFound, "Not found", notFound.Message),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled", "The request was cancelled before it completed."),
            InvalidOperationException invalid => (StatusCodes.Status409Conflict, "Invalid operation", invalid.Message),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred. The gateway has logged the details."),
        };

        var path = httpContext.Request.Path;
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, path);
        }
        else
        {
            logger.LogWarning(exception, "Request {Method} {Path} failed with {Status}: {Message}",
                httpContext.Request.Method, path, status, exception.Message);
        }

        if (httpContext.Response.HasStarted)
        {
            // Headers are already on the wire, so the status can't change any more;
            // the log entry above is all that can usefully be done.
            return true;
        }

        httpContext.Response.StatusCode = status;

        try
        {
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = status,
                    Title = title,
                    Detail = detail,
                    Instance = path,
                },
            });
        }
        catch (Exception writeException)
        {
            // Writing the error body itself failed (e.g. the client disconnected).
            // Returning false lets the built-in middleware fall back to its default.
            logger.LogError(writeException, "Failed to write the ProblemDetails response for {Path}.", path);
            return false;
        }
    }
}
