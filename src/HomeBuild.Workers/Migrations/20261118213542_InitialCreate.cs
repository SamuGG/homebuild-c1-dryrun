using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Workers.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contracts");

            migrationBuilder.CreateTable(
                name: "contracts",
                schema: "contracts",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contracts", x => x.reservation_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "contracts",
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

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "contracts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contracts_tenant_id",
                schema: "contracts",
                table: "contracts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                schema: "contracts",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "published_at IS NULL");

            // The app role (C4) reads and writes the worker's tables; it does not own them. No row-level security:
            // the worker processes every tenant's messages and keeps the tenant in each row.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'homebuild_app') THEN CREATE ROLE homebuild_app NOLOGIN; END IF;
                END $$;
                GRANT USAGE ON SCHEMA contracts TO homebuild_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA contracts TO homebuild_app;
                REVOKE ALL ON contracts.__ef_migrations_history FROM homebuild_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA contracts GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO homebuild_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA contracts REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM homebuild_app; REVOKE ALL ON ALL TABLES IN SCHEMA contracts FROM homebuild_app; REVOKE USAGE ON SCHEMA contracts FROM homebuild_app;");

            migrationBuilder.DropTable(
                name: "contracts",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "contracts");
        }
    }
}
