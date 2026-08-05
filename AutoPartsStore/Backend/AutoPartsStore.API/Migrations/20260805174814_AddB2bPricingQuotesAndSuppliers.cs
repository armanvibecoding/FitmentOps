using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsStore.API.Migrations
{
    /// <inheritdoc />
    public partial class AddB2bPricingQuotesAndSuppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BulkQuoteRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    QuotedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    QuoteValidUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkQuoteRequests", x => x.Id);
                    table.CheckConstraint("CK_BulkQuoteRequests_Status", "[Status] IN ('Submitted', 'UnderReview', 'Quoted', 'Accepted', 'Rejected', 'Expired')");
                    table.CheckConstraint("CK_BulkQuoteRequests_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc] AND ([QuoteValidUntilUtc] IS NULL OR [QuotedAtUtc] IS NOT NULL) AND ([AcceptedAtUtc] IS NULL OR [Status] = 'Accepted')");
                    table.ForeignKey(
                        name: "FK_BulkQuoteRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerGroups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerGroups", x => x.Id);
                    table.CheckConstraint("CK_CustomerGroups_Priority", "[Priority] >= 0");
                    table.CheckConstraint("CK_CustomerGroups_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc]");
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    HealthStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.CheckConstraint("CK_Suppliers_HealthStatus", "[HealthStatus] IN ('Healthy', 'Degraded', 'Unhealthy')");
                    table.CheckConstraint("CK_Suppliers_Priority", "[Priority] >= 0");
                    table.CheckConstraint("CK_Suppliers_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc]");
                });

            migrationBuilder.CreateTable(
                name: "BulkQuoteLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BulkQuoteRequestId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    RequestedIdentifier = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NormalizedIdentifier = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RequestedQuantity = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QuotedUnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AvailableQuantity = table.Column<int>(type: "int", nullable: true),
                    LeadTimeDays = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkQuoteLines", x => x.Id);
                    table.CheckConstraint("CK_BulkQuoteLines_Quantity", "[LineNumber] > 0 AND [RequestedQuantity] > 0 AND ([AvailableQuantity] IS NULL OR [AvailableQuantity] >= 0) AND ([LeadTimeDays] IS NULL OR [LeadTimeDays] >= 0)");
                    table.CheckConstraint("CK_BulkQuoteLines_Quote", "([Status] = 'Quoted' AND [QuotedUnitPrice] IS NOT NULL AND [QuotedUnitPrice] > 0) OR ([Status] <> 'Quoted' AND [QuotedUnitPrice] IS NULL)");
                    table.CheckConstraint("CK_BulkQuoteLines_Status", "[Status] IN ('Unmatched', 'Matched', 'Quoted', 'Unavailable')");
                    table.ForeignKey(
                        name: "FK_BulkQuoteLines_BulkQuoteRequests_BulkQuoteRequestId",
                        column: x => x.BulkQuoteRequestId,
                        principalTable: "BulkQuoteRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BulkQuoteLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DealerApplications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TaxNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerGroupId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DealerApplications", x => x.Id);
                    table.CheckConstraint("CK_DealerApplications_Group", "([Status] = 'Approved' AND [CustomerGroupId] IS NOT NULL) OR ([Status] <> 'Approved')");
                    table.CheckConstraint("CK_DealerApplications_Status", "[Status] IN ('Pending', 'Approved', 'Rejected', 'Suspended')");
                    table.CheckConstraint("CK_DealerApplications_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc]");
                    table.ForeignKey(
                        name: "FK_DealerApplications_CustomerGroups_CustomerGroupId",
                        column: x => x.CustomerGroupId,
                        principalTable: "CustomerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DealerApplications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceLists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CustomerGroupId = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLists", x => x.Id);
                    table.CheckConstraint("CK_PriceLists_Validity", "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                    table.ForeignKey(
                        name: "FK_PriceLists_CustomerGroups_CustomerGroupId",
                        column: x => x.CustomerGroupId,
                        principalTable: "CustomerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierOffers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SupplierId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    ExternalOfferId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OemNumber = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ShippingCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AvailableQuantity = table.Column<int>(type: "int", nullable: false),
                    LeadTimeDays = table.Column<int>(type: "int", nullable: false),
                    MinimumOrderQuantity = table.Column<int>(type: "int", nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CanDropship = table.Column<bool>(type: "bit", nullable: false),
                    CanSupplyWarehouse = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierOffers", x => x.Id);
                    table.CheckConstraint("CK_SupplierOffers_Capability", "[CanDropship] = 1 OR [CanSupplyWarehouse] = 1");
                    table.CheckConstraint("CK_SupplierOffers_Costs", "[UnitCost] >= 0 AND [ShippingCost] >= 0");
                    table.CheckConstraint("CK_SupplierOffers_LeadTime", "[LeadTimeDays] >= 0");
                    table.CheckConstraint("CK_SupplierOffers_Quantities", "[AvailableQuantity] >= 0 AND [MinimumOrderQuantity] > 0");
                    table.CheckConstraint("CK_SupplierOffers_Validity", "[ValidUntilUtc] > [CreatedAtUtc]");
                    table.ForeignKey(
                        name: "FK_SupplierOffers_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierOffers_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PriceRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PriceListId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    BrandId = table.Column<int>(type: "int", nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: true),
                    MinimumQuantity = table.Column<int>(type: "int", nullable: false),
                    MinimumPeriodRevenue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    DiscountPercentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    FixedUnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceRules", x => x.Id);
                    table.CheckConstraint("CK_PriceRules_Adjustment", "([DiscountPercentage] IS NULL AND [FixedUnitPrice] IS NOT NULL) OR ([DiscountPercentage] IS NOT NULL AND [FixedUnitPrice] IS NULL)");
                    table.CheckConstraint("CK_PriceRules_DiscountRange", "[DiscountPercentage] IS NULL OR (CAST([DiscountPercentage] AS REAL) > 0 AND CAST([DiscountPercentage] AS REAL) < 100)");
                    table.CheckConstraint("CK_PriceRules_FixedPriceRange", "[FixedUnitPrice] IS NULL OR [FixedUnitPrice] > 0");
                    table.CheckConstraint("CK_PriceRules_QuantityRevenue", "[MinimumQuantity] > 0 AND [MinimumPeriodRevenue] >= 0");
                    table.CheckConstraint("CK_PriceRules_Validity", "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                    table.ForeignKey(
                        name: "FK_PriceRules_Brands_BrandId",
                        column: x => x.BrandId,
                        principalTable: "Brands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceRules_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceRules_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceRules_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkQuoteLines_BulkQuoteRequestId_LineNumber",
                table: "BulkQuoteLines",
                columns: new[] { "BulkQuoteRequestId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkQuoteLines_ProductId",
                table: "BulkQuoteLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkQuoteRequests_IdempotencyKey",
                table: "BulkQuoteRequests",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkQuoteRequests_RequestNumber",
                table: "BulkQuoteRequests",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkQuoteRequests_UserId_Status_CreatedAtUtc",
                table: "BulkQuoteRequests",
                columns: new[] { "UserId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerGroups_Code",
                table: "CustomerGroups",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DealerApplications_CustomerGroupId",
                table: "DealerApplications",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DealerApplications_IdempotencyKey",
                table: "DealerApplications",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DealerApplications_UserId",
                table: "DealerApplications",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_Code",
                table: "PriceLists",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceLists_CustomerGroupId",
                table: "PriceLists",
                column: "CustomerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceRules_BrandId",
                table: "PriceRules",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceRules_CategoryId",
                table: "PriceRules",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceRules_PriceListId_Priority_ValidFromUtc",
                table: "PriceRules",
                columns: new[] { "PriceListId", "Priority", "ValidFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceRules_ProductId",
                table: "PriceRules",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOffers_ProductId_OemNumber_Currency_IsActive_ValidUntilUtc",
                table: "SupplierOffers",
                columns: new[] { "ProductId", "OemNumber", "Currency", "IsActive", "ValidUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOffers_SupplierId_ExternalOfferId",
                table: "SupplierOffers",
                columns: new[] { "SupplierId", "ExternalOfferId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Code",
                table: "Suppliers",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BulkQuoteLines");

            migrationBuilder.DropTable(
                name: "DealerApplications");

            migrationBuilder.DropTable(
                name: "PriceRules");

            migrationBuilder.DropTable(
                name: "SupplierOffers");

            migrationBuilder.DropTable(
                name: "BulkQuoteRequests");

            migrationBuilder.DropTable(
                name: "PriceLists");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropTable(
                name: "CustomerGroups");
        }
    }
}
