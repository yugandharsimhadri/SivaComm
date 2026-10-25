using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SivaComm.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "comm");

            migrationBuilder.CreateTable(
                name: "admin_users",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    password_salt = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                    table.CheckConstraint("ck_applications_code", "code ~ '^[a-z0-9][a-z0-9-]{1,39}$'");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "message_events",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_key = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    error_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "request_nonces",
                schema: "comm",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_nonces", x => new { x.application_id, x.nonce });
                });

            migrationBuilder.CreateTable(
                name: "application_keys",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_keys_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "comm",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "application_templates",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    template_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    parameter_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_templates", x => x.id);
                    table.CheckConstraint("ck_application_templates_params", "parameter_count BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "fk_application_templates_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "comm",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    template_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recipient_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    recipient_masked = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    payload_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "comm",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "otp_challenges",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reset_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    otp_hmac = table.Column<byte[]>(type: "bytea", nullable: false),
                    recipient_masked = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    send_count = table.Column<int>(type: "integer", nullable: false),
                    last_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_otp_challenges", x => x.id);
                    table.CheckConstraint("ck_otp_challenges_attempts", "attempts >= 0 AND attempts <= max_attempts");
                    table.CheckConstraint("ck_otp_challenges_sends", "send_count >= 1");
                    table.ForeignKey(
                        name: "fk_otp_challenges_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "comm",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification_grants",
                schema: "comm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consume_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verification_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_verification_grants_otp_challenges_challenge_id",
                        column: x => x.challenge_id,
                        principalSchema: "comm",
                        principalTable: "otp_challenges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_users_username",
                schema: "comm",
                table: "admin_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_keys_application_id_key_id",
                schema: "comm",
                table: "application_keys",
                columns: new[] { "application_id", "key_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_templates_application_id_purpose",
                schema: "comm",
                table: "application_templates",
                columns: new[] { "application_id", "purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_applications_code",
                schema: "comm",
                table: "applications",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_application_id_created_at",
                schema: "comm",
                table: "audit_events",
                columns: new[] { "application_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_event_type_created_at",
                schema: "comm",
                table: "audit_events",
                columns: new[] { "event_type", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_message_events_event_key",
                schema: "comm",
                table: "message_events",
                column: "event_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_message_events_unapplied",
                schema: "comm",
                table: "message_events",
                column: "provider_message_id",
                filter: "NOT applied");

            migrationBuilder.CreateIndex(
                name: "ix_messages_application_id_client_reference",
                schema: "comm",
                table: "messages",
                columns: new[] { "application_id", "client_reference" },
                unique: true,
                filter: "client_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_application_id_created_at",
                schema: "comm",
                table: "messages",
                columns: new[] { "application_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_challenge_id",
                schema: "comm",
                table: "messages",
                column: "challenge_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_created_at",
                schema: "comm",
                table: "messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_messages_provider_message_id",
                schema: "comm",
                table: "messages",
                column: "provider_message_id",
                unique: true,
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_queue",
                schema: "comm",
                table: "messages",
                column: "next_attempt_at",
                filter: "status = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_otp_challenges_application_id_reset_reference",
                schema: "comm",
                table: "otp_challenges",
                columns: new[] { "application_id", "reset_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_otp_challenges_application_id_user_reference_created_at",
                schema: "comm",
                table: "otp_challenges",
                columns: new[] { "application_id", "user_reference", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_otp_challenges_created_at",
                schema: "comm",
                table: "otp_challenges",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_request_nonces_created_at",
                schema: "comm",
                table: "request_nonces",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_verification_grants_challenge_id",
                schema: "comm",
                table: "verification_grants",
                column: "challenge_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_verification_grants_grant_hash",
                schema: "comm",
                table: "verification_grants",
                column: "grant_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "application_keys",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "application_templates",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "message_events",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "request_nonces",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "verification_grants",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "otp_challenges",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "comm");
        }
    }
}
