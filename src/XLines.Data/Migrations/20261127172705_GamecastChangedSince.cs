using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XLines.Data.Migrations
{
    /// <inheritdoc />
    public partial class GamecastChangedSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_game_play_game_updated",
                table: "GamePlays",
                columns: new[] { "GameId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_game_drive_game_updated",
                table: "GameDrives",
                columns: new[] { "GameId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_game_play_game_updated",
                table: "GamePlays");

            migrationBuilder.DropIndex(
                name: "ix_game_drive_game_updated",
                table: "GameDrives");
        }
    }
}
