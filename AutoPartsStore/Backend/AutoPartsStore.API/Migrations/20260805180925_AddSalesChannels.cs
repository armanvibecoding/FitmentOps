using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AutoPartsStore.API.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RequestedEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesChannels", x => x.Id);
                    table.CheckConstraint("CK_SalesChannels_Mode", "[Mode] IN ('Disabled', 'Sandbox', 'Production')");
                });

            migrationBuilder.CreateTable(
                name: "ChannelListings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SalesChannelId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    ExternalListingId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DesiredPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DesiredStock = table.Column<int>(type: "int", nullable: false),
                    ObservedPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ObservedStock = table.Column<int>(type: "int", nullable: true),
                    DesiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelListings", x => x.Id);
                    table.CheckConstraint("CK_ChannelListings_Desired", "[DesiredPrice] > 0 AND [DesiredStock] >= 0");
                    table.CheckConstraint("CK_ChannelListings_Observed", "([ObservedPrice] IS NULL OR [ObservedPrice] > 0) AND ([ObservedStock] IS NULL OR [ObservedStock] >= 0)");
                    table.CheckConstraint("CK_ChannelListings_Status", "[Status] IN ('Blocked', 'Pending', 'Active', 'Error')");
                    table.ForeignKey(
                        name: "FK_ChannelListings_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelListings_SalesChannels_SalesChannelId",
                        column: x => x.SalesChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOrderLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SalesChannelId = table.Column<int>(type: "int", nullable: false),
                    ExternalOrderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOrderLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOrderLinks_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelOrderLinks_SalesChannels_SalesChannelId",
                        column: x => x.SalesChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelInboxEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SalesChannelId = table.Column<int>(type: "int", nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChannelOrderLinkId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelInboxEvents", x => x.Id);
                    table.CheckConstraint("CK_ChannelInboxEvents_Status", "[Status] IN ('Processed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_ChannelInboxEvents_ChannelOrderLinks_ChannelOrderLinkId",
                        column: x => x.ChannelOrderLinkId,
                        principalTable: "ChannelOrderLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelInboxEvents_SalesChannels_SalesChannelId",
                        column: x => x.SalesChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "SalesChannels",
                columns: new[] { "Id", "Code", "ConcurrencyToken", "CreatedAtUtc", "DisplayName", "Mode", "RequestedEnabled", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { 1, "Trendyol", new Guid("0d847fc5-94c8-4309-a14b-e8dd38cc8036"), new DateTime(2026, 8, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Trendyol", "Disabled", false, new DateTime(2026, 8, 5, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, "Hepsiburada", new Guid("e7b5fd9e-418d-46d2-9951-c12944850b7b"), new DateTime(2026, 8, 5, 0, 0, 0, 0, DateTimeKind.Utc), "Hepsiburada", "Disabled", false, new DateTime(2026, 8, 5, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelInboxEvents_ChannelOrderLinkId",
                table: "ChannelInboxEvents",
                column: "ChannelOrderLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelInboxEvents_SalesChannelId_ExternalEventId",
                table: "ChannelInboxEvents",
                columns: new[] { "SalesChannelId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelInboxEvents_Status_ReceivedAtUtc",
                table: "ChannelInboxEvents",
                columns: new[] { "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelListings_ProductId",
                table: "ChannelListings",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelListings_SalesChannelId_ExternalListingId",
                table: "ChannelListings",
                columns: new[] { "SalesChannelId", "ExternalListingId" },
                unique: true,
                filter: "[ExternalListingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelListings_SalesChannelId_ProductId",
                table: "ChannelListings",
                columns: new[] { "SalesChannelId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOrderLinks_OrderId",
                table: "ChannelOrderLinks",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOrderLinks_SalesChannelId_ExternalOrderId",
                table: "ChannelOrderLinks",
                columns: new[] { "SalesChannelId", "ExternalOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_Code",
                table: "SalesChannels",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelInboxEvents");

            migrationBuilder.DropTable(
                name: "ChannelListings");

            migrationBuilder.DropTable(
                name: "ChannelOrderLinks");

            migrationBuilder.DropTable(
                name: "SalesChannels");
        }
    }
}
