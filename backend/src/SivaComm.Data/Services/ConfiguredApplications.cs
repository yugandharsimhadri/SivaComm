using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

/// <summary>
/// One consuming application as written in the <c>Applications</c> section
/// of SivaComm's configuration file:
///
/// <code>
/// "Applications": [
///   {
///     "Code": "hms-web",
///     "Name": "Sivayaan HMS",
///     "Enabled": true,
///     "Keys": [ { "KeyId": "hms-2026-10", "Secret": "..." } ],
///     "Templates": {
///       "PasswordResetOtp": { "Name": "sivayaan_otp", "Language": "en" },
///       "Welcome":          { "Name": "hms_welcome", "Language": "en", "Parameters": 2 }
///     }
///   }
/// ]
/// </code>
///
/// The same KeyId and Secret go into the application's own config file
/// (HMS_WEB: the SivaComm section), so a deployment is two files and no
/// clicking.
/// </summary>
public sealed class ConfiguredApplication
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<ConfiguredKey> Keys { get; set; } = [];
    public ConfiguredTemplates Templates { get; set; } = new();
}

public sealed class ConfiguredKey
{
    public string KeyId { get; set; } = "";

    /// <summary>At least 32 characters of randomness. The application signs
    /// with exactly this value.</summary>
    public string Secret { get; set; } = "";

    /// <summary>True stops the key working without deleting it from the file,
    /// which is the second half of a rotation.</summary>
    public bool Revoked { get; set; }
}

public sealed class ConfiguredTemplates
{
    public ConfiguredTemplate? PasswordResetOtp { get; set; }
    public ConfiguredTemplate? Welcome { get; set; }
}

public sealed class ConfiguredTemplate
{
    /// <summary>The template name exactly as approved in WhatsApp Manager.</summary>
    public string Name { get; set; } = "";

    /// <summary>Exactly as approved: "en" and "en_US" are different templates to Meta.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Body placeholders. Ignored for PasswordResetOtp, which always has one (the code).</summary>
    public int Parameters { get; set; }
}

