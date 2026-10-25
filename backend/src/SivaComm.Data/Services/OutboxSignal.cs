namespace SivaComm.Data.Services;

/// <summary>
/// Wakes the worker the moment something is queued, so an OTP goes out in
/// milliseconds rather than at the next poll. The poll still runs — the
/// signal is an optimisation, never the only way a message gets sent.
/// </summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        // At most one pending wake-up; a burst of enqueues needs only one.
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
