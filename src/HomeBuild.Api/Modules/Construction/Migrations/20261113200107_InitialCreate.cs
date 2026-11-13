using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Construction.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "construction");

            migrationBuilder.CreateTable(
                name: "build_progress",
                schema: "construction",
                columns: table => new
                {
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    milestones = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_progress", x => x.unit_id);
                });

            migrationBuilder.CreateTable(
                name: "diary_entries",
                schema: "construction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    written_on = table.Column<DateOnly>(type: "date", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diary_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_build_progress_development_id",
                schema: "construction",
                table: "build_progress",
                column: "development_id");

            migrationBuilder.CreateIndex(
                name: "ix_build_progress_tenant_id",
                schema: "construction",
                table: "build_progress",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_diary_entries_tenant_id",
                schema: "construction",
                table: "diary_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_diary_entries_unit_id_written_on",
                schema: "construction",
                table: "diary_entries",
                columns: new[] { "unit_id", "written_on" });

            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "construction", "build_progress", "diary_entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "construction", "build_progress", "diary_entries");

            migrationBuilder.DropTable(
                name: "build_progress",
                schema: "construction");

            migrationBuilder.DropTable(
                name: "diary_entries",
                schema: "construction");
        }
    }
}
