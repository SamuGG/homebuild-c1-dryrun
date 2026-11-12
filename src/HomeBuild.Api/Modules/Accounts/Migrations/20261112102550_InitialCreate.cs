using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Accounts.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounts");

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                schema: "accounts",
                columns: table => new
                {
                    subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_memberships", x => x.subject);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_memberships_tenant_id",
                schema: "accounts",
                table: "tenant_memberships",
                column: "tenant_id");

            // The API role reads memberships to find the tenant; it never writes them. No RLS: the lookup comes first.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'homebuild_app') THEN CREATE ROLE homebuild_app NOLOGIN; END IF;
                END $$;
                GRANT USAGE ON SCHEMA accounts TO homebuild_app;
                GRANT SELECT ON accounts.tenant_memberships TO homebuild_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_memberships",
                schema: "accounts");
        }
    }
}
