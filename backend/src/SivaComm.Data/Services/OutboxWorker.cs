using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SivaComm.Data.Services;

/// <summary>
/// Drives <see cref="OutboxProcessor"/> in the API process. Woken by
/// <see cref="OutboxSignal"/> when something is queued, otherwise polls.
/// Once an hour it also runs the retention sweep.
/// </summary>
public sealed class OutboxWorker(
    IServiceProvider services,
    OutboxSignal signal,
    TimeProvider time,
    IOptions<WorkerOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private DateTime _nextSweep = DateTime.MinValue;

    public static DateTime LastHeartbeat { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogWarning("The outbox worker is disabled (Worker:Enabled = false). Queued messages will not be sent.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            LastHeartbeat = time.GetUtcNow().UtcDateTime;
            var claimed = 0;

            try
            {
                using var scope = services.CreateScope();
                claimed = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(stoppingToken);

                if (LastHeartbeat >= _nextSweep)
                {
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().SweepAsync(stoppingToken);
                    _nextSweep = LastHeartbeat.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database down, most likely. Keep looping: the moment it is
                // back the queue drains, with no restart needed.
                logger.LogError(ex, "Outbox worker loop failed; retrying shortly.");
            }

            // A full batch means more may be waiting — go again at once.
            if (claimed >= _options.BatchSize) continue;

            try
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>
/// Keeps the tables small and the sensitive columns short-lived: nonces after
/// their replay window, encrypted phone numbers after a month, everything
/// else after the record retention period.
/// </summary>
public sealed class RetentionService(IDbContextFactory<CommDbContext> factory, TimeProvider time, IOptions<WorkerOptions> options)
{
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = options.Value;
        await using var db = await factory.CreateDbContextAsync(ct);

        await db.RequestNonces.Where(n => n.CreatedAt < now.AddMinutes(-30)).ExecuteDeleteAsync(ct);

        await db.Messages
            .Where(m => m.CreatedAt < now.AddDays(-o.RecipientRetentionDays) && m.RecipientEncrypted != null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RecipientEncrypted, (byte[]?)null), ct);

        var cutoff = now.AddDays(-o.RecordRetentionDays);
        await db.MessageEvents.Where(e => e.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.Messages.Where(m => m.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.VerificationGrants.Where(g => g.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.OtpChallenges.Where(c => c.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        await db.AuditEvents.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
