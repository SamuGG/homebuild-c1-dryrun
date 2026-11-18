using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Http.Idempotency.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "idempotency");

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "idempotency",
                columns: table => new
                {
                    caller = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    response_location = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.caller, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_created_at",
                schema: "idempotency",
                table: "idempotency_keys",
                column: "created_at");

            // The app role reads and writes the keys; no row-level security (keys are per caller, not per tenant).
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "idempotency");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "idempotency");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "idempotency");
        }
    }
}
