using Microsoft.AspNetCore.Mvc;
using SivaComm.Data.Services;

namespace SivaComm.Api.Contracts;

// ── Requests ───────────────────────────────────────────────────────────────
//
// None of these has, or can be given, a password field: unknown JSON members
// are rejected (Program.cs), so a "newPassword" sent by mistake is a 400, not
// silently carried into a log.

public sealed record CreateChallengeRequest(string ResetReference, string UserReference, string Phone, string? Purpose);

public sealed record ChallengeReferenceRequest(string ResetReference);

public sealed record VerifyOtpRequest(string ResetReference, string Otp);

public sealed record CompletionRequest(string ResetReference, string Outcome);

public sealed record ConsumeGrantRequest(string Grant, string ResetReference);

public sealed record WelcomeRequest(string ClientReference, string Phone, IReadOnlyList<string> Parameters);

// ── The one response shape for every application endpoint ──────────────────

public sealed class ApiResponse
{
    public bool Success { get; init; }
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public Guid? ChallengeId { get; init; }
    public Guid? MessageId { get; init; }
    public string? ResetReference { get; init; }
    public string? UserReference { get; init; }
    public int? RemainingAttempts { get; init; }

    /// <summary>Only ever set on VERIFIED.</summary>
    public string? VerificationGrant { get; init; }

    public DateTime? ExpiresAtUtc { get; init; }
    public DateTime? ResendAvailableAtUtc { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public bool? Replayed { get; init; }
    public string CorrelationId { get; init; } = "";
}

/// <summary>
/// Outcome → wire status and HTTP code. One table, so every endpoint
/// reports the same failure the same way.
/// </summary>
public static class ApiStatus
{
    public static string Name(Outcome o) => o switch
    {
        Outcome.Queued => "QUEUED",
        Outcome.Verified => "VERIFIED",
        Outcome.Consumed => "CONSUMED",
        Outcome.Completed => "COMPLETED",
        Outcome.Cancelled => "CANCELLED",
        Outcome.InvalidOtp => "INVALID_OTP",
        Outcome.Expired => "EXPIRED",
        Outcome.AttemptsExceeded => "ATTEMPTS_EXCEEDED",
        Outcome.AlreadyUsed => "ALREADY_USED",
        Outcome.RateLimited => "RATE_LIMITED",
        Outcome.ChallengeNotFound => "CHALLENGE_NOT_FOUND",
        Outcome.GrantNotFound => "GRANT_NOT_FOUND",
        Outcome.Conflict => "CONFLICT",
        Outcome.InvalidRequest => "INVALID_REQUEST",
        _ => "TEMPORARY_ERROR",
    };

    public static int Http(Outcome o) => o switch
    {
        Outcome.Queued => 202,
        Outcome.Verified or Outcome.Consumed or Outcome.Completed => 200,
        Outcome.InvalidOtp or Outcome.Expired or Outcome.AttemptsExceeded => 422,
        Outcome.AlreadyUsed or Outcome.Cancelled or Outcome.Conflict => 409,
        Outcome.RateLimited => 429,
        Outcome.ChallengeNotFound or Outcome.GrantNotFound => 404,
        Outcome.InvalidRequest => 400,
        _ => 503,
    };

    private static string? DefaultMessage(Outcome o) => o switch
    {
        Outcome.InvalidOtp => "The OTP is invalid. Please try again.",
        Outcome.Expired => "The OTP has expired. Request a new one.",
        Outcome.AttemptsExceeded => "Too many incorrect attempts. Start the reset again.",
        Outcome.AlreadyUsed => "This has already been used. Start the reset again.",
        Outcome.Cancelled => "This reset was cancelled. Start the reset again.",
        Outcome.RateLimited => "Too many requests. Try again later.",
        Outcome.ChallengeNotFound => "No such challenge.",
        Outcome.GrantNotFound => "No such grant.",
        Outcome.TemporaryError => "Temporary failure. Try again.",
        _ => null,
    };

    /// <param name="successOverride">For endpoints where an outcome that is a
    /// failure elsewhere is the success here (CANCELLED from the cancel endpoint).</param>
    public static IActionResult Result(HttpContext http, Outcome outcome, Func<ApiResponse, ApiResponse>? fill = null,
        string? message = null, TimeSpan? retryAfter = null, Outcome? successOverride = null)
    {
        var success = outcome is Outcome.Queued or Outcome.Verified or Outcome.Consumed or Outcome.Completed
                      || outcome == successOverride;

        var body = new ApiResponse
        {
            Success = success,
            Status = Name(outcome),
            Message = message ?? DefaultMessage(outcome),
            RetryAfterSeconds = retryAfter is { } r ? (int)Math.Ceiling(r.TotalSeconds) : null,
            CorrelationId = http.CorrelationId(),
        };
        if (fill is not null) body = fill(body);

        if (retryAfter is { } wait) http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();

        return new ObjectResult(body) { StatusCode = outcome == successOverride ? 200 : Http(outcome) };
    }
}
