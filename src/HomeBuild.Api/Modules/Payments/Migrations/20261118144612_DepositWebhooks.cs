using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Payments.Migrations
{
    /// <inheritdoc />
    public partial class DepositWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deposits",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "expired_checkout_sessions",
                schema: "payments",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expired_checkout_sessions", x => x.session_id);
                });

            migrationBuilder.CreateTable(
                name: "processed_webhook_events",
                schema: "payments",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_webhook_events", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deposits_reservation_id",
                schema: "payments",
                table: "deposits",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_deposits_session_id",
                schema: "payments",
                table: "deposits",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deposits_tenant_id",
                schema: "payments",
                table: "deposits",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_expired_checkout_sessions_reservation_id",
                schema: "payments",
                table: "expired_checkout_sessions",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_expired_checkout_sessions_tenant_id",
                schema: "payments",
                table: "expired_checkout_sessions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_processed_webhook_events_tenant_id",
                schema: "payments",
                table: "processed_webhook_events",
                column: "tenant_id");

            // C8 Lab 2: tenant isolation for the webhook's tables, as for stage_payments_due.
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "payments", "processed_webhook_events", "deposits", "expired_checkout_sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "processed_webhook_events", "deposits", "expired_checkout_sessions" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON payments.{table};");
            }

            migrationBuilder.DropTable(
                name: "deposits",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "expired_checkout_sessions",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "processed_webhook_events",
                schema: "payments");
        }
    }
}
