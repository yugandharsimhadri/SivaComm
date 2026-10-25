using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace SivaComm.Api;

public static class CorrelationExtensions
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "sivacomm.correlation";

    public static string CorrelationId(this HttpContext http) =>
        http.Items[ItemKey] as string ?? http.TraceIdentifier;

    /// <summary>
    /// Takes the caller's correlation id when it is a sane token, otherwise
    /// makes one; echoes it on the response and puts it in every log line of
    /// the request, so one id follows a reset from HMS_WEB's log into ours.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var incoming = http.Request.Headers[Header].ToString();
        var id = incoming is { Length: >= 8 and <= 64 } && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.NewGuid().ToString("N");

        http.Items[ItemKey] = id;
        http.Response.Headers[Header] = id;

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SivaComm.Request");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next();
        }
    });

    /// <summary>
    /// No exception detail ever reaches a caller. A database or network fault
    /// is TEMPORARY_ERROR (503), which every source application must treat as
    /// failure-and-retry — never as success.
    /// </summary>
    public static IApplicationBuilder UseSafeErrors(this IApplicationBuilder app) => app.UseExceptionHandler(builder => builder.Run(async http =>
    {
        var error = http.Features.Get<IExceptionHandlerFeature>()?.Error;
        var transient = error is DbException or TimeoutException or Microsoft.EntityFrameworkCore.DbUpdateException
                        || error?.InnerException is DbException or TimeoutException;

        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SivaComm.Errors")
            .LogError(error, "Unhandled error for {Path}.", http.Request.Path);

        http.Response.StatusCode = transient ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        await http.Response.WriteAsJsonAsync(new
        {
            success = false,
            status = transient ? "TEMPORARY_ERROR" : "INTERNAL_ERROR",
            message = "The request could not be completed. Try again.",
            correlationId = http.CorrelationId(),
        });
    }));
}
