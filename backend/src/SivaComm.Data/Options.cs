using Npgsql;

namespace SivaComm.Data;

/// <summary>
/// Same shape as HMS_WEB's DatabaseOptions on purpose: one key per thing an
/// operator changes, each also settable as an environment variable
/// (<c>Database__Password</c>), with <see cref="ConnectionString"/> as the
/// escape hatch for a managed host.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "sivacomm";
    public string Username { get; set; } = "sivacomm";
    public string Password { get; set; } = "";
    public string SslMode { get; set; } = "Prefer";
    public int MaxPoolSize { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public string ApplicationName { get; set; } = "SivaComm";

    /// <summary>Null: yes in Development, no otherwise (a release runs <c>--migrate</c>).</summary>
    public bool? MigrateOnStartup { get; set; }

    public string? ConnectionString { get; set; }

    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString.Trim();

        if (!Enum.TryParse<SslMode>(SslMode, ignoreCase: true, out var ssl))
            throw new InvalidOperationException($"Database:SslMode '{SslMode}' is not a valid Npgsql SslMode.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = Host.Trim(),
            Port = Port,
            Database = Name.Trim(),
            Username = Username.Trim(),
            Password = Password,
            SslMode = ssl,
            MaxPoolSize = MaxPoolSize,
            CommandTimeout = CommandTimeoutSeconds,
            ApplicationName = ApplicationName,
        }.ConnectionString;
    }
}

/// <summary>
/// Every OTP and grant rule as configuration, with the defaults HMS_WEB's
/// PASSWORD_RESET.md established. Changing one is a deliberate security
/// decision; the reasoning for each default is in docs/SECURITY.md.
/// </summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    public int Length { get; set; } = 6;
    public int ExpiryMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Codes sent (new challenges plus resends) per user per window.</summary>
    public int MaxSendsPerWindow { get; set; } = 3;
    public int SendWindowMinutes { get; set; } = 15;

    /// <summary>Minimum gap between two sends on the same challenge.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    public int GrantExpiryMinutes { get; set; } = 10;

    public void Validate()
    {
        if (Length is < 6 or > 9) throw new InvalidOperationException("Otp:Length must be 6–9.");
        if (ExpiryMinutes is < 1 or > 30) throw new InvalidOperationException("Otp:ExpiryMinutes must be 1–30.");
        if (MaxAttempts is < 1 or > 10) throw new InvalidOperationException("Otp:MaxAttempts must be 1–10.");
        if (MaxSendsPerWindow < 1) throw new InvalidOperationException("Otp:MaxSendsPerWindow must be at least 1.");
        if (GrantExpiryMinutes is < 1 or > 30) throw new InvalidOperationException("Otp:GrantExpiryMinutes must be 1–30.");
    }
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// 32 random bytes, base64. Encrypts application secrets, phone numbers
    /// and queued payloads, and keys the OTP HMAC. It must not live in the
    /// database: a database dump without it is a table of ciphertext and
    /// HMACs that cannot be brute-forced.
    /// </summary>
    public string MasterKey { get; set; } = "";

    /// <summary>Allowed difference between a request's timestamp and ours.</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    public const string DevelopmentPlaceholder = "REPLACE-BEFORE-PRODUCTION";
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Prepended to a bare national number. Empty means such numbers
    /// are refused rather than guessed at.</summary>
    public string DefaultCountryCode { get; set; } = "91";
}

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 10;
    public int MaxConcurrency { get; set; } = 4;
    public int MaxAttempts { get; set; } = 5;
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int BaseBackoffSeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 300;
    public int ProviderTimeoutSeconds { get; set; } = 10;

    /// <summary>Encrypted phone numbers on messages are wiped after this.</summary>
    public int RecipientRetentionDays { get; set; } = 30;

    /// <summary>Challenges, grants, events and audit rows are deleted after this.</summary>
    public int RecordRetentionDays { get; set; } = 180;
}
