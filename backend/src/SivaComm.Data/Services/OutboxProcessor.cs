using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SivaComm.Data.Providers;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

/// <summary>
/// Sends queued messages. Separate from the hosted service that drives it so
/// a test can run exactly one batch and look at the result.
///
/// Claiming is a single UPDATE … FOR UPDATE SKIP LOCKED that also stamps a
/// short lease, so two worker instances (or two loops of one) never take the
/// same row, and a worker that dies mid-send releases its rows when the lease
/// runs out instead of holding them forever.
/// </summary>
public sealed class OutboxProcessor(
    IDbContextFactory<CommDbContext> factory,
    IMessageProvider provider,
    Crypto crypto,
    TimeProvider time,
    IOptions<WorkerOptions> workerOptions,
    ILogger<OutboxProcessor> logger)
{
    private readonly WorkerOptions _options = workerOptions.Value;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <returns>How many messages were claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var ids = await ClaimAsync(ct);
        if (ids.Count == 0) return 0;

        await Parallel.ForEachAsync(ids,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency), CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    await SendOneAsync(id, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // One bad row must not stop the batch. Its lease expires
                    // and it is picked up again; attempts already counted it.
                    logger.LogError(ex, "Unexpected failure sending message {MessageId}.", id);
                }
            });

        return ids.Count;
    }

    private async Task<List<Guid>> ClaimAsync(CancellationToken ct)
    {
        var now = Now;
        var leaseUntil = now + Lease;

        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<Guid>($"""
            UPDATE comm.messages
               SET locked_until = {leaseUntil}, attempts = attempts + 1, updated_at = {now}
             WHERE id IN (
                   SELECT id FROM comm.messages
                    WHERE status = 'Queued'
                      AND next_attempt_at <= {now}
                      AND (locked_until IS NULL OR locked_until < {now})
                    ORDER BY next_attempt_at
                    LIMIT {_options.BatchSize}
                      FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(ct);
    }

    private async Task SendOneAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null || message.Status != MessageStatus.Queued) return;

        var now = Now;

        // An OTP whose challenge has ended is worth nothing to anyone, and
        // sending it would put a dead code on someone's lock screen.
        if (message.ChallengeId is { } challengeId)
        {
            var challenge = await db.OtpChallenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == challengeId, ct);
            if (challenge is null || challenge.Status != ChallengeStatus.Pending || challenge.ExpiresAt <= now)
            {
                Finish(message, MessageStatus.Expired, now, "challenge ended before sending");
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        ProviderMessage outbound;
        try
        {
            var to = crypto.Decrypt(message.RecipientEncrypted ?? throw new CryptographicException("no recipient"));
            var payload = JsonSerializer.Deserialize<MessagePayload>(
                crypto.Decrypt(message.PayloadEncrypted ?? throw new CryptographicException("no payload")))!;
            outbound = new ProviderMessage(message.Id, message.Purpose, to, message.TemplateName, message.LanguageCode, payload.Parameters);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Usually a changed master key. Retrying cannot fix it.
            logger.LogError("Message {MessageId} could not be decrypted; marking failed.", message.Id);
            Finish(message, MessageStatus.Failed, now, "could not decrypt (master key changed?)");
            await db.SaveChangesAsync(ct);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ProviderTimeoutSeconds));

        ProviderResult result;
        try
        {
            result = await provider.SendAsync(outbound, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ProviderResult.Transient("timeout");
        }

        now = Now;
        switch (result.Kind)
        {
            case ProviderResultKind.Accepted:
                message.Status = MessageStatus.Submitted;
                message.ProviderMessageId = result.ProviderMessageId;
                message.SubmittedAt = now;
                message.PayloadEncrypted = null;
                message.LockedUntil = null;
                message.LastError = null;
                message.UpdatedAt = now;
                await ApplyEarlyEventsAsync(db, message, now, ct);
                break;

            case ProviderResultKind.Transient when message.Attempts < _options.MaxAttempts:
                message.NextAttemptAt = now + Backoff(message.Attempts);
                message.LockedUntil = null;
                message.LastError = Trim(result.Error);
                message.UpdatedAt = now;
                break;

            default:
                Finish(message, MessageStatus.Failed, now, result.Error);
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A webhook for this message may have arrived before the provider's
    /// response did — Meta is fast and our database write comes second. Such
    /// events are stored unattached; apply them now, in their own order.
    /// </summary>
    private static async Task ApplyEarlyEventsAsync(CommDbContext db, Message message, DateTime now, CancellationToken ct)
    {
        var early = await db.MessageEvents
            .Where(e => !e.Applied && e.ProviderMessageId == message.ProviderMessageId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        foreach (var e in early)
        {
            DeliveryState.Apply(message, e.Status, e.OccurredAt, e.ErrorCode, now);
            e.MessageId = message.Id;
            e.Applied = true;
        }
    }

    private static void Finish(Message m, MessageStatus status, DateTime now, string? error)
    {
        m.Status = status;
        m.PayloadEncrypted = null;
        m.LockedUntil = null;
        m.LastError = Trim(error);
        m.UpdatedAt = now;
        if (status == MessageStatus.Failed) m.FailedAt = now;
    }

    /// <summary>Exponential with full jitter, capped — so a provider outage
    /// does not turn into every queued message retrying in lockstep.</summary>
    public TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(_options.MaxBackoffSeconds, _options.BaseBackoffSeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        var seconds = ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2;
        return TimeSpan.FromSeconds(seconds);
    }

    private static string? Trim(string? s) => s is null ? null : s.Length <= 300 ? s : s[..300];
}
