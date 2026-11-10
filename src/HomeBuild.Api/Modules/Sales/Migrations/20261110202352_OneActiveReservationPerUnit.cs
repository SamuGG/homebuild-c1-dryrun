using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Sales.Migrations
{
    /// <inheritdoc />
    public partial class OneActiveReservationPerUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reservations_unit_id",
                schema: "sales",
                table: "reservations");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "sales",
                table: "units",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "ux_reservations_one_active_per_unit",
                schema: "sales",
                table: "reservations",
                column: "unit_id",
                unique: true,
                filter: "status IN ('held', 'confirmed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_reservations_one_active_per_unit",
                schema: "sales",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "sales",
                table: "units");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_unit_id",
                schema: "sales",
                table: "reservations",
                column: "unit_id");
        }
    }
}
