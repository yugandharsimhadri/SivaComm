using Microsoft.EntityFrameworkCore;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

public sealed record IssuedKey(string ClientId, string KeyId, string Secret);

public sealed record ApplicationView(
    Guid Id, string Code, string Name, bool IsActive, DateTime CreatedAt,
    IReadOnlyList<KeyView> Keys, IReadOnlyList<TemplateView> Templates);

public sealed record KeyView(string KeyId, DateTime CreatedAt, DateTime? RevokedAt, DateTime? LastUsedAt);

public sealed record TemplateView(MessagePurpose Purpose, string TemplateName, string LanguageCode, int ParameterCount);

/// <summary>Registering applications and managing their signing keys. Admin-only.</summary>
public sealed class ApplicationService(IDbContextFactory<CommDbContext> factory, Crypto crypto, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<List<ApplicationView>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var apps = await db.Applications.AsNoTracking()
            .Include(a => a.Keys).Include(a => a.Templates)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        return apps.Select(a => new ApplicationView(
            a.Id, a.Code, a.Name, a.IsActive, a.CreatedAt,
            a.Keys.OrderByDescending(k => k.CreatedAt).Select(k => new KeyView(k.KeyId, k.CreatedAt, k.RevokedAt, k.LastUsedAt)).ToList(),
            a.Templates.OrderBy(t => t.Purpose).Select(t => new TemplateView(t.Purpose, t.TemplateName, t.LanguageCode, t.ParameterCount)).ToList()))
            .ToList();
    }

    /// <summary>Registers an application and issues its first key. The secret
    /// is returned here and never again.</summary>
    public async Task<(Application App, IssuedKey Key)?> RegisterAsync(string code, string name, string adminUser, CancellationToken ct)
    {
        code = code.Trim().ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Applications.AnyAsync(a => a.Code == code, ct)) return null;

        var now = Now;
        var app = new Application { Id = Guid.CreateVersion7(), Code = code, Name = name.Trim(), CreatedAt = now };
        db.Applications.Add(app);

        var key = NewKey(db, app.Id, now);
        db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.ApplicationRegistered, Detail = $"by {adminUser}", CreatedAt = now });
        await db.SaveChangesAsync(ct);

        return (app, new IssuedKey(app.Code, key.KeyId, key.Secret));
    }

    /// <summary>A second live key for rotation. The old one keeps working
    /// until it is revoked, so the application can be redeployed in between.</summary>
    public async Task<IssuedKey?> IssueKeyAsync(Guid applicationId, string adminUser, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null) return null;

        var live = await db.ApplicationKeys.CountAsync(k => k.ApplicationId == applicationId && k.RevokedAt == null, ct);
        if (live >= 2) throw new InvalidOperationException("Two keys are already live. Revoke one before issuing another.");

        var now = Now;
        var key = NewKey(db, app.Id, now);
        db.AuditEvents.Add(new AuditEvent { ApplicationId = app.Id, EventType = AuditTypes.KeyIssued, Detail = $"{key.KeyId} by {adminUser}", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return new IssuedKey(app.Code, key.KeyId, key.Secret);
    }

    public async Task<bool> RevokeKeyAsync(Guid applicationId, string keyId, string adminUser, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ApplicationKeys.FirstOrDefaultAsync(k => k.ApplicationId == applicationId && k.KeyId == keyId && k.RevokedAt == null, ct);
        if (key is null) return false;

        var now = Now;
        key.RevokedAt = now;
        db.AuditEvents.Add(new AuditEvent { ApplicationId = applicationId, EventType = AuditTypes.KeyRevoked, Detail = $"{keyId} by {adminUser}", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetActiveAsync(Guid applicationId, bool active, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Applications.Where(a => a.Id == applicationId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, active), ct) == 1;
    }

    public async Task<bool> SetTemplateAsync(Guid applicationId, TemplateView template, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, ct)) return false;

        var row = await db.ApplicationTemplates.FirstOrDefaultAsync(t => t.ApplicationId == applicationId && t.Purpose == template.Purpose, ct);
        if (row is null)
        {
            row = new ApplicationTemplate { Id = Guid.CreateVersion7(), ApplicationId = applicationId, Purpose = template.Purpose };
            db.ApplicationTemplates.Add(row);
        }

        row.TemplateName = template.TemplateName.Trim();
        row.LanguageCode = template.LanguageCode.Trim();

        // An Authentication template always carries exactly the code.
        row.ParameterCount = template.Purpose == MessagePurpose.PasswordResetOtp ? 1 : template.ParameterCount;

        await db.SaveChangesAsync(ct);
        return true;
    }

    private (string KeyId, string Secret) NewKey(CommDbContext db, Guid applicationId, DateTime now)
    {
        var keyId = $"k{now:yyyyMMdd}{System.Security.Cryptography.RandomNumberGenerator.GetHexString(6, lowercase: true)}";
        var secret = Crypto.NewToken();
        db.ApplicationKeys.Add(new ApplicationKey
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = applicationId,
            KeyId = keyId,
            SecretEncrypted = crypto.Encrypt(secret),
            CreatedAt = now,
        });
        return (keyId, secret);
    }
}
