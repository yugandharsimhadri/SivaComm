using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SivaComm.Data;
using SivaComm.Data.Providers;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

namespace SivaComm.Tests;

public sealed class DeliveryTests : IAsyncLifetime
{
    private readonly Harness h = new();
    public Task InitializeAsync() => h.InitializeAsync();
    public Task DisposeAsync() => h.DisposeAsync();

    private async Task<(CallerContext App, Guid ChallengeId, string Otp)> ChallengeAsync()
    {
        var app = await h.AppAsync("hms-web");
        var c = await h.OtpService.CreateAsync(app, "reset-0001", "user-0001", "9876543210", default);
        return (app, c.ChallengeId!.Value, await h.QueuedOtpAsync(c.ChallengeId.Value));
    }

    private async Task<Message> OnlyMessageAsync()
    {
        await using var db = h.Db.Context();
        return await db.Messages.SingleAsync();
    }

    [Fact]
    public async Task Accepted_message_is_submitted_with_its_provider_id_and_the_payload_is_wiped()
    {
        var (_, _, otp) = await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Accepted("wamid.ABC"));

        Assert.Equal(1, await h.Outbox.ProcessBatchAsync(default));

        var sent = Assert.Single(h.Provider.Sent);
        Assert.Equal("919876543210", sent.To);
        Assert.Equal("hms-web_otp", sent.TemplateName);
        Assert.Equal([otp], sent.Parameters);

        var m = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Submitted, m.Status);
        Assert.Equal("wamid.ABC", m.ProviderMessageId);
        Assert.Null(m.PayloadEncrypted);
    }

    [Fact]
    public async Task Transient_failure_is_retried_after_a_backoff_then_succeeds()
    {
        await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Transient("503"));

        await h.Outbox.ProcessBatchAsync(default);
        var waiting = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Queued, waiting.Status);
        Assert.True(waiting.NextAttemptAt > h.Time.GetUtcNow().UtcDateTime);

        // Not yet due: nothing is claimed.
        Assert.Equal(0, await h.Outbox.ProcessBatchAsync(default));

        h.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, await h.Outbox.ProcessBatchAsync(default));
        Assert.Equal(MessageStatus.Submitted, (await OnlyMessageAsync()).Status);
        Assert.Equal(2, h.Provider.Sent.Count);
    }

    [Fact]
    public async Task Retries_stop_at_the_attempt_limit_and_the_message_fails()
    {
        await ChallengeAsync();
        for (var i = 0; i < 5; i++) h.Provider.Script.Enqueue(ProviderResult.Transient("503"));

        // A minute is past any backoff here (5 s, then 10 s) and well inside
        // the OTP's 10-minute life — longer, and the worker would rightly
        // mark the message Expired instead of retrying it.
        for (var i = 0; i < 3; i++)
        {
            await h.Outbox.ProcessBatchAsync(default);
            h.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var m = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Failed, m.Status);
        Assert.Equal(3, m.Attempts);
        Assert.Null(m.PayloadEncrypted);
        Assert.Equal(0, await h.Outbox.ProcessBatchAsync(default));
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Permanent("400 (#132001) Template name does not exist"));

        await h.Outbox.ProcessBatchAsync(default);

        var m = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Failed, m.Status);
        Assert.Contains("132001", m.LastError);
        Assert.Single(h.Provider.Sent);
    }

    [Fact]
    public async Task An_otp_whose_challenge_ended_is_never_sent()
    {
        var (app, id, _) = await ChallengeAsync();
        await h.OtpService.CancelAsync(app, id, "reset-0001", default);

        await h.Outbox.ProcessBatchAsync(default);

        Assert.Empty(h.Provider.Sent);
        Assert.Equal(MessageStatus.Expired, (await OnlyMessageAsync()).Status);
    }

    [Fact]
    public async Task Delivery_and_verification_are_independent()
    {
        var (app, id, otp) = await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Accepted("wamid.D"));
        await h.Outbox.ProcessBatchAsync(default);
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.D", MessageStatus.Delivered, DateTime.UtcNow, null)], default);

        // Delivered, but the wrong code still fails...
        Assert.Equal(Outcome.InvalidOtp, (await h.OtpService.VerifyAsync(app, id, "reset-0001", Harness.WrongCode(otp), default)).Outcome);

        // ...and a delivery failure does not stop the right code working.
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.D", MessageStatus.Failed, DateTime.UtcNow, "131026")], default);
        Assert.Equal(Outcome.Verified, (await h.OtpService.VerifyAsync(app, id, "reset-0001", otp, default)).Outcome);
    }

    [Fact]
    public async Task Duplicate_webhook_events_are_applied_once()
    {
        await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Accepted("wamid.X"));
        await h.Outbox.ProcessBatchAsync(default);

        var update = new StatusUpdate("wamid.X", MessageStatus.Delivered, DateTime.UtcNow, null);
        Assert.Equal(1, await h.Webhooks.ApplyAsync([update], default));
        Assert.Equal(0, await h.Webhooks.ApplyAsync([update], default));

        await using var db = h.Db.Context();
        Assert.Equal(1, await db.MessageEvents.CountAsync());
    }

    [Fact]
    public async Task Out_of_order_events_never_move_status_backwards()
    {
        await ChallengeAsync();
        h.Provider.Script.Enqueue(ProviderResult.Accepted("wamid.Y"));
        await h.Outbox.ProcessBatchAsync(default);

        var t = DateTime.UtcNow;
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.Y", MessageStatus.Read, t.AddSeconds(3), null)], default);
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.Y", MessageStatus.Delivered, t.AddSeconds(2), null)], default);
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.Y", MessageStatus.Sent, t.AddSeconds(1), null)], default);
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.Y", MessageStatus.Failed, t.AddSeconds(4), "131047")], default);

        var m = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Read, m.Status);
        Assert.NotNull(m.SentAt);
        Assert.NotNull(m.DeliveredAt);
        Assert.Null(m.FailedAt);
    }

    [Fact]
    public async Task A_webhook_that_beats_the_provider_response_is_applied_when_the_id_is_recorded()
    {
        await ChallengeAsync();
        await h.Webhooks.ApplyAsync([new StatusUpdate("wamid.EARLY", MessageStatus.Delivered, DateTime.UtcNow, null)], default);

        h.Provider.Script.Enqueue(ProviderResult.Accepted("wamid.EARLY"));
        await h.Outbox.ProcessBatchAsync(default);

        var m = await OnlyMessageAsync();
        Assert.Equal(MessageStatus.Delivered, m.Status);
        await using var db = h.Db.Context();
        Assert.True((await db.MessageEvents.SingleAsync()).Applied);
    }

    [Fact]
    public async Task Welcome_is_idempotent_on_client_reference_and_checks_parameter_count()
    {
        var app = await h.AppAsync("hms-web");

        var first = await h.Messages.QueueWelcomeAsync(app, "welcome-user-0001", "9876543210", ["Sunrise Clinic", "drsri@sunrise"], default);
        var again = await h.Messages.QueueWelcomeAsync(app, "welcome-user-0001", "9876543210", ["Sunrise Clinic", "drsri@sunrise"], default);
        var wrong = await h.Messages.QueueWelcomeAsync(app, "welcome-user-0002", "9876543210", ["only one"], default);

        Assert.Equal(Outcome.Queued, first.Outcome);
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(Outcome.InvalidRequest, wrong.Outcome);
    }
}

