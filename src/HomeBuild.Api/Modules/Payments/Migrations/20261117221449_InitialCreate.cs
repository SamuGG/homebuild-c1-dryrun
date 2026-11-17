using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "stage_payments_due",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    due_from = table.Column<DateOnly>(type: "date", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stage_payments_due", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stage_payments_due_tenant_id",
                schema: "payments",
                table: "stage_payments_due",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_stage_payments_due_unit_id_milestone",
                schema: "payments",
                table: "stage_payments_due",
                columns: new[] { "unit_id", "milestone" },
                unique: true);

            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "payments", "stage_payments_due");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "payments", "stage_payments_due");

            migrationBuilder.DropTable(
                name: "stage_payments_due",
                schema: "payments");
        }
    }
}
