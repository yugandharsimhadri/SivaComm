using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

public sealed record MessageView(
    Guid MessageId,
    MessagePurpose Purpose,
    MessageStatus Status,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime? FailedAt);

/// <summary>Non-OTP messages (welcome today) and per-application status lookups.</summary>
public sealed class MessageService(
    IDbContextFactory<CommDbContext> factory,
    Crypto crypto,
    TimeProvider time,
    OutboxSignal outbox,
    IOptions<MessagingOptions> messagingOptions)
{
    private readonly MessagingOptions _messaging = messagingOptions.Value;

    /// <summary>
    /// Queues a welcome. Idempotent on <paramref name="clientReference"/>: a
    /// retried request gets the first message back rather than a second
    /// WhatsApp message.
    /// </summary>
    public async Task<QueueMessageResult> QueueWelcomeAsync(
        CallerContext caller, string clientReference, string phone, IReadOnlyList<string> parameters, CancellationToken ct)
    {
        if (clientReference is not { Length: >= 8 and <= 100 })
            return new(Outcome.InvalidRequest, Error: "clientReference must be 8–100 characters.");

        if (parameters.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200 || p.Contains('\n')))
            return new(Outcome.InvalidRequest, Error: "Each parameter must be 1–200 characters on one line.");

        if (!PhoneNumbers.TryNormalise(phone, _messaging.DefaultCountryCode, out var normalised))
            return new(Outcome.InvalidRequest, Error: "phone is not a number with a usable country code.");

        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ApplicationId == caller.ApplicationId && m.ClientReference == clientReference, ct);
        if (existing is not null) return new(Outcome.Queued, existing.Id, existing.Status);

        var template = await db.ApplicationTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApplicationId == caller.ApplicationId && t.Purpose == MessagePurpose.Welcome, ct);
        if (template is null)
            return new(Outcome.InvalidRequest, Error: "No welcome template is configured for this application.");

        if (template.ParameterCount != parameters.Count)
            return new(Outcome.InvalidRequest, Error: $"The welcome template takes {template.ParameterCount} parameters.");

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = caller.ApplicationId,
            Purpose = MessagePurpose.Welcome,
            ClientReference = clientReference,
            TemplateName = template.TemplateName,
            LanguageCode = template.LanguageCode,
            RecipientEncrypted = crypto.Encrypt(normalised),
            RecipientMasked = PhoneNumbers.Mask(normalised),
            PayloadEncrypted = crypto.Encrypt(JsonSerializer.Serialize(new MessagePayload(parameters))),
            Status = MessageStatus.Queued,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Messages.Add(message);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Two concurrent retries of the same request: the unique index
            // let one in, and this one returns what that one queued.
            await using var fresh = await factory.CreateDbContextAsync(ct);
            var winner = await fresh.Messages.AsNoTracking()
                .FirstAsync(m => m.ApplicationId == caller.ApplicationId && m.ClientReference == clientReference, ct);
            return new(Outcome.Queued, winner.Id, winner.Status);
        }

        outbox.Notify();
        return new(Outcome.Queued, message.Id, message.Status);
    }

    /// <summary>Only the caller's own messages; another application's id is
    /// simply not found.</summary>
    public async Task<MessageView?> GetAsync(CallerContext caller, Guid messageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.ApplicationId == caller.ApplicationId)
            .Select(m => new MessageView(m.Id, m.Purpose, m.Status, m.CreatedAt, m.SubmittedAt, m.SentAt, m.DeliveredAt, m.ReadAt, m.FailedAt))
            .FirstOrDefaultAsync(ct);
    }
}
