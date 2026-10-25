using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SivaComm.Data;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

namespace SivaComm.Tests;

/// <summary>Signs every request the way a source application must.</summary>
public sealed class SigningHandler(string client, string keyId, string secret) : DelegatingHandler
{
    public string? FixedNonce { get; set; }
    public DateTimeOffset? FixedTime { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var ts = (FixedTime ?? DateTimeOffset.UtcNow).ToString("O");
        var nonce = FixedNonce ?? Guid.NewGuid().ToString("N");
        var canonical = RequestSigning.Canonical(request.Method.Method, request.RequestUri!.PathAndQuery, ts, nonce, body);

        request.Headers.Add(RequestSigning.ClientHeader, client);
        request.Headers.Add(RequestSigning.KeyIdHeader, keyId);
        request.Headers.Add(RequestSigning.TimestampHeader, ts);
        request.Headers.Add(RequestSigning.NonceHeader, nonce);
        request.Headers.Add(RequestSigning.SignatureHeader, RequestSigning.Sign(secret, canonical));
        return await base.SendAsync(request, ct);
    }
}

public sealed class ApiTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Database:ConnectionString", _db.ConnectionString);
            b.UseSetting("Database:MigrateOnStartup", "false");
            b.UseSetting("Worker:Enabled", "false");
            b.UseSetting("Admin:Username", "ops");
            b.UseSetting("Admin:Password", "ops-password-for-tests");
            b.UseSetting("WhatsApp:AppSecret", "meta-app-secret");
        });
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<(HttpClient Client, SigningHandler Signer, IssuedKey Key, Guid AppId)> AppClientAsync(string code)
    {
        var apps = _factory.Services.GetRequiredService<ApplicationService>();
        var (app, key) = (await apps.RegisterAsync(code, code, "test", default))!.Value;
        await apps.SetTemplateAsync(app.Id, new TemplateView(MessagePurpose.PasswordResetOtp, "otp", "en", 1), default);
        await apps.SetTemplateAsync(app.Id, new TemplateView(MessagePurpose.Welcome, "welcome", "en", 2), default);

        var signer = new SigningHandler(key.ClientId, key.KeyId, key.Secret);
        return (_factory.CreateDefaultClient(signer), signer, key, app.Id);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> QueuedOtpAsync(Guid challengeId)
    {
        await using var db = _db.Context();
        var crypto = _factory.Services.GetRequiredService<Crypto>();
        var m = db.Messages.Single(x => x.ChallengeId == challengeId && x.Status == MessageStatus.Queued);
        return JsonSerializer.Deserialize<MessagePayload>(crypto.Decrypt(m.PayloadEncrypted!))!.Parameters[0];
    }

    private static object Create(string reset = "reset-0001") =>
        new { resetReference = reset, userReference = "user-0001", phone = "9876543210" };

    [Fact]
    public async Task The_whole_reset_flow_over_http()
    {
        var (client, _, _, _) = await AppClientAsync("hms-web");

        var created = await client.PostAsJsonAsync("/api/v1/otp/challenges", Create());
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var c = await JsonAsync(created);
        Assert.Equal("QUEUED", c.GetProperty("status").GetString());
        Assert.False(c.TryGetProperty("otp", out _));
        var id = c.GetProperty("challengeId").GetGuid();
        var otp = await QueuedOtpAsync(id);

        var wrong = await client.PostAsJsonAsync($"/api/v1/otp/challenges/{id}/verify",
            new { resetReference = "reset-0001", otp = Harness.WrongCode(otp) });
        Assert.Equal((HttpStatusCode)422, wrong.StatusCode);
        var w = await JsonAsync(wrong);
        Assert.False(w.GetProperty("success").GetBoolean());
        Assert.Equal("INVALID_OTP", w.GetProperty("status").GetString());
        Assert.Equal(4, w.GetProperty("remainingAttempts").GetInt32());
        Assert.False(w.TryGetProperty("verificationGrant", out _));

        var right = await client.PostAsJsonAsync($"/api/v1/otp/challenges/{id}/verify", new { resetReference = "reset-0001", otp });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var v = await JsonAsync(right);
        Assert.Equal("VERIFIED", v.GetProperty("status").GetString());
        var grant = v.GetProperty("verificationGrant").GetString()!;

        using var consume = new HttpRequestMessage(HttpMethod.Post, "/api/v1/grants/consume")
        {
            Content = JsonContent.Create(new { grant, resetReference = "reset-0001" }),
        };
        consume.Headers.Add("Idempotency-Key", "reset-0001");
        var consumed = await client.SendAsync(consume);
        Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
        Assert.Equal("user-0001", (await JsonAsync(consumed)).GetProperty("userReference").GetString());

        var done = await client.PostAsJsonAsync($"/api/v1/otp/challenges/{id}/completion",
            new { resetReference = "reset-0001", outcome = "PASSWORD_CHANGED" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.False(string.IsNullOrEmpty(done.Headers.GetValues("X-Correlation-Id").Single()));
    }

    [Fact]
    public async Task A_password_field_is_rejected_rather_than_ignored()
    {
        var (client, _, _, _) = await AppClientAsync("hms-web");

        var r = await client.PostAsJsonAsync("/api/v1/otp/challenges",
            new { resetReference = "reset-0001", userReference = "user-0001", phone = "9876543210", newPassword = "hunter22" });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("INVALID_REQUEST", (await JsonAsync(r)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unsigned_requests_are_refused()
    {
        var r = await _factory.CreateClient().PostAsJsonAsync("/api/v1/otp/challenges", Create());
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        var (_, _, key, _) = await AppClientAsync("hms-web");
        var forged = _factory.CreateDefaultClient(new SigningHandler(key.ClientId, key.KeyId, "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);
    }

    [Fact]
    public async Task A_tampered_body_is_refused()
    {
        var (_, _, key, _) = await AppClientAsync("hms-web");

        // Sign one body, send another.
        var ts = DateTimeOffset.UtcNow.ToString("O");
        var nonce = Guid.NewGuid().ToString("N");
        var signed = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Create("reset-0001")));
        var sig = RequestSigning.Sign(key.Secret, RequestSigning.Canonical("POST", "/api/v1/otp/challenges", ts, nonce, signed));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/otp/challenges") { Content = JsonContent.Create(Create("reset-9999")) };
        req.Headers.Add(RequestSigning.ClientHeader, key.ClientId);
        req.Headers.Add(RequestSigning.KeyIdHeader, key.KeyId);
        req.Headers.Add(RequestSigning.TimestampHeader, ts);
        req.Headers.Add(RequestSigning.NonceHeader, nonce);
        req.Headers.Add(RequestSigning.SignatureHeader, sig);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task A_stale_timestamp_is_refused()
    {
        var (client, signer, _, _) = await AppClientAsync("hms-web");
        signer.FixedTime = DateTimeOffset.UtcNow.AddMinutes(-6);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);
    }

    [Fact]
    public async Task A_replayed_request_is_refused()
    {
        var (client, signer, _, _) = await AppClientAsync("hms-web");
        signer.FixedNonce = "nonce-replayed-0000000001";

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_while_the_rotated_one_works()
    {
        var (client, _, key, appId) = await AppClientAsync("hms-web");
        var apps = _factory.Services.GetRequiredService<ApplicationService>();
        var next = (await apps.IssueKeyAsync(appId, "test", default))!;
        await apps.RevokeKeyAsync(appId, key.KeyId, "test", default);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);

        var rotated = _factory.CreateDefaultClient(new SigningHandler(next.ClientId, next.KeyId, next.Secret));
        Assert.Equal(HttpStatusCode.Accepted, (await rotated.PostAsJsonAsync("/api/v1/otp/challenges", Create())).StatusCode);
    }

    [Fact]
    public async Task One_application_cannot_touch_another_applications_challenge()
    {
        var (hms, _, _, _) = await AppClientAsync("hms-web");
        var (other, _, _, _) = await AppClientAsync("swarnakshi");

        var id = (await JsonAsync(await hms.PostAsJsonAsync("/api/v1/otp/challenges", Create()))).GetProperty("challengeId").GetGuid();
        var otp = await QueuedOtpAsync(id);

        var r = await other.PostAsJsonAsync($"/api/v1/otp/challenges/{id}/verify", new { resetReference = "reset-0001", otp });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("CHALLENGE_NOT_FOUND", (await JsonAsync(r)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Consume_without_an_idempotency_key_is_refused()
    {
        var (client, _, _, _) = await AppClientAsync("hms-web");
        var r = await client.PostAsJsonAsync("/api/v1/grants/consume", new { grant = "x", resetReference = "reset-0001" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_need_an_operator_session_not_an_application_key()
    {
        var (client, _, _, _) = await AppClientAsync("hms-web");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/summary")).StatusCode);
    }

    [Fact]
    public async Task Admin_writes_need_the_csrf_header_and_reads_report_counts()
    {
        var (app, _, _, _) = await AppClientAsync("hms-web");
        await app.PostAsJsonAsync("/api/v1/otp/challenges", Create());

        var admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // No header: refused before credentials are even looked at.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync("/api/admin/auth/login", new { username = "ops", password = "ops-password-for-tests" })).StatusCode);

        admin.DefaultRequestHeaders.Add("X-SivaComm-Admin", "1");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await admin.PostAsJsonAsync("/api/admin/auth/login", new { username = "ops", password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync("/api/admin/auth/login", new { username = "ops", password = "ops-password-for-tests" })).StatusCode);

        var summary = await JsonAsync(await admin.GetAsync("/api/admin/summary"));
        var hms = summary.GetProperty("applications").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "hms-web");
        Assert.Equal(1, hms.GetProperty("challengesCreated").GetInt32());
        Assert.Equal(1, hms.GetProperty("otpMessages").GetInt32());
        Assert.Equal(1, summary.GetProperty("queueBacklog").GetInt32());

        var page = await admin.GetStringAsync("/api/admin/messages?pageSize=10");
        Assert.Contains("91••••••3210", JsonDocument.Parse(page).RootElement.GetRawText().Replace("\\u2022", "•"));
        Assert.DoesNotContain("9876543210", page);
    }

    [Fact]
    public async Task Webhook_requires_metas_signature()
    {
        var client = _factory.CreateClient();
        var body = """{"object":"whatsapp_business_account","entry":[]}""";

        using var unsigned = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/webhooks/whatsapp", unsigned)).StatusCode);

        using var signedReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/whatsapp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        signedReq.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("meta-app-secret"u8.ToArray(), Encoding.UTF8.GetBytes(body))));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(signedReq)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_answer()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
