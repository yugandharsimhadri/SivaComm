using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SivaCom.Data;
using SivaCom.Data.Security;
using SivaCom.Data.Services;

namespace SivaCom.Api.Auth;

public static class AppClaims
{
    public const string Scheme = "AppSignature";
    public const string Policy = "Application";
    public const string ApplicationId = "sivacom:app_id";
    public const string ApplicationCode = "sivacom:app_code";

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
        if (!headers.ContainsKey(RequestSigning.SignatureHeader))
        {
            // Only application endpoints use this scheme, so an unsigned call
            // here is a misconfigured caller. The usual one: an application
            // built before the rename, still sending X-SivaComm-* headers.
            var old = headers.Keys.Any(k => k.StartsWith("X-SivaComm-", StringComparison.OrdinalIgnoreCase));
            Logger.LogWarning("Unsigned request to {Path:l} refused (401): {Reason:l}.", Request.Path.Value,
                old ? "it uses the old X-SivaComm-* header names; the calling application is a build from before the SivaCom rename and must be updated"
                    : $"no {RequestSigning.SignatureHeader} header");
            return AuthenticateResult.NoResult();
        }

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

            Logger.LogWarning("Signed request to {Path:l} rejected (401): {Failure} for client {Client:l}, key {KeyId:l} - {Hint:l}",
                Request.Path.Value, failure, claimed, signed.KeyId is { Length: <= 60 } k ? k : "?", failure switch
                {
                    AuthFailure.MissingHeaders => "a signing header is missing or the nonce is not 16-64 characters.",
                    AuthFailure.UnknownClient => "no enabled application with that ClientId has that KeyId (or the key is revoked). Compare the application's ClientId/KeyId with Applications in SivaCom's config.",
                    AuthFailure.StaleTimestamp => "the caller's clock differs from this server's by more than Security:ClockSkewSeconds.",
                    AuthFailure.BadSignature => "the Secret differs between the application's config and SivaCom's Applications entry.",
                    AuthFailure.Replayed => "this exact request (nonce) was already used.",
                    _ => "",
                });
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
