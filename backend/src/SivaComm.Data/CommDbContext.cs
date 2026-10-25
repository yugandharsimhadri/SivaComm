using Microsoft.EntityFrameworkCore;

namespace SivaComm.Data;

/// <summary>
/// SivaComm's own database. Nothing here is shared with, or reaches into, a
/// consuming application's database.
///
/// Everything lives in the <c>comm</c> schema with snake_case names, so it
/// reads naturally in psql and never needs quoting.
/// </summary>
public class CommDbContext(DbContextOptions<CommDbContext> options) : DbContext(options)
{
    public const string Schema = "comm";

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ApplicationKey> ApplicationKeys => Set<ApplicationKey>();
    public DbSet<ApplicationTemplate> ApplicationTemplates => Set<ApplicationTemplate>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<VerificationGrant> VerificationGrants => Set<VerificationGrant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<RequestNonce> RequestNonces => Set<RequestNonce>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<ChallengeStatus>().HaveConversion<string>().HaveMaxLength(24);
        builder.Properties<MessageStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<MessagePurpose>().HaveConversion<string>().HaveMaxLength(24);
        builder.Properties<MessageChannel>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Application>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => x.Code).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_applications_code", "code ~ '^[a-z0-9][a-z0-9-]{1,39}$'"));
        });

        b.Entity<ApplicationKey>(e =>
        {
            e.Property(x => x.KeyId).HasMaxLength(40);
            e.HasIndex(x => new { x.ApplicationId, x.KeyId }).IsUnique();
            e.HasOne(x => x.Application).WithMany(a => a.Keys).HasForeignKey(x => x.ApplicationId);
        });

        b.Entity<ApplicationTemplate>(e =>
        {
            e.Property(x => x.TemplateName).HasMaxLength(512);
            e.Property(x => x.LanguageCode).HasMaxLength(16);
            e.HasIndex(x => new { x.ApplicationId, x.Purpose }).IsUnique();
            e.HasOne(x => x.Application).WithMany(a => a.Templates).HasForeignKey(x => x.ApplicationId);
            e.ToTable(t => t.HasCheckConstraint("ck_application_templates_params", "parameter_count BETWEEN 0 AND 10"));
        });

        b.Entity<OtpChallenge>(e =>
        {
            e.Property(x => x.ResetReference).HasMaxLength(100);
            e.Property(x => x.UserReference).HasMaxLength(100);
            e.Property(x => x.RecipientMasked).HasMaxLength(24);
            e.HasIndex(x => new { x.ApplicationId, x.ResetReference }).IsUnique();

            // The send cap and the supersede step both look up "this user's
            // recent challenges" on every request.
            e.HasIndex(x => new { x.ApplicationId, x.UserReference, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_otp_challenges_attempts", "attempts >= 0 AND attempts <= max_attempts");
                t.HasCheckConstraint("ck_otp_challenges_sends", "send_count >= 1");
            });
        });

        b.Entity<VerificationGrant>(e =>
        {
            e.Property(x => x.ConsumeKey).HasMaxLength(100);
            e.HasIndex(x => x.GrantHash).IsUnique();

            // One grant per challenge, enforced by the database: two
            // concurrent correct verifications cannot both mint one.
            e.HasIndex(x => x.ChallengeId).IsUnique();
            e.HasOne(x => x.Challenge).WithMany().HasForeignKey(x => x.ChallengeId);
        });

        b.Entity<Message>(e =>
        {
            e.Property(x => x.ClientReference).HasMaxLength(100);
            e.Property(x => x.TemplateName).HasMaxLength(512);
            e.Property(x => x.LanguageCode).HasMaxLength(16);
            e.Property(x => x.RecipientMasked).HasMaxLength(24);
            e.Property(x => x.ProviderMessageId).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(300);

            e.HasIndex(x => new { x.ApplicationId, x.ClientReference }).IsUnique()
                .HasFilter("client_reference IS NOT NULL");
            e.HasIndex(x => x.ProviderMessageId).IsUnique().HasFilter("provider_message_id IS NOT NULL");

            // The worker's claim query, and only over rows still waiting.
            e.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'Queued'")
                .HasDatabaseName("ix_messages_queue");

            // Dashboard: per-application history newest first, and date ranges.
            e.HasIndex(x => new { x.ApplicationId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ChallengeId);
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId);
        });

        b.Entity<MessageEvent>(e =>
        {
            e.Property(x => x.EventKey).HasMaxLength(240);
            e.Property(x => x.ProviderMessageId).HasMaxLength(200);
            e.Property(x => x.ErrorCode).HasMaxLength(40);
            e.HasIndex(x => x.EventKey).IsUnique();
            e.HasIndex(x => x.ProviderMessageId).HasFilter("NOT applied").HasDatabaseName("ix_message_events_unapplied");
        });

        b.Entity<RequestNonce>(e =>
        {
            e.HasKey(x => new { x.ApplicationId, x.Nonce });
            e.Property(x => x.Nonce).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(300);
            e.HasIndex(x => new { x.EventType, x.CreatedAt });
            e.HasIndex(x => new { x.ApplicationId, x.CreatedAt });
        });

        b.Entity<AdminUser>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(60);
            e.HasIndex(x => x.Username).IsUnique();
        });
    }
}
