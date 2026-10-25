using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

/// <summary>
/// OTP challenges and verification grants. The only class that ever sees an
/// OTP in the clear, and only for the instant between generating it and
/// encrypting it into the outbox row.
///
/// Every state change happens inside one short transaction that holds a row
/// lock on the challenge (or grant), so two concurrent requests for the same
/// challenge are applied one after the other — never interleaved. That is
/// what makes "two correct submissions produce one grant" and "a grant is
/// consumed once" true under load rather than merely in a single-threaded
/// test.
/// </summary>
public sealed class OtpService(
    IDbContextFactory<CommDbContext> factory,
    Crypto crypto,
    TimeProvider time,
    OutboxSignal outbox,
    IOptions<OtpOptions> otpOptions,
    IOptions<MessagingOptions> messagingOptions)
{
    private readonly OtpOptions _otp = otpOptions.Value;
    private readonly MessagingOptions _messaging = messagingOptions.Value;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Create ──────────────────────────────────────────────────────────────

    public async Task<CreateChallengeResult> CreateAsync(
        CallerContext caller, string resetReference, string userReference, string phone, CancellationToken ct)
    {
        if (!IsReference(resetReference) || !IsReference(userReference))
            return new(Outcome.InvalidRequest, Error: "resetReference and userReference must be 8–100 characters of [A-Za-z0-9._:-].");

        if (!PhoneNumbers.TryNormalise(phone, _messaging.DefaultCountryCode, out var normalised))
            return new(Outcome.InvalidRequest, Error: "phone is not a number with a usable country code.");

        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);

        var template = await db.ApplicationTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApplicationId == caller.ApplicationId && t.Purpose == MessagePurpose.PasswordResetOtp, ct);
        if (template is null)
            return new(Outcome.InvalidRequest, Error: "No password-reset OTP template is configured for this application.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serialise everything for this one user, so two simultaneous
        // "forgot password" clicks cannot both slip under the send cap.
        await LockUserAsync(db, caller.ApplicationId, userReference, ct);

        var existing = await db.OtpChallenges
            .FirstOrDefaultAsync(c => c.ApplicationId == caller.ApplicationId && c.ResetReference == resetReference, ct);

        if (existing is not null)
        {
            // A retried create (a lost response) gets the same challenge back,
            // without a second message. The same reference reused for another
            // user is a caller bug, and is refused rather than rebound.
            if (existing.UserReference != userReference)
                return new(Outcome.Conflict, Error: "resetReference is already bound to a different user.");

            return new(Outcome.Queued, existing.Id, existing.ExpiresAt, existing.LastSentAt.AddSeconds(_otp.ResendCooldownSeconds));
        }

        var limited = await CheckSendCapAsync(db, caller, userReference, now, ct);
        if (limited is not null)
        {
            await tx.CommitAsync(ct);
            return limited;
        }

        // A new code kills every earlier one for the same user. Two live codes
        // would double an attacker's chances for free.
        var superseded = await db.OtpChallenges
            .Where(c => c.ApplicationId == caller.ApplicationId && c.UserReference == userReference && c.Status == ChallengeStatus.Pending)
            .ToListAsync(ct);
        foreach (var old in superseded)
        {
            old.Status = ChallengeStatus.Superseded;
            await ExpireQueuedMessagesAsync(db, old.Id, now, ct);
        }

        var challenge = new OtpChallenge
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = caller.ApplicationId,
            ResetReference = resetReference,
            UserReference = userReference,
            Purpose = MessagePurpose.PasswordResetOtp,
            RecipientMasked = PhoneNumbers.Mask(normalised),
            Status = ChallengeStatus.Pending,
            MaxAttempts = _otp.MaxAttempts,
            SendCount = 1,
            LastSentAt = now,
            ExpiresAt = now.AddMinutes(_otp.ExpiryMinutes),
            CreatedAt = now,
        };

        var otp = Crypto.NewOtp(_otp.Length);
        challenge.OtpHmac = crypto.OtpHmac(challenge.Id, otp);
        db.OtpChallenges.Add(challenge);

        db.Messages.Add(NewOtpMessage(caller.ApplicationId, challenge, template, crypto.Encrypt(normalised), otp, now));
        Audit(db, caller, AuditTypes.ChallengeCreated, now, challenge.Id);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        outbox.Notify();

        return new(Outcome.Queued, challenge.Id, challenge.ExpiresAt, now.AddSeconds(_otp.ResendCooldownSeconds));
    }

    // ── Resend ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A fresh code on the same challenge. The old code stops working, the
    /// expiry restarts, and the attempt counter does <b>not</b> reset — a
    /// resend must not be a way to buy five more guesses.
    /// </summary>
    public async Task<CreateChallengeResult> ResendAsync(
        CallerContext caller, Guid challengeId, string resetReference, CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var challenge = await LockChallengeAsync(db, caller, challengeId, resetReference, ct);
        if (challenge is null) return new(Outcome.ChallengeNotFound);

        await LockUserAsync(db, caller.ApplicationId, challenge.UserReference, ct);

        if (challenge.Status == ChallengeStatus.Pending && challenge.ExpiresAt <= now)
        {
            challenge.Status = ChallengeStatus.Expired;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(Outcome.Expired);
        }

        if (challenge.Status != ChallengeStatus.Pending) return new(TerminalOutcome(challenge.Status));

        var cooldownEnds = challenge.LastSentAt.AddSeconds(_otp.ResendCooldownSeconds);
        if (now < cooldownEnds)
            return new(Outcome.RateLimited, challenge.Id, ResendAvailableAt: cooldownEnds, RetryAfter: cooldownEnds - now);

        var limited = await CheckSendCapAsync(db, caller, challenge.UserReference, now, ct);
        if (limited is not null)
        {
            await tx.CommitAsync(ct);
            return limited;
        }

        var previous = await db.Messages
            .Where(m => m.ChallengeId == challenge.Id && m.RecipientEncrypted != null)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // The phone is only ever held encrypted on the message rows. If the
        // retention job has wiped it, a resend has nowhere to go — the source
        // app starts a new challenge, supplying the number again.
        if (previous?.RecipientEncrypted is null) return new(Outcome.Expired);

        var template = await db.ApplicationTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApplicationId == caller.ApplicationId && t.Purpose == MessagePurpose.PasswordResetOtp, ct);
        if (template is null) return new(Outcome.InvalidRequest, Error: "No password-reset OTP template is configured.");

        await ExpireQueuedMessagesAsync(db, challenge.Id, now, ct);

        var otp = Crypto.NewOtp(_otp.Length);
        challenge.OtpHmac = crypto.OtpHmac(challenge.Id, otp);
        challenge.SendCount++;
        challenge.LastSentAt = now;
        challenge.ExpiresAt = now.AddMinutes(_otp.ExpiryMinutes);

        db.Messages.Add(NewOtpMessage(caller.ApplicationId, challenge, template, previous.RecipientEncrypted, otp, now));
        Audit(db, caller, AuditTypes.ChallengeResent, now, challenge.Id);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        outbox.Notify();

        return new(Outcome.Queued, challenge.Id, challenge.ExpiresAt, now.AddSeconds(_otp.ResendCooldownSeconds));
    }

    // ── Verify ──────────────────────────────────────────────────────────────

    public async Task<VerifyResult> VerifyAsync(
        CallerContext caller, Guid challengeId, string resetReference, string otp, CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var challenge = await LockChallengeAsync(db, caller, challengeId, resetReference, ct);
        if (challenge is null)
        {
            Audit(db, caller, AuditTypes.OtpVerifyFailed, now, null, "ChallengeNotFound");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(Outcome.ChallengeNotFound);
        }

        if (challenge.Status == ChallengeStatus.Pending && challenge.ExpiresAt <= now)
        {
            challenge.Status = ChallengeStatus.Expired;
            Audit(db, caller, AuditTypes.OtpVerifyFailed, now, challenge.Id, "Expired");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(Outcome.Expired);
        }

        if (challenge.Status != ChallengeStatus.Pending)
        {
            var terminal = TerminalOutcome(challenge.Status);
            Audit(db, caller, AuditTypes.OtpVerifyFailed, now, challenge.Id, terminal.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(terminal);
        }

        // Malformed input costs an attempt like a wrong code does. Otherwise
        // the counter could be sidestepped by probing with junk.
        var wellFormed = otp.Length == _otp.Length && otp.All(char.IsAsciiDigit);

        if (!wellFormed || !crypto.OtpMatches(challenge.Id, otp, challenge.OtpHmac))
        {
            challenge.Attempts++;
            var exhausted = challenge.Attempts >= challenge.MaxAttempts;
            if (exhausted) challenge.Status = ChallengeStatus.AttemptsExceeded;

            Audit(db, caller, AuditTypes.OtpVerifyFailed, now, challenge.Id, exhausted ? "AttemptsExceeded" : "InvalidOtp");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return exhausted
                ? new(Outcome.AttemptsExceeded, RemainingAttempts: 0)
                : new(Outcome.InvalidOtp, RemainingAttempts: challenge.MaxAttempts - challenge.Attempts);
        }

        challenge.Status = ChallengeStatus.Verified;
        challenge.VerifiedAt = now;

        var grant = Crypto.NewToken();
        var expires = now.AddMinutes(_otp.GrantExpiryMinutes);
        db.VerificationGrants.Add(new VerificationGrant
        {
            Id = Guid.CreateVersion7(),
            ChallengeId = challenge.Id,
            GrantHash = Crypto.Sha256(grant),
            ExpiresAt = expires,
            CreatedAt = now,
        });

        Audit(db, caller, AuditTypes.OtpVerified, now, challenge.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new(Outcome.Verified, Grant: grant, GrantExpiresAt: expires);
    }

    // ── Consume ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Spends a grant, exactly once. A retry with the same idempotency key
    /// after a lost response is told "consumed" again (with Replayed set); any
    /// other key is told the grant is already used. That is what lets a source
    /// application retry safely without letting anyone else reuse the grant.
    /// </summary>
    public async Task<ConsumeResult> ConsumeAsync(
        CallerContext caller, string grant, string resetReference, string idempotencyKey, CancellationToken ct)
    {
        var now = Now;
        var hash = Crypto.Sha256(grant ?? "");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var row = await db.VerificationGrants
            .FromSql($"""
                SELECT g.* FROM comm.verification_grants g
                JOIN comm.otp_challenges c ON c.id = g.challenge_id
                WHERE g.grant_hash = {hash} AND c.application_id = {caller.ApplicationId}
                FOR UPDATE OF g
                """)
            .SingleOrDefaultAsync(ct);

        var challenge = row is null ? null : await db.OtpChallenges.FirstAsync(c => c.Id == row.ChallengeId, ct);

        // A grant presented with another transaction's reference is treated
        // exactly as an unknown grant: binding is part of what it proves.
        if (row is null || challenge is null || challenge.ResetReference != resetReference)
        {
            Audit(db, caller, AuditTypes.GrantRejected, now, challenge?.Id, "GrantNotFound");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(Outcome.GrantNotFound);
        }

        if (row.ConsumedAt is not null)
        {
            if (row.ConsumeKey == idempotencyKey)
                return new(Outcome.Consumed, challenge.Id, challenge.ResetReference, challenge.UserReference, Replayed: true);

            Audit(db, caller, AuditTypes.GrantRejected, now, challenge.Id, "AlreadyUsed");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(Outcome.AlreadyUsed);
        }

        if (row.ExpiresAt <= now || challenge.Status != ChallengeStatus.Verified)
        {
            var outcome = challenge.Status == ChallengeStatus.Cancelled ? Outcome.Cancelled : Outcome.Expired;
            Audit(db, caller, AuditTypes.GrantRejected, now, challenge.Id, outcome.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(outcome);
        }

        row.ConsumedAt = now;
        row.ConsumeKey = idempotencyKey;
        Audit(db, caller, AuditTypes.GrantConsumed, now, challenge.Id);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new(Outcome.Consumed, challenge.Id, challenge.ResetReference, challenge.UserReference);
    }

    // ── Cancel and completion ───────────────────────────────────────────────

    public async Task<SimpleResult> CancelAsync(CallerContext caller, Guid challengeId, string resetReference, CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var challenge = await LockChallengeAsync(db, caller, challengeId, resetReference, ct);
        if (challenge is null) return new(Outcome.ChallengeNotFound);

        if (challenge.Status is ChallengeStatus.Pending or ChallengeStatus.Verified)
        {
            challenge.Status = ChallengeStatus.Cancelled;
            await ExpireQueuedMessagesAsync(db, challenge.Id, now, ct);

            // An unspent grant dies with its challenge.
            var grant = await db.VerificationGrants.FirstOrDefaultAsync(g => g.ChallengeId == challenge.Id && g.ConsumedAt == null, ct);
            if (grant is not null && grant.ExpiresAt > now) grant.ExpiresAt = now;

            Audit(db, caller, AuditTypes.ChallengeCancelled, now, challenge.Id);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new(Outcome.Cancelled);
    }

    /// <summary>The source application reporting that it changed the
    /// password. Accepted only after the grant was consumed; idempotent.</summary>
    public async Task<SimpleResult> CompleteAsync(CallerContext caller, Guid challengeId, string resetReference, CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var challenge = await LockChallengeAsync(db, caller, challengeId, resetReference, ct);
        if (challenge is null) return new(Outcome.ChallengeNotFound);

        if (challenge.CompletedAt is not null) return new(Outcome.Completed);

        var consumed = await db.VerificationGrants.AnyAsync(g => g.ChallengeId == challenge.Id && g.ConsumedAt != null, ct);
        if (!consumed) return new(Outcome.Conflict, "The grant for this challenge has not been consumed.");

        challenge.CompletedAt = now;
        Audit(db, caller, AuditTypes.ResetCompleted, now, challenge.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(Outcome.Completed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsReference(string? value) =>
        value is { Length: >= 8 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    private static Outcome TerminalOutcome(ChallengeStatus status) => status switch
    {
        ChallengeStatus.Verified => Outcome.AlreadyUsed,
        ChallengeStatus.Expired => Outcome.Expired,
        ChallengeStatus.AttemptsExceeded => Outcome.AttemptsExceeded,
        ChallengeStatus.Cancelled or ChallengeStatus.Superseded => Outcome.Cancelled,
        _ => Outcome.TemporaryError,
    };

    /// <summary>
    /// The challenge, row-locked for the rest of the transaction — but only
    /// if it belongs to the caller and carries the caller's reset reference.
    /// Anything else is reported as not found, so one application cannot
    /// even learn that another's challenge id exists.
    /// </summary>
    private static async Task<OtpChallenge?> LockChallengeAsync(
        CommDbContext db, CallerContext caller, Guid challengeId, string resetReference, CancellationToken ct)
    {
        var challenge = await db.OtpChallenges
            .FromSql($"SELECT * FROM comm.otp_challenges WHERE id = {challengeId} AND application_id = {caller.ApplicationId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

        return challenge is not null && challenge.ResetReference == resetReference ? challenge : null;
    }

    private static Task LockUserAsync(CommDbContext db, Guid applicationId, string userReference, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({applicationId.ToString("N") + ":" + userReference}, 0))", ct);

    private async Task<CreateChallengeResult?> CheckSendCapAsync(
        CommDbContext db, CallerContext caller, string userReference, DateTime now, CancellationToken ct)
    {
        var windowStart = now.AddMinutes(-_otp.SendWindowMinutes);

        // Counted from the message rows, so a resend on an older challenge
        // counts in the window it was actually sent in.
        var sent = await db.Messages
            .Where(m => m.ApplicationId == caller.ApplicationId
                        && m.Purpose == MessagePurpose.PasswordResetOtp
                        && m.CreatedAt >= windowStart
                        && db.OtpChallenges.Any(c => c.Id == m.ChallengeId && c.UserReference == userReference))
            .Select(m => m.CreatedAt)
            .OrderBy(t => t)
            .ToListAsync(ct);

        if (sent.Count < _otp.MaxSendsPerWindow) return null;

        Audit(db, caller, AuditTypes.ChallengeRateLimited, now, null);
        await db.SaveChangesAsync(ct);

        var retryAfter = sent[0].AddMinutes(_otp.SendWindowMinutes) - now;
        return new(Outcome.RateLimited, RetryAfter: retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1));
    }

    private static async Task ExpireQueuedMessagesAsync(CommDbContext db, Guid challengeId, DateTime now, CancellationToken ct)
    {
        var queued = await db.Messages
            .Where(m => m.ChallengeId == challengeId && m.Status == MessageStatus.Queued)
            .ToListAsync(ct);

        foreach (var m in queued)
        {
            m.Status = MessageStatus.Expired;
            m.PayloadEncrypted = null;
            m.UpdatedAt = now;
        }
    }

    private Message NewOtpMessage(
        Guid applicationId, OtpChallenge challenge, ApplicationTemplate template, byte[] recipient, string otp, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        ApplicationId = applicationId,
        ChallengeId = challenge.Id,
        Purpose = MessagePurpose.PasswordResetOtp,
        TemplateName = template.TemplateName,
        LanguageCode = template.LanguageCode,
        RecipientEncrypted = recipient,
        RecipientMasked = challenge.RecipientMasked,
        PayloadEncrypted = crypto.Encrypt(JsonSerializer.Serialize(new MessagePayload([otp]))),
        Status = MessageStatus.Queued,
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    internal static void Audit(CommDbContext db, CallerContext caller, string type, DateTime now, Guid? challengeId, string? detail = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            ApplicationId = caller.ApplicationId,
            EventType = type,
            ChallengeId = challengeId,
            CorrelationId = caller.CorrelationId,
            Detail = detail,
            CreatedAt = now,
        });
}

/// <summary>What is encrypted into <see cref="Message.PayloadEncrypted"/>.</summary>
public sealed record MessagePayload(IReadOnlyList<string> Parameters);