/// <summary>Pure functions: no database needed.</summary>
public sealed class ParsingTests
{
    [Fact]
    public void Webhook_signature_is_checked_against_the_raw_body()
    {
        var body = Encoding.UTF8.GetBytes("""{"entry":[]}""");
        var good = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("app-secret"u8.ToArray(), body));

        Assert.True(WebhookService.SignatureIsValid(body, good, "app-secret"));
        Assert.False(WebhookService.SignatureIsValid(body, good, "other-secret"));
        Assert.False(WebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("""{"entry":[1]}"""), good, "app-secret"));
        Assert.False(WebhookService.SignatureIsValid(body, null, "app-secret"));
        Assert.False(WebhookService.SignatureIsValid(body, good, ""));
    }

    [Fact]
    public void Webhook_parser_reads_statuses_and_ignores_everything_else()
    {
        var json = """
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{
          "messages":[{"from":"91999","text":{"body":"hi"}}],
          "statuses":[
            {"id":"wamid.1","status":"delivered","timestamp":"1761300000","recipient_id":"91999"},
            {"id":"wamid.2","status":"failed","timestamp":"1761300001","errors":[{"code":131026,"title":"undeliverable"}]},
            {"id":"wamid.3","status":"something_new","timestamp":"1761300002"}]}}]}]}
        """;

        var updates = WebhookService.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(2, updates.Count);
        Assert.Equal(MessageStatus.Delivered, updates[0].Status);
        Assert.Equal("131026", updates[1].ErrorCode);
    }

    [Theory]
    [InlineData("+91 98765 43210", "91", "919876543210")]
    [InlineData("98765 43210", "91", "919876543210")]
    [InlineData("0091 98765 43210", "", "919876543210")]
    [InlineData("+44 7700 900123", "91", "447700900123")]
    public void Phone_numbers_are_normalised(string input, string cc, string expected)
    {
        Assert.True(PhoneNumbers.TryNormalise(input, cc, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("98765 43210", "")]
    [InlineData("12345", "91")]
    [InlineData("", "91")]
    public void Unusable_phone_numbers_are_refused_not_guessed(string input, string cc) =>
        Assert.False(PhoneNumbers.TryNormalise(input, cc, out _));
}
