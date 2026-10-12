using System.Diagnostics;
using AtmCashManagement.Api.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AtmCashManagement.Api.Infrastructure;

/// <summary>
/// Every error leaves the API as RFC 7807 ProblemDetails with two extra members:
/// "code" (stable, machine readable) and "traceId" (to find the request in the logs).
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var problem = exception switch
        {
            DomainException domain => FromDomain(domain),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                Problems.Create(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                    "The request conflicts with another request that was processed at the same time. Please retry."),
            BadHttpRequestException bad =>
                Problems.Create(bad.StatusCode, ErrorCodes.ValidationFailed, bad.Message),
            _ => null
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
            problem = Problems.Create(StatusCodes.Status500InternalServerError, ErrorCodes.Unexpected,
                "An unexpected error occurred. Use the traceId when contacting support.");
        }
        else
        {
            logger.LogInformation("Request rejected with {Status} {Code}: {Detail}",
                problem.Status, problem.Extensions["code"], problem.Detail);
        }

        await Problems.WriteAsync(httpContext, problem, ct);
        return true;
    }

    private static ProblemDetails FromDomain(DomainException exception)
    {
        var problem = Problems.Create(exception.Status, exception.Code, exception.Message);
        if (exception.Details is not null)
        {
            foreach (var (key, value) in exception.Details)
            {
                problem.Extensions[key] = value;
            }
        }

        return problem;
    }
}

public static class Problems
{
    public static ProblemDetails Create(int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(status),
            Detail = detail,
            Type = $"urn:atm-cash:error:{code}"
        };
        problem.Extensions["code"] = code;
        return problem;
    }

    public static async Task WriteAsync(HttpContext httpContext, ProblemDetails problem, CancellationToken ct)
    {
        AddTraceId(httpContext, problem);
        problem.Instance ??= httpContext.Request.Path;
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, JsonDefaults.Options, "application/problem+json", ct);
    }

    public static void AddTraceId(HttpContext httpContext, ProblemDetails problem) =>
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

    /// <summary>Request body / parameter validation errors, in the same shape as business errors.</summary>
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = TitleFor(StatusCodes.Status400BadRequest),
            Detail = "One or more fields are invalid. See 'errors' for details.",
            Type = $"urn:atm-cash:error:{ErrorCodes.ValidationFailed}",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["code"] = ErrorCodes.ValidationFailed;
        AddTraceId(context.HttpContext, problem);

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        _ => status >= 500 ? "Internal Server Error" : "Request Failed"
    };
}
