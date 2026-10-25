using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SivaComm.Data;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

namespace SivaComm.Api.Auth;

public static class AppClaims
{
    public const string Scheme = "AppSignature";
    public const string Policy = "Application";
    public const string ApplicationId = "sivacomm:app_id";
    public const string ApplicationCode = "sivacomm:app_code";

    public static CallerContext Caller(this HttpContext http) => new(
        Guid.Parse(http.User.FindFirstValue(ApplicationId)!),
        http.User.FindFirstValue(ApplicationCode)!,
        http.CorrelationId());
}

/// <summary>
/// Authenticates server-to-server calls by their HMAC signature. The body is
/// part of what is signed, so it is buffered and read here, then rewound for
/// model binding.
/// </summary>
public sealed class AppSignatureHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    RequestAuthenticator authenticator,
    Microsoft.EntityFrameworkCore.IDbContextFactory<CommDbContext> dbFactory,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const int MaxBody = 16 * 1024;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;
        if (!headers.ContainsKey(RequestSigning.SignatureHeader)) return AuthenticateResult.NoResult();

        if (Request.ContentLength > MaxBody) return AuthenticateResult.Fail("Body too large.");

        Request.EnableBuffering(MaxBody);
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, Context.RequestAborted);
        Request.Body.Position = 0;

        var signed = new SignedRequest(
            headers[RequestSigning.ClientHeader],
            headers[RequestSigning.KeyIdHeader],
            headers[RequestSigning.TimestampHeader],
            headers[RequestSigning.NonceHeader],
            headers[RequestSigning.SignatureHeader],
            Request.Method,
            Request.Path.Value + Request.QueryString.Value,
            buffer.ToArray());

        var (failure, appId, appCode) = await authenticator.AuthenticateAsync(signed, Context.RequestAborted);

        if (failure != AuthFailure.None)
        {
            // Recorded for the dashboard's error list. The client id is what
            // the caller claimed — useful to an operator, trusted by nothing.
            await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted);
            var claimed = signed.ClientId is { Length: <= 40 } c ? c : "?";
            db.AuditEvents.Add(new AuditEvent
            {
                EventType = AuditTypes.AuthFailed,
                CorrelationId = Context.CorrelationId(),
                Detail = $"{failure} (client '{claimed}')",
                CreatedAt = time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync(Context.RequestAborted);

            Logger.LogWarning("Signed request rejected: {Failure} for claimed client {Client}.", failure, claimed);
            return AuthenticateResult.Fail(failure.ToString());
        }

        var identity = new ClaimsIdentity(
            [new Claim(AppClaims.ApplicationId, appId.ToString()), new Claim(AppClaims.ApplicationCode, appCode)],
            AppClaims.Scheme);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AppClaims.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new { success = false, status = "UNAUTHORIZED", correlationId = Context.CorrelationId() });
    }
}
