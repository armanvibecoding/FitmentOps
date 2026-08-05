using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsStore.API.Migrations
{
    /// <inheritdoc />
    public partial class LinkReturnsToRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RefundId",
                table: "ReturnRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequests_RefundId",
                table: "ReturnRequests",
                column: "RefundId",
                unique: true,
                filter: "[RefundId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnRequests_Refunds_RefundId",
                table: "ReturnRequests",
                column: "RefundId",
                principalTable: "Refunds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReturnRequests_Refunds_RefundId",
                table: "ReturnRequests");

            migrationBuilder.DropIndex(
                name: "IX_ReturnRequests_RefundId",
                table: "ReturnRequests");

            migrationBuilder.DropColumn(
                name: "RefundId",
                table: "ReturnRequests");
        }
    }
}
