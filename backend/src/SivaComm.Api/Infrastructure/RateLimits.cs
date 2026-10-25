using System.Security.Claims;
using System.Threading.RateLimiting;
using SivaComm.Api.Auth;

namespace SivaComm.Api;

/// <summary>
/// Coarse per-application limits, so one misbehaving or compromised
/// application cannot flood the service. The fine-grained, per-user limits
/// (sends per window, attempts per challenge) are enforced in OtpService,
/// where they are atomic with the data they protect.
/// </summary>
public static class RateLimits
{
    public const string Create = "app-create";
    public const string Verify = "app-verify";
    public const string AdminLogin = "admin-login";

    public static IServiceCollection AddSivaCommRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var createPerMinute = config.GetValue("RateLimits:CreatePerMinute", 60);
        var verifyPerMinute = config.GetValue("RateLimits:VerifyPerMinute", 300);

        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                await ctx.HttpContext.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    status = "RATE_LIMITED",
                    message = "Too many requests. Try again later.",
                    retryAfterSeconds = 60,
                    correlationId = ctx.HttpContext.CorrelationId(),
                }, ct);
            };

            o.AddPolicy(Create, http => PerApplication(http, createPerMinute));
            o.AddPolicy(Verify, http => PerApplication(http, verifyPerMinute));
            o.AddPolicy(AdminLogin, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    private static RateLimitPartition<string> PerApplication(HttpContext http, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirstValue(AppClaims.ApplicationId) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
}
