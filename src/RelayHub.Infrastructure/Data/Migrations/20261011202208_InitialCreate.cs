using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    ExternalThreadId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SenderHandle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastInboundAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastOutboundAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    ConversationId = table.Column<int>(type: "int", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SenderHandle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    DestinationId = table.Column<int>(type: "int", nullable: false),
                    Template = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Filter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspaceId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsDemo = table.Column<bool>(type: "bit", nullable: false),
                    RetryProfile = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    DestinationId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptNo = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempts_Deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "Deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SourceId_ExternalThreadId",
                table: "Conversations",
                columns: new[] { "SourceId", "ExternalThreadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_EventId",
                table: "Deliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Status_NextAttemptAt",
                table: "Deliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryId",
                table: "DeliveryAttempts",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SourceId_ExternalId",
                table: "Events",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_WorkspaceId_ReceivedAt",
                table: "Events",
                columns: new[] { "WorkspaceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sources_KeyId",
                table: "Sources",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_WorkspaceId_Name",
                table: "Sources",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "DeliveryAttempts");

            migrationBuilder.DropTable(
                name: "Destinations");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
