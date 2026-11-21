using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Aftercare.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "aftercare");

            migrationBuilder.CreateTable(
                name: "handovers",
                schema: "aftercare",
                columns: table => new
                {
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    keys_handed_over = table.Column<bool>(type: "boolean", nullable: false),
                    meter_readings = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    warranties_issued = table.Column<bool>(type: "boolean", nullable: false),
                    completed_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    defects_end_on = table.Column<DateOnly>(type: "date", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handovers", x => x.unit_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "aftercare",
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
                name: "snags",
                schema: "aftercare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    before_handover = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "snag_timeline",
                schema: "aftercare",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    from = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    snag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snag_timeline", x => x.id);
                    table.ForeignKey(
                        name: "fk_snag_timeline_snags_snag_id",
                        column: x => x.snag_id,
                        principalSchema: "aftercare",
                        principalTable: "snags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_handovers_tenant_id",
                schema: "aftercare",
                table: "handovers",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                schema: "aftercare",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_snag_timeline_snag_id",
                schema: "aftercare",
                table: "snag_timeline",
                column: "snag_id");

            migrationBuilder.CreateIndex(
                name: "ix_snags_tenant_id",
                schema: "aftercare",
                table: "snags",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_snags_unit_id",
                schema: "aftercare",
                table: "snags",
                column: "unit_id");

            // Row-level security on the tenant tables (C4); timeline rows are reached only through their snag.
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "aftercare", "handovers", "snags");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "aftercare", "handovers", "snags");

            migrationBuilder.DropTable(
                name: "handovers",
                schema: "aftercare");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "aftercare");

            migrationBuilder.DropTable(
                name: "snag_timeline",
                schema: "aftercare");

            migrationBuilder.DropTable(
                name: "snags",
                schema: "aftercare");
        }
    }
}
