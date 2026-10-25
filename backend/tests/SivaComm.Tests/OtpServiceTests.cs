using Microsoft.EntityFrameworkCore;
using SivaComm.Data;
using SivaComm.Data.Services;

namespace SivaComm.Tests;

public sealed class OtpServiceTests : IAsyncLifetime
{
    private readonly Harness h = new();
    public Task InitializeAsync() => h.InitializeAsync();
    public Task DisposeAsync() => h.DisposeAsync();

    private const string Phone = "98765 43210";

    private async Task<(CallerContext App, Guid ChallengeId, string Otp)> NewChallengeAsync(
        string reset = "reset-0001", string user = "user-0001", CallerContext? app = null)
    {
        app ??= await h.AppAsync("hms-web");
        var r = await h.OtpService.CreateAsync(app, reset, user, Phone, default);
        Assert.Equal(Outcome.Queued, r.Outcome);
        return (app, r.ChallengeId!.Value, await h.QueuedOtpAsync(r.ChallengeId.Value));
    }

    [Fact]
    public async Task Correct_otp_verifies_and_issues_a_grant()
    {
        var (app, id, otp) = await NewChallengeAsync();

        var r = await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default);

        Assert.Equal(Outcome.Verified, r.Outcome);
        Assert.False(string.IsNullOrEmpty(r.Grant));
        Assert.Equal(h.Time.GetUtcNow().UtcDateTime.AddMinutes(10), r.GrantExpiresAt);
    }

    [Fact]
    public async Task Otp_and_grant_are_never_stored_in_plaintext()
    {
        var (app, id, otp) = await NewChallengeAsync();
        var r = await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default);

        await using var db = h.Db.Context();
        var challenge = await db.OtpChallenges.SingleAsync();
        var grant = await db.VerificationGrants.SingleAsync();

        Assert.Equal(32, challenge.OtpHmac.Length);
        Assert.DoesNotContain(otp, Convert.ToHexString(challenge.OtpHmac));
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(r.Grant!), grant.GrantHash);
        Assert.Equal("91••••••3210", challenge.RecipientMasked);
    }

    [Fact]
    public async Task Wrong_otp_reports_remaining_attempts_and_issues_no_grant()
    {
        var (app, id, otp) = await NewChallengeAsync();

        var r = await h.OtpService.VerifyAsync(app, id, "reset-0001", Harness.WrongCode(otp), default);

        Assert.Equal(Outcome.InvalidOtp, r.Outcome);
        Assert.Equal(4, r.RemainingAttempts);
        Assert.Null(r.Grant);
        await using var db = h.Db.Context();
        Assert.Equal(0, await db.VerificationGrants.CountAsync());
    }

    [Fact]
    public async Task Malformed_otp_costs_an_attempt()
    {
        var (app, id, _) = await NewChallengeAsync();
        var r = await h.OtpService.VerifyAsync(app, id, "reset-0001", "12ab", default);
        Assert.Equal(Outcome.InvalidOtp, r.Outcome);
        Assert.Equal(4, r.RemainingAttempts);
    }

    [Fact]
    public async Task Fifth_wrong_attempt_locks_the_challenge_even_against_the_right_code()
    {
        var (app, id, otp) = await NewChallengeAsync();
        var wrong = Harness.WrongCode(otp);

        for (var i = 0; i < 4; i++)
            Assert.Equal(Outcome.InvalidOtp, (await h.OtpService.VerifyAsync(app, id, "reset-0001", wrong, default)).Outcome);

        var fifth = await h.OtpService.VerifyAsync(app, id, "reset-0001", wrong, default);
        Assert.Equal(Outcome.AttemptsExceeded, fifth.Outcome);
        Assert.Equal(0, fifth.RemainingAttempts);

        var right = await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default);
        Assert.Equal(Outcome.AttemptsExceeded, right.Outcome);
        Assert.Null(right.Grant);
    }

    [Fact]
    public async Task Expired_otp_is_reported_as_expired()
    {
        var (app, id, otp) = await NewChallengeAsync();
        h.Time.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));

        var r = await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default);

        Assert.Equal(Outcome.Expired, r.Outcome);
        Assert.Null(r.Grant);
    }

    [Fact]
    public async Task A_verified_challenge_cannot_be_verified_again()
    {
        var (app, id, otp) = await NewChallengeAsync();
        Assert.Equal(Outcome.Verified, (await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default)).Outcome);

        var again = await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default);
        Assert.Equal(Outcome.AlreadyUsed, again.Outcome);
        Assert.Null(again.Grant);
    }

    [Fact]
    public async Task Concurrent_correct_verifications_issue_exactly_one_grant()
    {
        var (app, id, otp) = await NewChallengeAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default))));

        Assert.Single(results, r => r.Outcome == Outcome.Verified);
        Assert.All(results.Where(r => r.Outcome != Outcome.Verified), r => Assert.Equal(Outcome.AlreadyUsed, r.Outcome));
        await using var db = h.Db.Context();
        Assert.Equal(1, await db.VerificationGrants.CountAsync());
    }

    [Fact]
    public async Task Concurrent_wrong_guesses_cannot_exceed_the_attempt_limit()
    {
        var (app, id, otp) = await NewChallengeAsync();
        var wrong = Harness.WrongCode(otp);

        var results = await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(_ => Task.Run(() => h.OtpService.VerifyAsync(app, id, "reset-0001", wrong, default))));

        Assert.Equal(4, results.Count(r => r.Outcome == Outcome.InvalidOtp));
        Assert.Equal(11, results.Count(r => r.Outcome == Outcome.AttemptsExceeded));
        await using var db = h.Db.Context();
        Assert.Equal(5, (await db.OtpChallenges.SingleAsync()).Attempts);
    }

    [Fact]
    public async Task A_challenge_is_bound_to_its_reset_reference()
    {
        var (app, id, otp) = await NewChallengeAsync();
        var r = await h.OtpService.VerifyAsync(app, id, "reset-OTHER", otp, default);
        Assert.Equal(Outcome.ChallengeNotFound, r.Outcome);
    }

    [Fact]
    public async Task Another_application_cannot_verify_or_even_find_the_challenge()
    {
        var (_, id, otp) = await NewChallengeAsync();
        var other = await h.AppAsync("swarnakshi");

        Assert.Equal(Outcome.ChallengeNotFound, (await h.OtpService.VerifyAsync(other, id, "reset-0001", otp, default)).Outcome);
        Assert.Equal(Outcome.ChallengeNotFound, (await h.OtpService.ResendAsync(other, id, "reset-0001", default)).Outcome);
        Assert.Equal(Outcome.ChallengeNotFound, (await h.OtpService.CancelAsync(other, id, "reset-0001", default)).Outcome);
    }

    [Fact]
    public async Task Create_is_idempotent_on_reset_reference_and_sends_once()
    {
        var (app, id, _) = await NewChallengeAsync();

        var retry = await h.OtpService.CreateAsync(app, "reset-0001", "user-0001", Phone, default);

        Assert.Equal(Outcome.Queued, retry.Outcome);
        Assert.Equal(id, retry.ChallengeId);
        await using var db = h.Db.Context();
        Assert.Equal(1, await db.Messages.CountAsync());
    }

    [Fact]
    public async Task Reusing_a_reset_reference_for_another_user_is_a_conflict()
    {
        var (app, _, _) = await NewChallengeAsync();
        var r = await h.OtpService.CreateAsync(app, "reset-0001", "user-9999", Phone, default);
        Assert.Equal(Outcome.Conflict, r.Outcome);
    }

    [Fact]
    public async Task A_new_challenge_supersedes_the_previous_one()
    {
        var (app, first, firstOtp) = await NewChallengeAsync("reset-0001");
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await NewChallengeAsync("reset-0002", app: app);

        var r = await h.OtpService.VerifyAsync(app, first, "reset-0001", firstOtp, default);
        Assert.Equal(Outcome.Cancelled, r.Outcome);
    }

    [Fact]
    public async Task Fourth_send_in_the_window_is_rate_limited()
    {
        var app = await h.AppAsync("hms-web");
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(Outcome.Queued, (await h.OtpService.CreateAsync(app, $"reset-000{i}", "user-0001", Phone, default)).Outcome);
            h.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var fourth = await h.OtpService.CreateAsync(app, "reset-0004", "user-0001", Phone, default);
        Assert.Equal(Outcome.RateLimited, fourth.Outcome);
        Assert.True(fourth.RetryAfter > TimeSpan.Zero);

        // Another user is unaffected, and the window does pass.
        Assert.Equal(Outcome.Queued, (await h.OtpService.CreateAsync(app, "reset-0005", "user-0002", Phone, default)).Outcome);
        h.Time.Advance(TimeSpan.FromMinutes(13));
        Assert.Equal(Outcome.Queued, (await h.OtpService.CreateAsync(app, "reset-0006", "user-0001", Phone, default)).Outcome);
    }

    [Fact]
    public async Task Resend_is_throttled_by_the_cooldown()
    {
        var (app, id, _) = await NewChallengeAsync();

        var tooSoon = await h.OtpService.ResendAsync(app, id, "reset-0001", default);
        Assert.Equal(Outcome.RateLimited, tooSoon.Outcome);

        h.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(Outcome.Queued, (await h.OtpService.ResendAsync(app, id, "reset-0001", default)).Outcome);
    }

    [Fact]
    public async Task Resend_replaces_the_code_and_does_not_reset_attempts()
    {
        var (app, id, oldOtp) = await NewChallengeAsync();
        await h.OtpService.VerifyAsync(app, id, "reset-0001", Harness.WrongCode(oldOtp), default);

        h.Time.Advance(TimeSpan.FromSeconds(61));
        await h.OtpService.ResendAsync(app, id, "reset-0001", default);
        var newOtp = await h.QueuedOtpAsync(id);

        if (newOtp != oldOtp)
        {
            var old = await h.OtpService.VerifyAsync(app, id, "reset-0001", oldOtp, default);
            Assert.Equal(Outcome.InvalidOtp, old.Outcome);
            Assert.Equal(3, old.RemainingAttempts);
        }

        Assert.Equal(Outcome.Verified, (await h.OtpService.VerifyAsync(app, id, "reset-0001", newOtp, default)).Outcome);

        // Only the newest message is still waiting to go; the old one was withdrawn.
        await using var db = h.Db.Context();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Status == MessageStatus.Queued));
    }

    [Fact]
    public async Task Invalid_phone_or_reference_is_refused_before_anything_is_stored()
    {
        var app = await h.AppAsync("hms-web");
        Assert.Equal(Outcome.InvalidRequest, (await h.OtpService.CreateAsync(app, "reset-0001", "user-0001", "12345", default)).Outcome);
        Assert.Equal(Outcome.InvalidRequest, (await h.OtpService.CreateAsync(app, "short", "user-0001", Phone, default)).Outcome);

        await using var db = h.Db.Context();
        Assert.Equal(0, await db.OtpChallenges.CountAsync());
    }
}
