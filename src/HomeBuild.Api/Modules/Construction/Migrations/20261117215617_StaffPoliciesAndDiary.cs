using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Construction.Migrations
{
    /// <inheritdoc />
    public partial class StaffPoliciesAndDiary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "photo_keys",
                schema: "construction",
                table: "diary_entries",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "weather",
                schema: "construction",
                table: "diary_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "written_by",
                schema: "construction",
                table: "diary_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "milestone_audit",
                schema: "construction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    from = table.Column<DateOnly>(type: "date", nullable: true),
                    to = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_milestone_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "site_assignments",
                schema: "construction",
                columns: table => new
                {
                    site_manager = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_assignments", x => new { x.site_manager, x.development_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_milestone_audit_tenant_id",
                schema: "construction",
                table: "milestone_audit",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_milestone_audit_unit_id_at",
                schema: "construction",
                table: "milestone_audit",
                columns: new[] { "unit_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_site_assignments_tenant_id",
                schema: "construction",
                table: "site_assignments",
                column: "tenant_id");

            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "construction", "milestone_audit", "site_assignments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "construction", "milestone_audit", "site_assignments");

            migrationBuilder.DropTable(
                name: "milestone_audit",
                schema: "construction");

            migrationBuilder.DropTable(
                name: "site_assignments",
                schema: "construction");

            migrationBuilder.DropColumn(
                name: "photo_keys",
                schema: "construction",
                table: "diary_entries");

            migrationBuilder.DropColumn(
                name: "weather",
                schema: "construction",
                table: "diary_entries");

            migrationBuilder.DropColumn(
                name: "written_by",
                schema: "construction",
                table: "diary_entries");
        }
    }
}
