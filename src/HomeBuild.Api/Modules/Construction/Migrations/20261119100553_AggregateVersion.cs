using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeBuild.Api.Modules.Construction.Migrations
{
    /// <inheritdoc />
    public partial class AggregateVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "construction",
                table: "build_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing units start at their number of completed milestones (each completion was one change), so the
            // next event's version is above any a consumer could have seen. Notes and corrections before this were not
            // counted; versions only need to grow, not to count every change.
            migrationBuilder.Sql("""
                UPDATE construction.build_progress
                SET version = (SELECT count(*) FROM jsonb_array_elements(milestones) m WHERE m->>'Status' = 'Completed');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                schema: "construction",
                table: "build_progress");
        }
    }
}
