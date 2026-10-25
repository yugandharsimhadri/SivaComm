using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SivaComm.Data.Security;

/// <summary>
/// The signature every application request carries. Specified fully in
/// docs/API.md so an application in any language can implement it; HMS_WEB's
/// SivaCommClient is the reference implementation.
///
/// <code>
/// canonical = METHOD + "\n" + PATH_AND_QUERY + "\n" + TIMESTAMP + "\n" + NONCE + "\n" + hex(SHA256(body))
/// signature = base64(HMAC-SHA256(secret, canonical))
/// </code>
/// </summary>
public static class RequestSigning
{
    public const string ClientHeader = "X-SivaComm-Client";
    public const string KeyIdHeader = "X-SivaComm-Key-Id";
    public const string TimestampHeader = "X-SivaComm-Timestamp";
    public const string NonceHeader = "X-SivaComm-Nonce";
    public const string SignatureHeader = "X-SivaComm-Signature";

    public static string Canonical(string method, string pathAndQuery, string timestamp, string nonce, byte[] body) =>
        string.Join('\n',
            method.ToUpperInvariant(),
            pathAndQuery,
            timestamp,
            nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)));

    public static string Sign(string secret, string canonical) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
}

public sealed record SignedRequest(
    string? ClientId, string? KeyId, string? Timestamp, string? Nonce, string? Signature,
    string Method, string PathAndQuery, byte[] Body);

public enum AuthFailure
{
    None,
    MissingHeaders,
    UnknownClient,
    StaleTimestamp,
    BadSignature,
    Replayed,
}

/// <summary>
/// Checks a signed request. On success the application is the one whose key
/// produced the signature — the only source of application identity in
/// SivaComm.
/// </summary>
public sealed class RequestAuthenticator(
    IDbContextFactory<CommDbContext> factory, Crypto crypto, TimeProvider time, IOptions<SecurityOptions> options)
{
    public async Task<(AuthFailure Failure, Guid ApplicationId, string ApplicationCode)> AuthenticateAsync(
        SignedRequest r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.ClientId) || string.IsNullOrEmpty(r.KeyId) || string.IsNullOrEmpty(r.Timestamp) ||
            string.IsNullOrEmpty(r.Nonce) || string.IsNullOrEmpty(r.Signature) || r.Nonce.Length is < 16 or > 64)
            return (AuthFailure.MissingHeaders, default, "");

        var now = time.GetUtcNow();
        if (!DateTimeOffset.TryParse(r.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sent) ||
            Math.Abs((now - sent).TotalSeconds) > options.Value.ClockSkewSeconds)
            return (AuthFailure.StaleTimestamp, default, "");

        await using var db = await factory.CreateDbContextAsync(ct);

        var key = await db.ApplicationKeys
            .Include(k => k.Application)
            .FirstOrDefaultAsync(k => k.Application!.Code == r.ClientId && k.KeyId == r.KeyId, ct);

        // Unknown client, unknown key, revoked key and disabled application
        // are all one failure: a caller learns nothing about which it hit.
        if (key is null || key.RevokedAt is not null || key.Application is not { IsActive: true })
            return (AuthFailure.UnknownClient, default, "");

        var expected = RequestSigning.Sign(crypto.Decrypt(key.SecretEncrypted),
            RequestSigning.Canonical(r.Method, r.PathAndQuery, r.Timestamp, r.Nonce, r.Body));

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(r.Signature)))
            return (AuthFailure.BadSignature, default, "");

        // Checked after the signature, so only the key holder can spend a
        // nonce. The primary key makes the second use fail atomically.
        db.RequestNonces.Add(new RequestNonce { ApplicationId = key.ApplicationId, Nonce = r.Nonce, CreatedAt = now.UtcDateTime });

        // Throttled to once a minute: the dashboard wants "last used", not a
        // write per request.
        if (key.LastUsedAt is null || key.LastUsedAt < now.UtcDateTime.AddMinutes(-1)) key.LastUsedAt = now.UtcDateTime;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return (AuthFailure.Replayed, default, "");
        }

        return (AuthFailure.None, key.ApplicationId, key.Application.Code);
    }
}
