using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SivaComm.Data;
using SivaComm.Data.Providers;
using SivaComm.Data.Security;
using SivaComm.Data.Services;

namespace SivaComm.Tests;

/// <summary>
/// One throwaway PostgreSQL database per test class instance, built by
/// running the real migrations — so every test run also proves the
/// migrations apply. Real PostgreSQL rather than an in-memory provider
/// because the guarantees under test (row locks, SKIP LOCKED, unique
/// violations) are PostgreSQL's.
///
/// Server from SIVACOMM_TEST_PG (an Npgsql connection string with a role
/// allowed to CREATE DATABASE and no Database key), else the local
/// development role from docs/DEVELOPMENT.md.
/// </summary>
public sealed class TestDb : IAsyncLifetime
{
    public const string ServerVariable = "SIVACOMM_TEST_PG";
    private const string DefaultServer = "Host=localhost;Port=5432;Username=sivacomm;Password=sivacomm-dev";

    private readonly string _server = Environment.GetEnvironmentVariable(ServerVariable) ?? DefaultServer;
    public string Name { get; } = $"sivacomm_test_{Guid.NewGuid():N}";

    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(_server) { Database = Name, ApplicationName = "SivaComm-Tests", MaxPoolSize = 60 }.ConnectionString;

    public DbContextOptions<CommDbContext> Options =>
        new DbContextOptionsBuilder<CommDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options;

    public IDbContextFactory<CommDbContext> Factory => new Fixed(Options);

    public CommDbContext Context() => new(Options);

    public async Task InitializeAsync()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres", Pooling = false }.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // A failing test should report its own reason, not a cleanup error.
        }
        return Task.CompletedTask;
    }

    private sealed class Fixed(DbContextOptions<CommDbContext> options) : IDbContextFactory<CommDbContext>
    {
        public CommDbContext CreateDbContext() => new(options);
    }
}

/// <summary>A provider that records what it was asked to send and answers
/// from a script, so retry and failure paths can be driven exactly.</summary>
public sealed class ScriptedProvider : IMessageProvider
{
    public List<ProviderMessage> Sent { get; } = [];
    public Queue<ProviderResult> Script { get; } = new();
    public string Name => "scripted";
    public MessageChannel Channel => MessageChannel.WhatsApp;

    public Task<ProviderResult> SendAsync(ProviderMessage message, CancellationToken ct)
    {
        lock (Sent) Sent.Add(message);
        var result = Script.Count > 0 ? Script.Dequeue() : ProviderResult.Accepted($"wamid.{Guid.NewGuid():N}");
        return Task.FromResult(result);
    }
}

/// <summary>Everything a service-level test needs, wired by hand.</summary>
public sealed class Harness : IAsyncLifetime
{
    public TestDb Db { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
    public OtpOptions Otp { get; } = new();
    public WorkerOptions Worker { get; } = new() { BaseBackoffSeconds = 5, MaxAttempts = 3 };
    public Crypto Crypto { get; } = new(Microsoft.Extensions.Options.Options.Create(new SecurityOptions { MasterKey = DevelopmentKeys.MasterKey }));
    public ScriptedProvider Provider { get; } = new();

    public OtpService OtpService => new(Db.Factory, Crypto, Time, new OutboxSignal(),
        Microsoft.Extensions.Options.Options.Create(Otp), Microsoft.Extensions.Options.Options.Create(new MessagingOptions()));

    public MessageService Messages => new(Db.Factory, Crypto, Time, new OutboxSignal(),
        Microsoft.Extensions.Options.Options.Create(new MessagingOptions()));

    public OutboxProcessor Outbox => new(Db.Factory, Provider, Crypto, Time,
        Microsoft.Extensions.Options.Options.Create(Worker), NullLogger<OutboxProcessor>.Instance);

    public WebhookService Webhooks => new(Db.Factory, Time, NullLogger<WebhookService>.Instance);

    public ApplicationService Applications => new(Db.Factory, Crypto, Time);

    public Task InitializeAsync() => Db.InitializeAsync();
    public Task DisposeAsync() => Db.DisposeAsync();

    /// <summary>Registers an application with both templates.</summary>
    public async Task<CallerContext> AppAsync(string code)
    {
        var (app, _) = (await Applications.RegisterAsync(code, code.ToUpperInvariant(), "test", default))!.Value;
        await Applications.SetTemplateAsync(app.Id, new TemplateView(MessagePurpose.PasswordResetOtp, $"{code}_otp", "en", 1), default);
        await Applications.SetTemplateAsync(app.Id, new TemplateView(MessagePurpose.Welcome, $"{code}_welcome", "en", 2), default);
        return new CallerContext(app.Id, code, "test-correlation");
    }

    /// <summary>The code queued for a challenge — read the way the worker
    /// reads it, by decrypting the outbox payload. Tests only.</summary>
    public async Task<string> QueuedOtpAsync(Guid challengeId)
    {
        await using var db = Db.Context();
        var m = await db.Messages
            .Where(x => x.ChallengeId == challengeId && x.Status == MessageStatus.Queued)
            .OrderByDescending(x => x.CreatedAt)
            .FirstAsync();
        return JsonSerializer.Deserialize<MessagePayload>(Crypto.Decrypt(m.PayloadEncrypted!))!.Parameters[0];
    }

    public static string WrongCode(string otp) => otp == "000000" ? "111111" : "000000";
}
