using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HomeBuild.Workers.Migrations.Notifications
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "aggregate_versions",
                schema: "notifications",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aggregate_versions", x => new { x.consumer, x.aggregate_id });
                });

            migrationBuilder.CreateTable(
                name: "buyer_notifications",
                schema: "notifications",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_buyer_notifications", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "notifications",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_buyer_notifications_message_id",
                schema: "notifications",
                table: "buyer_notifications",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_buyer_notifications_tenant_id_unit_id",
                schema: "notifications",
                table: "buyer_notifications",
                columns: new[] { "tenant_id", "unit_id" });

            // The app role (C4) reads and writes the worker's tables; it does not own them (as for contracts, Lab 3).
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'homebuild_app') THEN CREATE ROLE homebuild_app NOLOGIN; END IF;
                END $$;
                GRANT USAGE ON SCHEMA notifications TO homebuild_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA notifications TO homebuild_app;
                REVOKE ALL ON notifications.__ef_migrations_history FROM homebuild_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA notifications GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO homebuild_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA notifications REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM homebuild_app; REVOKE ALL ON ALL TABLES IN SCHEMA notifications FROM homebuild_app; REVOKE USAGE ON SCHEMA notifications FROM homebuild_app;");

            migrationBuilder.DropTable(
                name: "aggregate_versions",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "buyer_notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "notifications");
        }
    }
}
