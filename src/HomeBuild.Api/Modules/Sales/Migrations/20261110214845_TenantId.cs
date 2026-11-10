using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Sales.Migrations
{
    /// <inheritdoc />
    public partial class TenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "sales",
                table: "units",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "sales",
                table: "reservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_units_tenant_id",
                schema: "sales",
                table: "units",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_tenant_id",
                schema: "sales",
                table: "reservations",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_units_tenant_id",
                schema: "sales",
                table: "units");

            migrationBuilder.DropIndex(
                name: "ix_reservations_tenant_id",
                schema: "sales",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "sales",
                table: "units");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "sales",
                table: "reservations");
        }
    }
}
