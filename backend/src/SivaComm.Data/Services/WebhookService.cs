using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SivaComm.Data.Services;

public sealed record StatusUpdate(string ProviderMessageId, MessageStatus Status, DateTime OccurredAt, string? ErrorCode);

/// <summary>
/// Delivery receipts from Meta. Updates the dashboard's view of a message and
/// nothing else — a "delivered" receipt says the phone has the code, not that
/// anyone typed it in, and OTP verification never looks here.
/// </summary>
public sealed class WebhookService(IDbContextFactory<CommDbContext> factory, TimeProvider time, ILogger<WebhookService> logger)
{
    /// <summary>X-Hub-Signature-256: "sha256=" + hex HMAC of the raw body with the app secret.</summary>
    public static bool SignatureIsValid(byte[] body, string? header, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal))
            return false;

        byte[] presented;
        try { presented = Convert.FromHexString(header["sha256=".Length..]); }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>Pulls the status entries out of a WhatsApp webhook body.
    /// Anything else in it (inbound messages, template events) is ignored.</summary>
    public static List<StatusUpdate> Parse(byte[] body)
    {
        var updates = new List<StatusUpdate>();
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return updates;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || !value.TryGetProperty("statuses", out var statuses)) continue;
                foreach (var s in statuses.EnumerateArray())
                {
                    var id = s.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var status = (s.TryGetProperty("status", out var st) ? st.GetString() : null) switch
                    {
                        "sent" => MessageStatus.Sent,
                        "delivered" => MessageStatus.Delivered,
                        "read" => MessageStatus.Read,
                        "failed" => MessageStatus.Failed,
                        _ => (MessageStatus?)null,
                    };
                    if (id is null || status is null) continue;

                    var at = s.TryGetProperty("timestamp", out var ts) && long.TryParse(ts.GetString(), out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                        : DateTime.UtcNow;

                    string? error = null;
                    if (s.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 &&
                        errors[0].TryGetProperty("code", out var code))
                        error = code.ToString();

                    updates.Add(new StatusUpdate(id, status.Value, at, error));
                }
            }
        }

        return updates;
    }

    /// <returns>How many events were new (duplicates are skipped).</returns>
    public async Task<int> ApplyAsync(IReadOnlyList<StatusUpdate> updates, CancellationToken ct)
    {
        var applied = 0;
        foreach (var u in updates)
        {
            if (await ApplyOneAsync(u, ct)) applied++;
        }
        return applied;
    }

    private async Task<bool> ApplyOneAsync(StatusUpdate u, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var key = $"{u.ProviderMessageId}:{u.Status}";
        if (key.Length > 240) key = key[..240];

        var message = await db.Messages
            .FromSql($"SELECT * FROM comm.messages WHERE provider_message_id = {u.ProviderMessageId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var evt = new MessageEvent
        {
            Id = Guid.CreateVersion7(),
            MessageId = message?.Id,
            EventKey = key,
            ProviderMessageId = u.ProviderMessageId,
            Status = u.Status,
            OccurredAt = u.OccurredAt,
            ReceivedAt = now,
            ErrorCode = u.ErrorCode,
            Applied = message is not null,
        };
        db.MessageEvents.Add(evt);

        if (message is not null) DeliveryState.Apply(message, u.Status, u.OccurredAt, u.ErrorCode, now);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Meta redelivers webhooks it is unsure we received. Seen before,
            // already applied; nothing to do.
            logger.LogDebug("Duplicate webhook event {EventKey} ignored.", key);
            return false;
        }
    }
}
