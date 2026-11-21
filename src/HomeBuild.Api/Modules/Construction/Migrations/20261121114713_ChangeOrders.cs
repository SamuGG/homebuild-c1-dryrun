using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Construction.Migrations
{
    /// <inheritdoc />
    public partial class ChangeOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_orders",
                schema: "construction",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    basket_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extra_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extra_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    fit_by = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_orders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_orders_basket_line_id",
                schema: "construction",
                table: "change_orders",
                column: "basket_line_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_orders_development_id",
                schema: "construction",
                table: "change_orders",
                column: "development_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_orders_tenant_id",
                schema: "construction",
                table: "change_orders",
                column: "tenant_id");

            // Row-level security, as on build_progress (C4).
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "construction", "change_orders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON construction.change_orders;");

            migrationBuilder.DropTable(
                name: "change_orders",
                schema: "construction");
        }
    }
}
