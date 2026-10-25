namespace SivaComm.Data.Services;

/// <summary>
/// Every result the OTP endpoints can report. The API turns each into an
/// upper-snake-case status string and an HTTP code (see ApiStatus in the API
/// project); this enum is the single list both sides agree on.
/// </summary>
public enum Outcome
{
    Queued,
    Verified,
    Consumed,
    Completed,
    Cancelled,

    InvalidOtp,
    Expired,
    AttemptsExceeded,
    AlreadyUsed,
    RateLimited,
    ChallengeNotFound,
    GrantNotFound,

    /// <summary>The same reference was reused with different bound data.</summary>
    Conflict,

    /// <summary>Malformed input or a configuration gap (no template).</summary>
    InvalidRequest,

    TemporaryError,
}

/// <summary>Who is calling and which request this is. The application id is
/// taken from the verified signature, never from a request body.</summary>
public sealed record CallerContext(Guid ApplicationId, string ApplicationCode, string CorrelationId);

public sealed record CreateChallengeResult(
    Outcome Outcome,
    Guid? ChallengeId = null,
    DateTime? ExpiresAt = null,
    DateTime? ResendAvailableAt = null,
    TimeSpan? RetryAfter = null,
    string? Error = null);

public sealed record VerifyResult(
    Outcome Outcome,
    int? RemainingAttempts = null,
    string? Grant = null,
    DateTime? GrantExpiresAt = null);

public sealed record ConsumeResult(
    Outcome Outcome,
    Guid? ChallengeId = null,
    string? ResetReference = null,
    string? UserReference = null,
    bool Replayed = false);

public sealed record SimpleResult(Outcome Outcome, string? Error = null);

public sealed record QueueMessageResult(Outcome Outcome, Guid? MessageId = null, MessageStatus? Status = null, string? Error = null);
