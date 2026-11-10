using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Catalogue.Migrations
{
    /// <inheritdoc />
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.EnableFor(migrationBuilder, "catalogue", "developments", "units", "unit_prices");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            HomeBuild.Api.Data.RowLevelSecurity.DisableFor(migrationBuilder, "catalogue", "developments", "units", "unit_prices");

        }
    }
}
