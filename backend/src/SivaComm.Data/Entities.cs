namespace SivaComm.Data;

// Every status is stored as its enum name (see CommDbContext), so a row read
// in psql says "Verified", not 2, and reordering an enum cannot silently
// change what existing rows mean.

/// <summary>A consuming application (HMS_WEB, Swarnakshi, …). Its identity
/// comes only from a verified request signature, never from a field in a
/// request body.</summary>
public class Application
{
    public Guid Id { get; set; }

    /// <summary>Short stable id used in the X-SivaComm-Client header, e.g. "hms-web".</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public List<ApplicationKey> Keys { get; set; } = [];
    public List<ApplicationTemplate> Templates { get; set; } = [];
}

/// <summary>
/// One signing secret of an application. Two can be live at once, which is
/// what makes rotation possible without downtime: issue the new key, deploy
/// it to the application, then revoke the old one.
///
/// The secret is stored encrypted rather than hashed because HMAC
/// verification needs the secret itself on this side.
/// </summary>
public class ApplicationKey
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }

    /// <summary>Sent as X-SivaComm-Key-Id; unique within the application.</summary>
    public string KeyId { get; set; } = "";

    public byte[] SecretEncrypted { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public enum MessagePurpose
{
    PasswordResetOtp,
    Welcome,
}

/// <summary>
/// Which approved provider template an application uses for a purpose.
/// Per application because each product words its welcome differently and
/// each template is approved by Meta separately.
/// </summary>
public class ApplicationTemplate
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }

    public MessagePurpose Purpose { get; set; }
    public string TemplateName { get; set; } = "";
    public string LanguageCode { get; set; } = "en";

    /// <summary>How many body parameters the template declares. A welcome
    /// request with a different count is refused up front rather than failing
    /// at Meta minutes later.</summary>
    public int ParameterCount { get; set; }
}

public enum ChallengeStatus
{
    Pending,
    Verified,
    Expired,
    AttemptsExceeded,
    Cancelled,

    /// <summary>A newer challenge for the same user replaced this one.</summary>
    Superseded,
}

/// <summary>
/// One OTP challenge, bound to the application, the application's own
/// reset transaction and its user reference. The OTP itself is never stored:
/// only a keyed HMAC of it, and the key is not in the database.
/// </summary>
public class OtpChallenge
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }

    /// <summary>The source application's reset transaction id. Unique per application.</summary>
    public string ResetReference { get; set; } = "";

    /// <summary>The source application's pseudonymous id for the user. Never a username.</summary>
    public string UserReference { get; set; } = "";

    public MessagePurpose Purpose { get; set; } = MessagePurpose.PasswordResetOtp;

    public byte[] OtpHmac { get; set; } = [];
    public string RecipientMasked { get; set; } = "";

    public ChallengeStatus Status { get; set; } = ChallengeStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }

    public int SendCount { get; set; }
    public DateTime LastSentAt { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? VerifiedAt { get; set; }

    /// <summary>Set when the source backend reports that it changed the
    /// password. Reporting only — nothing in SivaComm depends on it.</summary>
    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Issued once, on a successful verification. Stored as a SHA-256 of a
/// 256-bit random value — enough entropy that an unsalted hash is not
/// guessable, unlike a six-digit OTP.
/// </summary>
public class VerificationGrant
{
    public Guid Id { get; set; }
    public Guid ChallengeId { get; set; }
    public OtpChallenge? Challenge { get; set; }

    public byte[] GrantHash { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>The Idempotency-Key the grant was consumed with. A retry
    /// carrying the same key gets the same success; any other key is a
    /// replay.</summary>
    public string? ConsumeKey { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum MessageChannel
{
    WhatsApp,
}

/// <summary>
/// Delivery states only — nothing here says whether an OTP was verified.
/// Ordered by progress: a webhook never moves a message backwards.
/// </summary>
public enum MessageStatus
{
    Queued,

    /// <summary>The provider accepted it and returned a message id.</summary>
    Submitted,
    Sent,
    Delivered,
    Read,
    Failed,

    /// <summary>Never sent because its OTP challenge ended first.</summary>
    Expired,
}

/// <summary>
/// The outbox. A row is written in the same transaction as the challenge (or
/// the welcome request) and the worker sends it later, so an API call never
/// waits on Meta.
/// </summary>
public class Message
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }

    public Guid? ChallengeId { get; set; }
    public MessagePurpose Purpose { get; set; }
    public MessageChannel Channel { get; set; } = MessageChannel.WhatsApp;

    /// <summary>For a welcome: the caller's idempotency reference, unique per application.</summary>
    public string? ClientReference { get; set; }

    public string TemplateName { get; set; } = "";
    public string LanguageCode { get; set; } = "en";

    /// <summary>The normalised number, encrypted. Wiped by the retention job.</summary>
    public byte[]? RecipientEncrypted { get; set; }
    public string RecipientMasked { get; set; } = "";

    /// <summary>
    /// Template parameters, encrypted — for an OTP message this includes the
    /// code, because the worker must send it. Wiped the moment the message
    /// reaches a terminal state or is handed to the provider, so it exists
    /// only for the seconds between being queued and being sent.
    /// </summary>
    public byte[]? PayloadEncrypted { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedUntil { get; set; }

    public string? ProviderMessageId { get; set; }

    /// <summary>Provider error code and title only — never a response body,
    /// which can echo the recipient back.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? FailedAt { get; set; }
}

/// <summary>
/// One delivery event from a provider webhook. Unique on its event key so a
/// redelivered webhook is a no-op. Kept even when it arrives before the
/// worker has recorded the provider id — the worker reconciles it then.
/// </summary>
public class MessageEvent
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }

    /// <summary>"{providerMessageId}:{status}".</summary>
    public string EventKey { get; set; } = "";

    public string ProviderMessageId { get; set; } = "";
    public MessageStatus Status { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? ErrorCode { get; set; }
    public bool Applied { get; set; }
}

/// <summary>A nonce seen in a signed request. Its primary key is what makes a
/// replayed request fail: the second insert conflicts.</summary>
public class RequestNonce
{
    public Guid ApplicationId { get; set; }
    public string Nonce { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AuditEvent
{
    public long Id { get; set; }
    public Guid? ApplicationId { get; set; }

    /// <summary>See <see cref="AuditTypes"/>.</summary>
    public string EventType { get; set; } = "";

    public Guid? ChallengeId { get; set; }
    public Guid? MessageId { get; set; }
    public string? CorrelationId { get; set; }

    /// <summary>Short and safe: never an OTP, grant, phone, secret or password.</summary>
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class AuditTypes
{
    public const string ChallengeCreated = "ChallengeCreated";
    public const string ChallengeResent = "ChallengeResent";
    public const string ChallengeRateLimited = "ChallengeRateLimited";
    public const string OtpVerified = "OtpVerified";
    public const string OtpVerifyFailed = "OtpVerifyFailed";
    public const string GrantConsumed = "GrantConsumed";
    public const string GrantRejected = "GrantRejected";
    public const string ResetCompleted = "ResetCompleted";
    public const string ChallengeCancelled = "ChallengeCancelled";
    public const string AuthFailed = "AuthFailed";
    public const string ApplicationRegistered = "ApplicationRegistered";
    public const string KeyIssued = "KeyIssued";
    public const string KeyRevoked = "KeyRevoked";
    public const string AdminLogin = "AdminLogin";
    public const string AdminLoginFailed = "AdminLoginFailed";
}

public class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
