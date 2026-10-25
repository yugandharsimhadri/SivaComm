namespace SivaComm.Data.Services;

/// <summary>
/// The one rule for moving a message's delivery status, used by both the
/// webhook and the worker's reconciliation.
///
/// Webhooks arrive out of order — "delivered" before "sent" is common — so
/// status only ever moves forward, while each event's own timestamp is still
/// recorded. A late "failed" after "delivered" is ignored: the phone has it.
/// </summary>
public static class DeliveryState
{
    private static int Rank(MessageStatus s) => s switch
    {
        MessageStatus.Queued => 0,
        MessageStatus.Submitted => 1,
        MessageStatus.Sent => 2,
        MessageStatus.Delivered => 3,
        MessageStatus.Read => 4,
        _ => -1,
    };

    /// <returns>Whether the event changed anything.</returns>
    public static bool Apply(Message m, MessageStatus status, DateTime occurredAt, string? error, DateTime now)
    {
        var changed = false;

        switch (status)
        {
            case MessageStatus.Sent when m.SentAt is null:
                m.SentAt = occurredAt; changed = true; break;
            case MessageStatus.Delivered when m.DeliveredAt is null:
                m.DeliveredAt = occurredAt; changed = true; break;
            case MessageStatus.Read when m.ReadAt is null:
                m.ReadAt = occurredAt; changed = true; break;
        }

        if (status == MessageStatus.Failed)
        {
            if (Rank(m.Status) is >= 0 and < 3)
            {
                m.Status = MessageStatus.Failed;
                m.FailedAt = occurredAt;
                m.LastError = error is null ? "failed (provider)" : $"provider {error}";
                changed = true;
            }
        }
        else if (Rank(status) > Rank(m.Status) && Rank(m.Status) >= 0)
        {
            m.Status = status;
            changed = true;
        }

        if (changed) m.UpdatedAt = now;
        return changed;
    }
}
