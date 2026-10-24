using Microsoft.AspNetCore.Diagnostics;
using SyWater.Valve.Application.Ports.Out;
using SyWater.Valve.Domain.Common;
using SyWater.Valve.Domain.Valves;

namespace SyWater.Valve.Api.Errors;

/// <summary>Domain exceptions → RFC 9457 Problem Details with the stable code in "title" (same as the other services).</summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails, ILogger<DomainExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code) = exception switch
        {
            PlaceNotFoundException e => (StatusCodes.Status404NotFound, e.Code),
            PermissionDeniedException e => (StatusCodes.Status403Forbidden, e.Code),
            ValveNotFoundException e => (StatusCodes.Status404NotFound, e.Code),
            CommandNotFoundException e => (StatusCodes.Status404NotFound, e.Code),
            CommandInProgressException e => (StatusCodes.Status409Conflict, e.Code),
            ValveAlreadyInStateException e => (StatusCodes.Status409Conflict, e.Code),
            ValveDeviceOfflineException e => (StatusCodes.Status409Conflict, e.Code),
            ConfirmationCodeRequiredException e => (StatusCodes.Status400BadRequest, e.Code),
            ConfirmationCodeRejectedException e => (e.Code == "auth.code_recently_sent"
                ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest, e.Code),
            ExternalServiceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "service.unavailable"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "auth.invalid_token"),
            _ => (0, ""),
        };

        if (status == 0) return false; // unknown error: ASP.NET Core answers a generic 500
        if (exception is ExternalServiceUnavailableException ex)
            logger.LogWarning(ex.InnerException, "{Service} is not available", ex.Service);

        context.Response.StatusCode = status;
        var details = new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = code,
                Detail = exception.Message,
                Type = exception is DomainException ? $"https://sywater.dev/errors/{code}" : null,
            },
        };
        if (exception is ConfirmationCodeRejectedException { RemainingAttempts: { } remaining })
            details.ProblemDetails.Extensions["remainingAttempts"] = remaining;
        if (exception is ExternalServiceUnavailableException unavailable)
            details.ProblemDetails.Extensions["service"] = unavailable.Service;

        return await problemDetails.TryWriteAsync(details);
    }
}
