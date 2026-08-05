using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPartsStore.API.Migrations
{
    /// <inheritdoc />
    public partial class AddFitmentAuditAndInventoryReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationIdSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKeySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PreviousEventHashSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventHashSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuditEvents", x => x.Id);
                    table.CheckConstraint("CK_AdminAuditEvents_Outcome", "[Outcome] IN ('succeeded', 'rejected', 'failed', 'replayed')");
                    table.CheckConstraint("CK_AdminAuditEvents_PositiveIdentity", "[Sequence] > 0 AND [ActorUserId] > 0 AND [AggregateId] > 0");
                    table.CheckConstraint("CK_AdminAuditEvents_Role", "[ActorRole] IN ('finance', 'warehouse', 'catalog', 'support', 'superadmin', 'Admin')");
                });

            migrationBuilder.CreateTable(
                name: "InventoryReservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CommittedOrderId = table.Column<int>(type: "int", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryReservations", x => x.Id);
                    table.CheckConstraint("CK_InventoryReservations_CommitLink", "([Status] = 'Committed' AND [CommittedOrderId] IS NOT NULL) OR ([Status] <> 'Committed' AND [CommittedOrderId] IS NULL)");
                    table.CheckConstraint("CK_InventoryReservations_Status", "[Status] IN ('Active', 'Committed', 'Released', 'Expired')");
                    table.CheckConstraint("CK_InventoryReservations_Timestamps", "[ExpiresAt] > [CreatedAt] AND [UpdatedAt] >= [CreatedAt]");
                    table.ForeignKey(
                        name: "FK_InventoryReservations_Orders_CommittedOrderId",
                        column: x => x.CommittedOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductIdentifiers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SchemeAuthority = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    NormalizedValue = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductIdentifiers", x => x.Id);
                    table.CheckConstraint("CK_ProductIdentifiers_Enums", "[Kind] IN ('Oem', 'Interchange', 'ManufacturerPartNumber', 'SupplierSku') AND [SourceKind] IN ('UnverifiedImport', 'Manufacturer', 'AuthorizedSupplier', 'LicensedCatalog', 'ManualExpertReview')");
                    table.CheckConstraint("CK_ProductIdentifiers_Validity", "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                    table.CheckConstraint("CK_ProductIdentifiers_VerifiedSource", "[IsVerified] = 0 OR [SourceKind] <> 'UnverifiedImport'");
                    table.ForeignKey(
                        name: "FK_ProductIdentifiers_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleMakes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CanonicalKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleMakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryReservationItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryReservationId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryReservationItems", x => x.Id);
                    table.CheckConstraint("CK_InventoryReservationItems_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_InventoryReservationItems_InventoryReservations_InventoryReservationId",
                        column: x => x.InventoryReservationId,
                        principalTable: "InventoryReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryReservationItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MakeId = table.Column<int>(type: "int", nullable: false),
                    CanonicalKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleModels_VehicleMakes_MakeId",
                        column: x => x.MakeId,
                        principalTable: "VehicleMakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleGenerations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ModelId = table.Column<int>(type: "int", nullable: false),
                    CanonicalKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProductionStartYear = table.Column<int>(type: "int", nullable: true),
                    ProductionEndYear = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleGenerations", x => x.Id);
                    table.CheckConstraint("CK_VehicleGenerations_Years", "([ProductionStartYear] IS NULL OR [ProductionStartYear] BETWEEN 1886 AND 2200) AND ([ProductionEndYear] IS NULL OR [ProductionEndYear] BETWEEN 1886 AND 2200) AND ([ProductionStartYear] IS NULL OR [ProductionEndYear] IS NULL OR [ProductionEndYear] >= [ProductionStartYear])");
                    table.ForeignKey(
                        name: "FK_VehicleGenerations_VehicleModels_ModelId",
                        column: x => x.ModelId,
                        principalTable: "VehicleModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleEngines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GenerationId = table.Column<int>(type: "int", nullable: false),
                    CanonicalKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EngineCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    FuelType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DisplacementCc = table.Column<int>(type: "int", nullable: true),
                    PowerKw = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleEngines", x => x.Id);
                    table.CheckConstraint("CK_VehicleEngines_Specifications", "([DisplacementCc] IS NULL OR [DisplacementCc] BETWEEN 1 AND 20000) AND ([PowerKw] IS NULL OR ([PowerKw] > 0 AND [PowerKw] <= 5000))");
                    table.ForeignKey(
                        name: "FK_VehicleEngines_VehicleGenerations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "VehicleGenerations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EngineId = table.Column<int>(type: "int", nullable: false),
                    CanonicalKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BodyStyle = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Transmission = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    DriveType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Market = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ProductionStartYear = table.Column<int>(type: "int", nullable: true),
                    ProductionEndYear = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.CheckConstraint("CK_Vehicles_Years", "([ProductionStartYear] IS NULL OR [ProductionStartYear] BETWEEN 1886 AND 2200) AND ([ProductionEndYear] IS NULL OR [ProductionEndYear] BETWEEN 1886 AND 2200) AND ([ProductionStartYear] IS NULL OR [ProductionEndYear] IS NULL OR [ProductionEndYear] >= [ProductionStartYear])");
                    table.ForeignKey(
                        name: "FK_Vehicles_VehicleEngines_EngineId",
                        column: x => x.EngineId,
                        principalTable: "VehicleEngines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductFitments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<int>(type: "int", nullable: false),
                    AssertionKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductFitments", x => x.Id);
                    table.CheckConstraint("CK_ProductFitments_Confidence", "[Confidence] >= 0 AND [Confidence] <= 1");
                    table.CheckConstraint("CK_ProductFitments_Enums", "[AssertionKind] IN ('Exact', 'Compatible') AND [SourceKind] IN ('UnverifiedImport', 'Manufacturer', 'AuthorizedSupplier', 'LicensedCatalog', 'ManualExpertReview')");
                    table.CheckConstraint("CK_ProductFitments_Validity", "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                    table.CheckConstraint("CK_ProductFitments_VerifiedSource", "[IsVerified] = 0 OR [SourceKind] <> 'UnverifiedImport'");
                    table.ForeignKey(
                        name: "FK_ProductFitments_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductFitments_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_IdempotencyKeySha256",
                table: "AdminAuditEvents",
                column: "IdempotencyKeySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_OccurredAtUtc",
                table: "AdminAuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_Sequence",
                table: "AdminAuditEvents",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservationItems_InventoryReservationId_ProductId",
                table: "InventoryReservationItems",
                columns: new[] { "InventoryReservationId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservationItems_ProductId",
                table: "InventoryReservationItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_CommittedOrderId",
                table: "InventoryReservations",
                column: "CommittedOrderId",
                unique: true,
                filter: "[CommittedOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_IdempotencyKey",
                table: "InventoryReservations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductFitments_IdempotencyKey",
                table: "ProductFitments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductFitments_ProductId_VehicleId",
                table: "ProductFitments",
                columns: new[] { "ProductId", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductFitments_SourceName_SourceRecordId",
                table: "ProductFitments",
                columns: new[] { "SourceName", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductFitments_VehicleId_ValidFromUtc_ValidToUtc",
                table: "ProductFitments",
                columns: new[] { "VehicleId", "ValidFromUtc", "ValidToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductIdentifiers_Kind_SchemeAuthority_NormalizedValue",
                table: "ProductIdentifiers",
                columns: new[] { "Kind", "SchemeAuthority", "NormalizedValue" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductIdentifiers_ProductId_Kind_SchemeAuthority_NormalizedValue",
                table: "ProductIdentifiers",
                columns: new[] { "ProductId", "Kind", "SchemeAuthority", "NormalizedValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductIdentifiers_SourceName_SourceRecordId",
                table: "ProductIdentifiers",
                columns: new[] { "SourceName", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleEngines_GenerationId_CanonicalKey",
                table: "VehicleEngines",
                columns: new[] { "GenerationId", "CanonicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleGenerations_ModelId_CanonicalKey",
                table: "VehicleGenerations",
                columns: new[] { "ModelId", "CanonicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMakes_CanonicalKey",
                table: "VehicleMakes",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleModels_MakeId_CanonicalKey",
                table: "VehicleModels",
                columns: new[] { "MakeId", "CanonicalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_EngineId_CanonicalKey",
                table: "Vehicles",
                columns: new[] { "EngineId", "CanonicalKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAuditEvents");

            migrationBuilder.DropTable(
                name: "InventoryReservationItems");

            migrationBuilder.DropTable(
                name: "ProductFitments");

            migrationBuilder.DropTable(
                name: "ProductIdentifiers");

            migrationBuilder.DropTable(
                name: "InventoryReservations");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "VehicleEngines");

            migrationBuilder.DropTable(
                name: "VehicleGenerations");

            migrationBuilder.DropTable(
                name: "VehicleModels");

            migrationBuilder.DropTable(
                name: "VehicleMakes");
        }
    }
}
