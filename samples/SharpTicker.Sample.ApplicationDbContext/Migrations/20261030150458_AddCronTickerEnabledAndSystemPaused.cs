using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpTicker.Sample.ApplicationDbContext.Migrations
{
    /// <inheritdoc />
    public partial class AddCronTickerEnabledAndSystemPaused : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "ticker",
                table: "CronTickers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemPaused",
                schema: "ticker",
                table: "CronTickers",
                type: "INTEGER",
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
