using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XLines.Data.Migrations
{
    /// <inheritdoc />
    public partial class IngestionRunJobKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_JobKey_StartedAt",
                table: "IngestionRuns",
                columns: new[] { "JobKey", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IngestionRuns_JobKey_StartedAt",
                table: "IngestionRuns");
        }
    }
}
