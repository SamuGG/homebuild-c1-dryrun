using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class StagePaymentPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_at",
                schema: "payments",
                table: "stage_payments_due",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_reference",
                schema: "payments",
                table: "stage_payments_due",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "paid_at",
                schema: "payments",
                table: "stage_payments_due");

            migrationBuilder.DropColumn(
                name: "provider_reference",
                schema: "payments",
                table: "stage_payments_due");
        }
    }
}
