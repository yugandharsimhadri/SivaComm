using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SivaComm.Api.Auth;
using SivaComm.Api.Contracts;
using SivaComm.Data.Services;

namespace SivaComm.Api.Controllers;

/// <summary>
/// OTP challenges for password reset. Callable only by a source
/// application's backend with a valid signature — never by a browser.
/// The verification result is returned synchronously here; it never depends
/// on, or waits for, WhatsApp delivery.
/// </summary>
[ApiController]
[Route("api/v1/otp/challenges")]
[Authorize(Policy = AppClaims.Policy)]
public sealed class OtpController(OtpService otp) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimits.Create)]
    public async Task<IActionResult> Create(CreateChallengeRequest request, CancellationToken ct)
    {
        if (request.Purpose is not (null or "PASSWORD_RESET"))
            return ApiStatus.Result(HttpContext, Outcome.InvalidRequest, message: "purpose must be PASSWORD_RESET.");

        var r = await otp.CreateAsync(HttpContext.Caller(), request.ResetReference, request.UserReference, request.Phone, ct);
        return Challenge(r, request.ResetReference);
    }

    [HttpPost("{challengeId:guid}/resend")]
    [EnableRateLimiting(RateLimits.Create)]
    public async Task<IActionResult> Resend(Guid challengeId, ChallengeReferenceRequest request, CancellationToken ct)
    {
        var r = await otp.ResendAsync(HttpContext.Caller(), challengeId, request.ResetReference, ct);
        return Challenge(r, request.ResetReference);
    }

    [HttpPost("{challengeId:guid}/verify")]
    [EnableRateLimiting(RateLimits.Verify)]
    public async Task<IActionResult> Verify(Guid challengeId, VerifyOtpRequest request, CancellationToken ct)
    {
        var r = await otp.VerifyAsync(HttpContext.Caller(), challengeId, request.ResetReference, request.Otp ?? "", ct);

        return ApiStatus.Result(HttpContext, r.Outcome, b => new ApiResponse
        {
            Success = b.Success,
            Status = b.Status,
            Message = b.Message,
            ChallengeId = challengeId,
            ResetReference = r.Outcome == Outcome.Verified ? request.ResetReference : null,
            RemainingAttempts = r.RemainingAttempts,
            VerificationGrant = r.Outcome == Outcome.Verified ? r.Grant : null,
            ExpiresAtUtc = r.GrantExpiresAt,
            CorrelationId = b.CorrelationId,
        });
    }

    [HttpPost("{challengeId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid challengeId, ChallengeReferenceRequest request, CancellationToken ct)
    {
        var r = await otp.CancelAsync(HttpContext.Caller(), challengeId, request.ResetReference, ct);
        return ApiStatus.Result(HttpContext, r.Outcome, successOverride: Outcome.Cancelled);
    }

    /// <summary>The source backend reporting that it changed the password.
    /// Reporting only: SivaComm never changes, or even sees, a password.</summary>
    [HttpPost("{challengeId:guid}/completion")]
    public async Task<IActionResult> Completion(Guid challengeId, CompletionRequest request, CancellationToken ct)
    {
        if (request.Outcome != "PASSWORD_CHANGED")
            return ApiStatus.Result(HttpContext, Outcome.InvalidRequest, message: "outcome must be PASSWORD_CHANGED.");

        var r = await otp.CompleteAsync(HttpContext.Caller(), challengeId, request.ResetReference, ct);
        return ApiStatus.Result(HttpContext, r.Outcome, message: r.Error);
    }

    private IActionResult Challenge(CreateChallengeResult r, string resetReference) =>
        ApiStatus.Result(HttpContext, r.Outcome, b => new ApiResponse
        {
            Success = b.Success,
            Status = b.Status,
            Message = b.Message,
            ChallengeId = r.ChallengeId,
            ResetReference = r.ChallengeId is null ? null : resetReference,
            ExpiresAtUtc = r.ExpiresAt,
            ResendAvailableAtUtc = r.ResendAvailableAt,
            RetryAfterSeconds = b.RetryAfterSeconds,
            CorrelationId = b.CorrelationId,
        }, message: r.Error, retryAfter: r.RetryAfter);
}

[ApiController]
[Route("api/v1/grants")]
[Authorize(Policy = AppClaims.Policy)]
public sealed class GrantsController(OtpService otp) : ControllerBase
{
    /// <summary>
    /// Spends a verification grant. Idempotency-Key is required: the source
    /// application uses its own reset transaction id, so a retry after a lost
    /// response succeeds again instead of looking like a replay.
    /// </summary>
    [HttpPost("consume")]
    [EnableRateLimiting(RateLimits.Verify)]
    public async Task<IActionResult> Consume(
        ConsumeGrantRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not { Length: >= 8 and <= 100 })
            return ApiStatus.Result(HttpContext, Outcome.InvalidRequest, message: "An Idempotency-Key header of 8–100 characters is required.");

        var r = await otp.ConsumeAsync(HttpContext.Caller(), request.Grant, request.ResetReference, idempotencyKey, ct);

        return ApiStatus.Result(HttpContext, r.Outcome, b => new ApiResponse
        {
            Success = b.Success,
            Status = b.Status,
            Message = b.Message,
            ChallengeId = r.ChallengeId,
            ResetReference = r.ResetReference,
            UserReference = r.UserReference,
            Replayed = r.Outcome == Outcome.Consumed ? r.Replayed : null,
            CorrelationId = b.CorrelationId,
        });
    }
}

[ApiController]
[Route("api/v1/messages")]
[Authorize(Policy = AppClaims.Policy)]
public sealed class MessagesController(MessageService messages) : ControllerBase
{
    [HttpPost("welcome")]
    [EnableRateLimiting(RateLimits.Create)]
    public async Task<IActionResult> Welcome(WelcomeRequest request, CancellationToken ct)
    {
        var r = await messages.QueueWelcomeAsync(HttpContext.Caller(), request.ClientReference, request.Phone, request.Parameters ?? [], ct);
        return ApiStatus.Result(HttpContext, r.Outcome, b => new ApiResponse
        {
            Success = b.Success,
            Status = b.Status,
            Message = b.Message,
            MessageId = r.MessageId,
            CorrelationId = b.CorrelationId,
        }, message: r.Error);
    }

    /// <summary>Delivery status of one of the caller's own messages.</summary>
    [HttpGet("{messageId:guid}")]
    public async Task<IActionResult> Get(Guid messageId, CancellationToken ct)
    {
        var m = await messages.GetAsync(HttpContext.Caller(), messageId, ct);
        if (m is null) return NotFound(new { success = false, status = "MESSAGE_NOT_FOUND", correlationId = HttpContext.CorrelationId() });

        return Ok(new { success = true, status = m.Status.ToString().ToUpperInvariant(), message = m, correlationId = HttpContext.CorrelationId() });
    }
}
