using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Catalogue.Migrations
{
    /// <inheritdoc />
    public partial class TenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "catalogue",
                table: "units",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "catalogue",
                table: "unit_prices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "catalogue",
                table: "developments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_units_tenant_id",
                schema: "catalogue",
                table: "units",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_unit_prices_tenant_id",
                schema: "catalogue",
                table: "unit_prices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_developments_tenant_id",
                schema: "catalogue",
                table: "developments",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_units_tenant_id",
                schema: "catalogue",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_unit_prices_tenant_id",
                schema: "catalogue",
                table: "unit_prices");

            migrationBuilder.DropIndex(
                name: "ix_developments_tenant_id",
                schema: "catalogue",
                table: "developments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "catalogue",
                table: "units");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "catalogue",
                table: "unit_prices");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "catalogue",
                table: "developments");
        }
    }
}
