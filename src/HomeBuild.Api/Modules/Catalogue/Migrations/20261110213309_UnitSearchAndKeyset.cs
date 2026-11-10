using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace HomeBuild.Api.Modules.Catalogue.Migrations
{
    /// <inheritdoc />
    public partial class UnitSearchAndKeyset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keyset paging index; EF Core 10 cannot model an index on a complex-type property (Price.Amount).
            migrationBuilder.Sql("CREATE INDEX ix_units_development_price_id ON catalogue.units (development_id, price_amount, id);");

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "catalogue",
                table: "units",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "catalogue",
                table: "units",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                defaultValue: "");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "catalogue",
                table: "units",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('english'::regconfig, coalesce(name, '') || ' ' || coalesce(description, ''))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_units_search_vector",
                schema: "catalogue",
                table: "units",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX catalogue.ix_units_development_price_id;");

            migrationBuilder.DropIndex(
                name: "ix_units_search_vector",
                schema: "catalogue",
                table: "units");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "catalogue",
                table: "units");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "catalogue",
                table: "units");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "catalogue",
                table: "units");
        }
    }
}
