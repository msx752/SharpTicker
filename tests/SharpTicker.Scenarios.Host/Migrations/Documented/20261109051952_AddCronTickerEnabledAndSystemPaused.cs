using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpTicker.Scenarios.Host.Migrations.Documented
{
    /// <inheritdoc />
    public partial class AddCronTickerEnabledAndSystemPaused : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Edited as MIGRATING.md says, so that existing crons stay enabled (EF generates false).
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "ticker",
                table: "CronTickers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemPaused",
                schema: "ticker",
                table: "CronTickers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "ticker",
                table: "CronTickers");

            migrationBuilder.DropColumn(
                name: "IsSystemPaused",
                schema: "ticker",
                table: "CronTickers");
        }
    }
}