/// <summary>
/// Makes the database match the <c>Applications</c> section at startup.
///
/// The file is the source of truth for every application it lists: name,
/// enabled, templates, and each listed key (added, its secret updated, or
/// revoked as the file says). Keys issued from the dashboard are left
/// alone, so the dashboard still works for an application the file does not
/// list — but anything the file does say wins at the next restart.
/// </summary>
public sealed class ConfiguredApplicationsSync(IDbContextFactory<CommDbContext> factory, Crypto crypto, TimeProvider time)
{
    private static readonly Regex CodePattern = new("^[a-z0-9][a-z0-9-]{1,39}$", RegexOptions.Compiled);
    private static readonly Regex KeyIdPattern = new("^[A-Za-z0-9._-]{1,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Every mistake in the section, worded for the person editing the file.
    /// Checked before anything is written, so a half-valid file changes nothing.
    /// </summary>
    public static List<string> Validate(IReadOnlyList<ConfiguredApplication> apps)
    {
        var errors = new List<string>();
        var codes = new HashSet<string>();

        for (var i = 0; i < apps.Count; i++)
        {
            var a = apps[i];
            var where = $"Applications[{i}] ({a.Code})";

            if (!CodePattern.IsMatch(a.Code ?? "")) errors.Add($"{where}: Code must be 2–40 lower-case letters, digits or hyphens.");
            else if (!codes.Add(a.Code!)) errors.Add($"{where}: Code is listed twice.");
            if (string.IsNullOrWhiteSpace(a.Name)) errors.Add($"{where}: Name is required.");

            var keyIds = new HashSet<string>();
            foreach (var k in a.Keys)
            {
                if (!KeyIdPattern.IsMatch(k.KeyId ?? "")) errors.Add($"{where}: KeyId '{k.KeyId}' must be 1–40 of A-Z a-z 0-9 . _ -.");
                else if (!keyIds.Add(k.KeyId!)) errors.Add($"{where}: KeyId '{k.KeyId}' is listed twice.");
                if ((k.Secret ?? "").Length < 32) errors.Add($"{where}: the Secret for key '{k.KeyId}' must be at least 32 characters.");
            }

            if (a.Enabled && !a.Keys.Any(k => !k.Revoked))
                errors.Add($"{where}: an enabled application needs at least one key that is not revoked.");

            Check(a.Templates.PasswordResetOtp, $"{where} Templates.PasswordResetOtp", 1, 1);
            Check(a.Templates.Welcome, $"{where} Templates.Welcome", 0, 10);
        }

        return errors;

        void Check(ConfiguredTemplate? t, string where, int min, int max)
        {
            if (t is null) return;
            if (string.IsNullOrWhiteSpace(t.Name)) errors.Add($"{where}: Name is required.");
            if (string.IsNullOrWhiteSpace(t.Language)) errors.Add($"{where}: Language is required.");
            if (min != max && t.Parameters is var p && (p < min || p > max)) errors.Add($"{where}: Parameters must be {min}–{max}.");
        }
    }

    /// <returns>A one-line summary per application, for the startup log.</returns>
    public async Task<List<string>> ApplyAsync(IReadOnlyList<ConfiguredApplication> apps, CancellationToken ct)
    {
        var errors = Validate(apps);
        if (errors.Count > 0)
            throw new InvalidOperationException("The Applications section is not valid:\n  " + string.Join("\n  ", errors));

        var now = time.GetUtcNow().UtcDateTime;
        var summary = new List<string>();

        await using var db = await factory.CreateDbContextAsync(ct);

        foreach (var c in apps)
        {
            var app = await db.Applications.Include(a => a.Keys).Include(a => a.Templates)
                .FirstOrDefaultAsync(a => a.Code == c.Code, ct);

            if (app is null)
            {
                app = new Application { Id = Guid.CreateVersion7(), Code = c.Code, CreatedAt = now };
                db.Applications.Add(app);
                db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.ApplicationRegistered, Detail = "from configuration", CreatedAt = now });
            }

            app.Name = c.Name.Trim();
            app.IsActive = c.Enabled;

            foreach (var k in c.Keys)
            {
                var key = app.Keys.FirstOrDefault(x => x.KeyId == k.KeyId);
                if (key is null)
                {
                    key = new ApplicationKey { Id = Guid.CreateVersion7(), ApplicationId = app.Id, KeyId = k.KeyId, SecretEncrypted = crypto.Encrypt(k.Secret), CreatedAt = now };
                    db.ApplicationKeys.Add(key);
                    db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.KeyIssued, Detail = $"{k.KeyId} from configuration", CreatedAt = now });
                }
                else if (!SameSecret(key, k.Secret))
                {
                    key.SecretEncrypted = crypto.Encrypt(k.Secret);
                    db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.KeyIssued, Detail = $"{k.KeyId} secret changed in configuration", CreatedAt = now });
                }

                if (k.Revoked && key.RevokedAt is null)
                {
                    key.RevokedAt = now;
                    db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.KeyRevoked, Detail = $"{k.KeyId} from configuration", CreatedAt = now });
                }
                else if (!k.Revoked)
                {
                    key.RevokedAt = null;
                }
            }

            SetTemplate(db, app, MessagePurpose.PasswordResetOtp, c.Templates.PasswordResetOtp, fixedParameters: 1);
            SetTemplate(db, app, MessagePurpose.Welcome, c.Templates.Welcome, fixedParameters: null);

            summary.Add($"{c.Code}: {(c.Enabled ? "enabled" : "disabled")}, {c.Keys.Count(k => !k.Revoked)} live key(s), " +
                        $"OTP template {c.Templates.PasswordResetOtp?.Name ?? "(none)"}, welcome template {c.Templates.Welcome?.Name ?? "(none)"}");
        }

        await db.SaveChangesAsync(ct);
        return summary;
    }

    private bool SameSecret(ApplicationKey key, string secret)
    {
        try { return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(crypto.Decrypt(key.SecretEncrypted)), System.Text.Encoding.UTF8.GetBytes(secret)); }
        catch (CryptographicException) { return false; } // master key changed: the file's value replaces it
    }

    private static void SetTemplate(CommDbContext db, Application app, MessagePurpose purpose, ConfiguredTemplate? t, int? fixedParameters)
    {
        if (t is null) return;

        var row = app.Templates.FirstOrDefault(x => x.Purpose == purpose);
        if (row is null)
        {
            row = new ApplicationTemplate { Id = Guid.CreateVersion7(), ApplicationId = app.Id, Purpose = purpose };
            db.ApplicationTemplates.Add(row);
        }

        row.TemplateName = t.Name.Trim();
        row.LanguageCode = t.Language.Trim();
        row.ParameterCount = fixedParameters ?? t.Parameters;
    }
}
