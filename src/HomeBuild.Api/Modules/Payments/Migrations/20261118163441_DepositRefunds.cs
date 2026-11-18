using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class DepositRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "refund_reason",
                schema: "payments",
                table: "deposits",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "refund_reference",
                schema: "payments",
                table: "deposits",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refunded_at",
                schema: "payments",
                table: "deposits",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_reason",
                schema: "payments",
                table: "deposits");

            migrationBuilder.DropColumn(
                name: "refund_reference",
                schema: "payments",
                table: "deposits");

            migrationBuilder.DropColumn(
                name: "refunded_at",
                schema: "payments",
                table: "deposits");
        }
    }
}
