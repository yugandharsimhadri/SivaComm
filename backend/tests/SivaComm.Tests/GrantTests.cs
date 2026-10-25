using SivaComm.Data.Services;

namespace SivaComm.Tests;

public sealed class GrantTests : IAsyncLifetime
{
    private readonly Harness h = new();
    public Task InitializeAsync() => h.InitializeAsync();
    public Task DisposeAsync() => h.DisposeAsync();

    private async Task<(CallerContext App, Guid ChallengeId, string Grant)> VerifiedAsync()
    {
        var app = await h.AppAsync("hms-web");
        var c = await h.OtpService.CreateAsync(app, "reset-0001", "user-0001", "9876543210", default);
        var otp = await h.QueuedOtpAsync(c.ChallengeId!.Value);
        var v = await h.OtpService.VerifyAsync(app, c.ChallengeId.Value, "reset-0001", otp, default);
        Assert.Equal(Outcome.Verified, v.Outcome);
        return (app, c.ChallengeId.Value, v.Grant!);
    }

    [Fact]
    public async Task A_grant_is_consumed_once_and_returns_its_binding()
    {
        var (app, id, grant) = await VerifiedAsync();

        var r = await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);

        Assert.Equal(Outcome.Consumed, r.Outcome);
        Assert.Equal(id, r.ChallengeId);
        Assert.Equal("user-0001", r.UserReference);
        Assert.False(r.Replayed);
    }

    [Fact]
    public async Task A_replay_with_another_key_is_refused()
    {
        var (app, _, grant) = await VerifiedAsync();
        await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);

        var replay = await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-ATTACKER", default);

        Assert.Equal(Outcome.AlreadyUsed, replay.Outcome);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_after_a_lost_response_succeeds_again()
    {
        var (app, _, grant) = await VerifiedAsync();
        await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);

        var retry = await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);

        Assert.Equal(Outcome.Consumed, retry.Outcome);
        Assert.True(retry.Replayed);
    }

    [Fact]
    public async Task Concurrent_consumption_succeeds_exactly_once()
    {
        var (app, _, grant) = await VerifiedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => h.OtpService.ConsumeAsync(app, grant, "reset-0001", $"idem-{i:0000}", default))));

        Assert.Single(results, r => r.Outcome == Outcome.Consumed);
        Assert.All(results.Where(r => r.Outcome != Outcome.Consumed), r => Assert.Equal(Outcome.AlreadyUsed, r.Outcome));
    }

    [Fact]
    public async Task An_expired_grant_is_refused()
    {
        var (app, _, grant) = await VerifiedAsync();
        h.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(Outcome.Expired, (await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default)).Outcome);
    }

    [Fact]
    public async Task A_grant_is_bound_to_its_reset_reference_and_application()
    {
        var (app, _, grant) = await VerifiedAsync();
        var other = await h.AppAsync("transtrack");

        Assert.Equal(Outcome.GrantNotFound, (await h.OtpService.ConsumeAsync(app, grant, "reset-OTHER", "idem-0001", default)).Outcome);
        Assert.Equal(Outcome.GrantNotFound, (await h.OtpService.ConsumeAsync(other, grant, "reset-0001", "idem-0001", default)).Outcome);
        Assert.Equal(Outcome.GrantNotFound, (await h.OtpService.ConsumeAsync(app, "made-up-grant", "reset-0001", "idem-0001", default)).Outcome);

        // None of those spent it.
        Assert.Equal(Outcome.Consumed, (await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default)).Outcome);
    }

    [Fact]
    public async Task Cancelling_the_challenge_kills_an_unspent_grant()
    {
        var (app, id, grant) = await VerifiedAsync();
        await h.OtpService.CancelAsync(app, id, "reset-0001", default);

        var r = await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);
        Assert.NotEqual(Outcome.Consumed, r.Outcome);
    }

    [Fact]
    public async Task Completion_is_accepted_only_after_consumption_and_is_idempotent()
    {
        var (app, id, grant) = await VerifiedAsync();

        Assert.Equal(Outcome.Conflict, (await h.OtpService.CompleteAsync(app, id, "reset-0001", default)).Outcome);

        await h.OtpService.ConsumeAsync(app, grant, "reset-0001", "idem-0001", default);
        Assert.Equal(Outcome.Completed, (await h.OtpService.CompleteAsync(app, id, "reset-0001", default)).Outcome);
        Assert.Equal(Outcome.Completed, (await h.OtpService.CompleteAsync(app, id, "reset-0001", default)).Outcome);
    }
}
