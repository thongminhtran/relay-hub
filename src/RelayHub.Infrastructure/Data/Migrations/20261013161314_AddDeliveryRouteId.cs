using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryRouteId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RouteId",
                table: "Deliveries",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RouteId",
                table: "Deliveries");
        }
    }
}
