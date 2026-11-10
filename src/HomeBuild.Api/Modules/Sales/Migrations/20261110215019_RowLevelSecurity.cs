using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Sales.Migrations
{
    /// <inheritdoc />
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "sales", "units", "reservations");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "sales", "units", "reservations");

        }
    }
}
