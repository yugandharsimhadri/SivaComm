using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SivaComm.Data;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

namespace SivaComm.Tests;

/// <summary>
/// Applications, keys and templates come from the config file. These pin that
/// the database follows the file — including a changed secret and a revoked
/// key — and that a bad file is refused with a reason before anything changes.
/// </summary>
public sealed class ConfiguredApplicationsTests : IAsyncLifetime
{
    private readonly Harness h = new();
    public Task InitializeAsync() => h.InitializeAsync();
    public Task DisposeAsync() => h.DisposeAsync();

    private const string SecretA = "secret-a-0123456789abcdefghijklmnopqrstuv";
    private const string SecretB = "secret-b-0123456789abcdefghijklmnopqrstuv";

    private ConfiguredApplicationsSync Sync => new(h.Db.Factory, h.Crypto, h.Time);

    private RequestAuthenticator Auth => new(h.Db.Factory, h.Crypto, h.Time, Options.Create(new SecurityOptions()));

    private static ConfiguredApplication Hms(params ConfiguredKey[] keys) => new()
    {
        Code = "hms-web",
        Name = "Sivayaan HMS",
        Keys = keys.ToList(),
        Templates = new ConfiguredTemplates
        {
            PasswordResetOtp = new ConfiguredTemplate { Name = "sivayaan_otp", Language = "en" },
            Welcome = new ConfiguredTemplate { Name = "hms_welcome", Language = "en", Parameters = 2 },
        },
    };

    private async Task<AuthFailure> SignAsync(string keyId, string secret)
    {
        var ts = h.Time.GetUtcNow().ToString("O");
        var nonce = Guid.NewGuid().ToString("N");
        var body = "{}"u8.ToArray();
        var sig = RequestSigning.Sign(secret, RequestSigning.Canonical("POST", "/api/v1/otp/challenges", ts, nonce, body));
        var (failure, _, _) = await Auth.AuthenticateAsync(
            new SignedRequest("hms-web", keyId, ts, nonce, sig, "POST", "/api/v1/otp/challenges", body), default);
        return failure;
    }

    [Fact]
    public async Task An_application_in_the_file_is_created_with_its_key_and_templates()
    {
        await Sync.ApplyAsync([Hms(new ConfiguredKey { KeyId = "hms-2026-10", Secret = SecretA })], default);

        Assert.Equal(AuthFailure.None, await SignAsync("hms-2026-10", SecretA));

        await using var db = h.Db.Context();
        var templates = await db.ApplicationTemplates.OrderBy(t => t.Purpose).ToListAsync();
        Assert.Equal(["sivayaan_otp", "hms_welcome"], templates.Select(t => t.TemplateName));
        Assert.Equal([1, 2], templates.Select(t => t.ParameterCount));
    }

    [Fact]
    public async Task Applying_the_same_file_twice_changes_nothing()
    {
        var file = new[] { Hms(new ConfiguredKey { KeyId = "k1", Secret = SecretA }) };
        await Sync.ApplyAsync(file, default);
        await Sync.ApplyAsync(file, default);

        await using var db = h.Db.Context();
        Assert.Equal(1, await db.Applications.CountAsync());
        Assert.Equal(1, await db.ApplicationKeys.CountAsync());
        Assert.Equal(2, await db.ApplicationTemplates.CountAsync());
    }

    [Fact]
    public async Task A_changed_secret_in_the_file_replaces_the_old_one()
    {
        await Sync.ApplyAsync([Hms(new ConfiguredKey { KeyId = "k1", Secret = SecretA })], default);
        await Sync.ApplyAsync([Hms(new ConfiguredKey { KeyId = "k1", Secret = SecretB })], default);

        Assert.Equal(AuthFailure.BadSignature, await SignAsync("k1", SecretA));
        Assert.Equal(AuthFailure.None, await SignAsync("k1", SecretB));
    }

    [Fact]
    public async Task Rotation_in_the_file_two_keys_then_the_old_one_revoked()
    {
        await Sync.ApplyAsync([Hms(
            new ConfiguredKey { KeyId = "old", Secret = SecretA },
            new ConfiguredKey { KeyId = "new", Secret = SecretB })], default);
        Assert.Equal(AuthFailure.None, await SignAsync("old", SecretA));
        Assert.Equal(AuthFailure.None, await SignAsync("new", SecretB));

        await Sync.ApplyAsync([Hms(
            new ConfiguredKey { KeyId = "old", Secret = SecretA, Revoked = true },
            new ConfiguredKey { KeyId = "new", Secret = SecretB })], default);
        Assert.Equal(AuthFailure.UnknownClient, await SignAsync("old", SecretA));
        Assert.Equal(AuthFailure.None, await SignAsync("new", SecretB));
    }

    [Fact]
    public async Task Disabled_in_the_file_means_every_call_is_refused()
    {
        var app = Hms(new ConfiguredKey { KeyId = "k1", Secret = SecretA });
        await Sync.ApplyAsync([app], default);
        app.Enabled = false;
        await Sync.ApplyAsync([app], default);

        Assert.Equal(AuthFailure.UnknownClient, await SignAsync("k1", SecretA));
    }

    [Fact]
    public async Task A_bad_file_is_refused_with_reasons_and_changes_nothing()
    {
        var bad = new[]
        {
            new ConfiguredApplication { Code = "HMS WEB", Name = "", Keys = [new ConfiguredKey { KeyId = "k1", Secret = "short" }] },
            Hms(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Sync.ApplyAsync(bad, default));

        Assert.Contains("Code must be", ex.Message);
        Assert.Contains("Name is required", ex.Message);
        Assert.Contains("at least 32 characters", ex.Message);
        Assert.Contains("needs at least one key", ex.Message);
        await using var db = h.Db.Context();
        Assert.Equal(0, await db.Applications.CountAsync());
    }
}
