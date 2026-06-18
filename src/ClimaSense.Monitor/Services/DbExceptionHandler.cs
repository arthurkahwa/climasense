using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ClimaSense.Monitor.Services;

public sealed class DbExceptionHandler(ILogger<DbExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (SqlException or DbException or TimeoutException))
            return false; // not a DB fault — let the default handler produce a 500

        logger.LogError(exception, "Database access failed for {Path}", httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Data temporarily unavailable",
            Detail = "The sensor database could not be reached. Please retry shortly."
        }, cancellationToken);
        return true;
    }
}
