using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Extras.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "extras");

            migrationBuilder.CreateTable(
                name: "extras",
                schema: "extras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    unlocks_at = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    closes_at = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    conflict_group = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    max_quantity = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extras", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "extras",
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

            migrationBuilder.CreateTable(
                name: "unit_progress",
                schema: "extras",
                columns: table => new
                {
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latest_milestone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_progress", x => x.unit_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extras_development_id",
                schema: "extras",
                table: "extras",
                column: "development_id");

            migrationBuilder.CreateIndex(
                name: "ix_extras_tenant_id",
                schema: "extras",
                table: "extras",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                schema: "extras",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_unit_progress_tenant_id",
                schema: "extras",
                table: "unit_progress",
                column: "tenant_id");

            // Row-level security on the tenant tables (C4); the outbox has a tenant column but no RLS (the relay reads every tenant's rows).
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "extras", "extras", "unit_progress");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "extras", "extras", "unit_progress");

            migrationBuilder.DropTable(
                name: "extras",
                schema: "extras");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "extras");

            migrationBuilder.DropTable(
                name: "unit_progress",
                schema: "extras");
        }
    }
}
