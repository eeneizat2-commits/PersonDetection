using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PersonDetection.Migrations
{
    /// <inheritdoc />
    public partial class UpdateVedioJobEntity3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AveragePersonsPerFrame",
                table: "VideoJobs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeakPersonCount",
                table: "VideoJobs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AveragePersonsPerFrame",
                table: "VideoJobs");

            migrationBuilder.DropColumn(
                name: "PeakPersonCount",
                table: "VideoJobs");
        }
    }
}
