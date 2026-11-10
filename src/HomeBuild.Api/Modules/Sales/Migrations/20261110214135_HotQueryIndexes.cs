using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Sales.Migrations
{
    /// <inheritdoc />
    public partial class HotQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_units_development_id_status",
                schema: "sales",
                table: "units",
                columns: new[] { "development_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_buyer_id_created_at",
                schema: "sales",
                table: "reservations",
                columns: new[] { "buyer_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_held_expires_at",
                schema: "sales",
                table: "reservations",
                column: "expires_at",
                filter: "status = 'held'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_units_development_id_status",
                schema: "sales",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_reservations_buyer_id_created_at",
                schema: "sales",
                table: "reservations");

            migrationBuilder.DropIndex(
                name: "ix_reservations_held_expires_at",
                schema: "sales",
                table: "reservations");
        }
    }
}
