using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfigJson",
                table: "Sources",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ExternalAccountId",
                table: "Sources",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Type_ExternalAccountId",
                table: "Sources",
                columns: new[] { "Type", "ExternalAccountId" },
                unique: true,
                filter: "[ExternalAccountId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sources_Type_ExternalAccountId",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "ConfigJson",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "ExternalAccountId",
                table: "Sources");
        }
    }
}
