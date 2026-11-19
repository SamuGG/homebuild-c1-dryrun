using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Extras.Migrations
{
    /// <inheritdoc />
    public partial class Basket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "baskets",
                schema: "extras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    development_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checkout_attempts = table.Column<int>(type: "integer", nullable: false),
                    session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    payment_intent = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    charged_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_baskets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "basket_lines",
                schema: "extras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    basket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extra_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    refund_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    refund_reference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_basket_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_basket_lines_baskets_basket_id",
                        column: x => x.basket_id,
                        principalSchema: "extras",
                        principalTable: "baskets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_basket_lines_basket_id",
                schema: "extras",
                table: "basket_lines",
                column: "basket_id");

            migrationBuilder.CreateIndex(
                name: "ix_basket_lines_tenant_id",
                schema: "extras",
                table: "basket_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_baskets_buyer_id_unit_id",
                schema: "extras",
                table: "baskets",
                columns: new[] { "buyer_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_baskets_session_id",
                schema: "extras",
                table: "baskets",
                column: "session_id",
                unique: true,
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_baskets_tenant_id",
                schema: "extras",
                table: "baskets",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "basket_lines",
                schema: "extras");

            migrationBuilder.DropTable(
                name: "baskets",
                schema: "extras");
        }
    }
}
