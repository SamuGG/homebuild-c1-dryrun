using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Catalogue.Migrations
{
    /// <inheritdoc />
    public partial class UnitPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unit_prices",
                schema: "catalogue",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_prices", x => x.id);
                    table.ForeignKey(
                        name: "fk_unit_prices_developments_development_id",
                        column: x => x.development_id,
                        principalSchema: "catalogue",
                        principalTable: "developments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_unit_prices_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "catalogue",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_unit_prices_development_id",
                schema: "catalogue",
                table: "unit_prices",
                column: "development_id");

            migrationBuilder.CreateIndex(
                name: "ix_unit_prices_unit_id",
                schema: "catalogue",
                table: "unit_prices",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unit_prices",
                schema: "catalogue");
        }
    }
}
