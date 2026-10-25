using Microsoft.EntityFrameworkCore;

namespace SivaComm.Data.Services;

public sealed record AppSummary(
    Guid ApplicationId,
    string Code,
    string Name,
    int MessagesTotal,
    Dictionary<string, int> MessagesByStatus,
    int WelcomeMessages,
    int OtpMessages,
    int ChallengesCreated,
    int ChallengesVerified,
    int VerificationFailures,
    int ResetsCompleted,
    int DeliveryFailures);

public sealed record Summary(
    DateTime From,
    DateTime To,
    IReadOnlyList<AppSummary> Applications,
    int QueueBacklog,
    DateTime? OldestQueuedAt,
    DateTime? LastProviderSuccessAt,
    DateTime? LastProviderFailureAt,
    string? LastProviderError,
    DateTime? WorkerHeartbeatAt);

public sealed record MessageRow(
    Guid Id,
    string Application,
    MessagePurpose Purpose,
    MessageStatus Status,
    string Recipient,
    string Template,
    int Attempts,
    string? LastError,
    ChallengeStatus? ChallengeStatus,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? DeliveredAt,
    DateTime? ReadAt,
    DateTime? FailedAt);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

public sealed record ErrorRow(DateTime At, string Source, string? Application, string Detail);

/// <summary>
/// Read-only aggregates for the admin dashboard. Every number here is a
/// count: the dashboard never receives a phone number beyond its mask, an
/// OTP, a grant or a payload, because none of those are selected.
/// </summary>
public sealed class DashboardService(IDbContextFactory<CommDbContext> factory)
{
    public async Task<Summary> SummaryAsync(DateTime from, DateTime to, Guid? applicationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var apps = await db.Applications.AsNoTracking()
            .Where(a => applicationId == null || a.Id == applicationId)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Code, a.Name })
            .ToListAsync(ct);

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to && (applicationId == null || m.ApplicationId == applicationId))
            .GroupBy(m => new { m.ApplicationId, m.Purpose, m.Status })
            .Select(g => new { g.Key.ApplicationId, g.Key.Purpose, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var created = await CountByApp(db.OtpChallenges.Where(c => c.CreatedAt >= from && c.CreatedAt < to), c => c.ApplicationId, applicationId, ct);
        var verified = await CountByApp(db.OtpChallenges.Where(c => c.VerifiedAt >= from && c.VerifiedAt < to), c => c.ApplicationId, applicationId, ct);
        var completed = await CountByApp(db.OtpChallenges.Where(c => c.CompletedAt >= from && c.CompletedAt < to), c => c.ApplicationId, applicationId, ct);

        var failures = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EventType == AuditTypes.OtpVerifyFailed && a.CreatedAt >= from && a.CreatedAt < to && a.ApplicationId != null
                        && (applicationId == null || a.ApplicationId == applicationId))
            .GroupBy(a => a.ApplicationId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var summaries = apps.Select(a =>
        {
            var mine = messages.Where(m => m.ApplicationId == a.Id).ToList();
            return new AppSummary(
                a.Id, a.Code, a.Name,
                mine.Sum(m => m.Count),
                mine.GroupBy(m => m.Status.ToString()).ToDictionary(g => g.Key, g => g.Sum(m => m.Count)),
                mine.Where(m => m.Purpose == MessagePurpose.Welcome).Sum(m => m.Count),
                mine.Where(m => m.Purpose == MessagePurpose.PasswordResetOtp).Sum(m => m.Count),
                created.GetValueOrDefault(a.Id),
                verified.GetValueOrDefault(a.Id),
                failures.GetValueOrDefault(a.Id),
                completed.GetValueOrDefault(a.Id),
                mine.Where(m => m.Status == MessageStatus.Failed).Sum(m => m.Count));
        }).ToList();

        var queued = db.Messages.AsNoTracking().Where(m => m.Status == MessageStatus.Queued);
        var backlog = await queued.CountAsync(ct);
        var oldest = await queued.MinAsync(m => (DateTime?)m.CreatedAt, ct);

        var lastSuccess = await db.Messages.AsNoTracking().MaxAsync(m => m.SubmittedAt, ct);
        var lastFailure = await db.Messages.AsNoTracking()
            .Where(m => m.Status == MessageStatus.Failed)
            .OrderByDescending(m => m.FailedAt)
            .Select(m => new { m.FailedAt, m.LastError })
            .FirstOrDefaultAsync(ct);

        var heartbeat = OutboxWorker.LastHeartbeat == default ? (DateTime?)null : OutboxWorker.LastHeartbeat;

        return new Summary(from, to, summaries, backlog, oldest, lastSuccess, lastFailure?.FailedAt, lastFailure?.LastError, heartbeat);
    }

    public async Task<Page<MessageRow>> MessagesAsync(
        Guid? applicationId, MessageStatus? status, MessagePurpose? purpose, DateTime? from, DateTime? to,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Messages.AsNoTracking().AsQueryable();
        if (applicationId is not null) q = q.Where(m => m.ApplicationId == applicationId);
        if (status is not null) q = q.Where(m => m.Status == status);
        if (purpose is not null) q = q.Where(m => m.Purpose == purpose);
        if (from is not null) q = q.Where(m => m.CreatedAt >= from);
        if (to is not null) q = q.Where(m => m.CreatedAt < to);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MessageRow(
                m.Id,
                m.Application!.Code,
                m.Purpose,
                m.Status,
                m.RecipientMasked,
                m.TemplateName,
                m.Attempts,
                m.LastError,
                db.OtpChallenges.Where(c => c.Id == m.ChallengeId).Select(c => (ChallengeStatus?)c.Status).FirstOrDefault(),
                m.CreatedAt,
                m.SubmittedAt,
                m.DeliveredAt,
                m.ReadAt,
                m.FailedAt))
            .ToListAsync(ct);

        return new Page<MessageRow>(items, total, page, pageSize);
    }

    public async Task<List<ErrorRow>> RecentErrorsAsync(int take, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var failed = await db.Messages.AsNoTracking()
            .Where(m => m.Status == MessageStatus.Failed)
            .OrderByDescending(m => m.FailedAt)
            .Take(take)
            .Select(m => new ErrorRow(m.FailedAt ?? m.UpdatedAt, "delivery", m.Application!.Code, m.LastError ?? "failed"))
            .ToListAsync(ct);

        var auth = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EventType == AuditTypes.AuthFailed || a.EventType == AuditTypes.GrantRejected)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new ErrorRow(a.CreatedAt, a.EventType, null, a.Detail ?? ""))
            .ToListAsync(ct);

        return failed.Concat(auth).OrderByDescending(e => e.At).Take(take).ToList();
    }

    private static Task<Dictionary<Guid, int>> CountByApp<T>(
        IQueryable<T> source, System.Linq.Expressions.Expression<Func<T, Guid>> appId, Guid? only, CancellationToken ct)
        where T : class =>
        source.AsNoTracking()
            .GroupBy(appId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .Where(x => only == null || x.Id == only)
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
}
