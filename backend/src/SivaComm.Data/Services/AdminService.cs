using Microsoft.EntityFrameworkCore;
using SivaComm.Data.Security;

namespace SivaComm.Data.Services;

/// <summary>Dashboard operators. Not application callers — those sign requests.</summary>
public sealed class AdminService(IDbContextFactory<CommDbContext> factory, TimeProvider time)
{
    // Verified against when the username is unknown, so a wrong username
    // takes as long as a wrong password and timing does not reveal which.
    private static readonly (string Hash, string Salt) Decoy = PasswordHasher.Hash(Guid.NewGuid().ToString());

    public async Task<AdminUser?> SignInAsync(string username, string password, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username.Trim() && u.IsActive, ct);

        bool ok;
        if (user is null)
        {
            _ = PasswordHasher.Verify(password, Decoy.Hash, Decoy.Salt);
            ok = false;
        }
        else
        {
            ok = PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt);
        }

        db.AuditEvents.Add(new AuditEvent
        {
            EventType = ok ? AuditTypes.AdminLogin : AuditTypes.AdminLoginFailed,
            Detail = ok ? user!.Username : "bad credentials",
            CreatedAt = now,
        });

        if (ok) user!.LastLoginAt = now;
        await db.SaveChangesAsync(ct);
        return ok ? user : null;
    }

    /// <summary>
    /// Creates the first admin from configuration when the table is empty, and
    /// never afterwards — so the bootstrap password in config stops mattering
    /// once a real operator exists, and editing config cannot silently reset
    /// anyone's password.
    /// </summary>
    public async Task<bool> EnsureBootstrapAdminAsync(string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.AdminUsers.AnyAsync(ct)) return false;

        var (hash, salt) = PasswordHasher.Hash(password);
        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.CreateVersion7(),
            Username = username.Trim(),
            PasswordHash = hash,
            PasswordSalt = salt,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
