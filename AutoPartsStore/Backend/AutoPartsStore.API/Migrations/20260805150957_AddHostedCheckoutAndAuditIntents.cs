using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsStore.API.Migrations
{
    /// <inheritdoc />
    public partial class AddHostedCheckoutAndAuditIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAuditIntents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    CorrelationIdSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuditIntents", x => x.Id);
                    table.CheckConstraint("CK_AdminAuditIntents_Lease", "([Status] = 'processing' AND [LeaseId] IS NOT NULL AND [LeaseExpiresAtUtc] IS NOT NULL) OR ([Status] <> 'processing' AND [LeaseId] IS NULL AND [LeaseExpiresAtUtc] IS NULL)");
                    table.CheckConstraint("CK_AdminAuditIntents_Outcome", "[Outcome] IN ('succeeded', 'rejected', 'failed', 'replayed')");
                    table.CheckConstraint("CK_AdminAuditIntents_PositiveIdentity", "[ActorUserId] > 0 AND [AggregateId] > 0 AND [AttemptCount] >= 0");
                    table.CheckConstraint("CK_AdminAuditIntents_Role", "[ActorRole] IN ('finance', 'warehouse', 'catalog', 'support', 'superadmin', 'Admin')");
                    table.CheckConstraint("CK_AdminAuditIntents_Status", "[Status] IN ('pending', 'processing', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "HostedCheckoutSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InventoryReservationId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedCheckoutSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostedCheckoutSessions_InventoryReservations_InventoryReservationId",
                        column: x => x.InventoryReservationId,
                        principalTable: "InventoryReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HostedCheckoutSessions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditIntents_OperationId",
                table: "AdminAuditIntents",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditIntents_Status_LeaseExpiresAtUtc",
                table: "AdminAuditIntents",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditIntents_Status_NextAttemptAtUtc",
                table: "AdminAuditIntents",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HostedCheckoutSessions_IdempotencyKey",
                table: "HostedCheckoutSessions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostedCheckoutSessions_InventoryReservationId",
                table: "HostedCheckoutSessions",
                column: "InventoryReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostedCheckoutSessions_OrderId",
                table: "HostedCheckoutSessions",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAuditIntents");

            migrationBuilder.DropTable(
                name: "HostedCheckoutSessions");
        }
    }
}
