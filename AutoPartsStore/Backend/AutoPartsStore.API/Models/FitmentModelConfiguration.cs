using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Models;

public static class FitmentModelConfiguration
{
    public static ModelBuilder ConfigureFitmentModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VehicleMake>(entity =>
        {
            entity.ToTable("VehicleMakes");
            entity.HasIndex(item => item.CanonicalKey).IsUnique();
        });

        modelBuilder.Entity<VehicleModel>(entity =>
        {
            entity.ToTable("VehicleModels");
            entity.HasIndex(item => new { item.MakeId, item.CanonicalKey }).IsUnique();
            entity.HasOne(item => item.Make)
                .WithMany(item => item.Models)
                .HasForeignKey(item => item.MakeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VehicleGeneration>(entity =>
        {
            entity.ToTable("VehicleGenerations", table =>
            {
                table.HasCheckConstraint(
                    "CK_VehicleGenerations_Years",
                    "([ProductionStartYear] IS NULL OR [ProductionStartYear] BETWEEN 1886 AND 2200) AND " +
                    "([ProductionEndYear] IS NULL OR [ProductionEndYear] BETWEEN 1886 AND 2200) AND " +
                    "([ProductionStartYear] IS NULL OR [ProductionEndYear] IS NULL OR [ProductionEndYear] >= [ProductionStartYear])");
            });
            entity.HasIndex(item => new { item.ModelId, item.CanonicalKey }).IsUnique();
            entity.HasOne(item => item.Model)
                .WithMany(item => item.Generations)
                .HasForeignKey(item => item.ModelId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VehicleEngine>(entity =>
        {
            entity.ToTable("VehicleEngines", table =>
            {
                table.HasCheckConstraint(
                    "CK_VehicleEngines_Specifications",
                    "([DisplacementCc] IS NULL OR [DisplacementCc] BETWEEN 1 AND 20000) AND " +
                    "([PowerKw] IS NULL OR ([PowerKw] > 0 AND [PowerKw] <= 5000))");
            });
            entity.Property(item => item.PowerKw).HasPrecision(8, 2);
            entity.HasIndex(item => new { item.GenerationId, item.CanonicalKey }).IsUnique();
            entity.HasOne(item => item.Generation)
                .WithMany(item => item.Engines)
                .HasForeignKey(item => item.GenerationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Vehicle>(entity =>
        {
            entity.ToTable("Vehicles", table =>
            {
                table.HasCheckConstraint(
                    "CK_Vehicles_Years",
                    "([ProductionStartYear] IS NULL OR [ProductionStartYear] BETWEEN 1886 AND 2200) AND " +
                    "([ProductionEndYear] IS NULL OR [ProductionEndYear] BETWEEN 1886 AND 2200) AND " +
                    "([ProductionStartYear] IS NULL OR [ProductionEndYear] IS NULL OR [ProductionEndYear] >= [ProductionStartYear])");
            });
            entity.HasIndex(item => new { item.EngineId, item.CanonicalKey }).IsUnique();
            entity.HasOne(item => item.Engine)
                .WithMany(item => item.Vehicles)
                .HasForeignKey(item => item.EngineId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductFitment>(entity =>
        {
            entity.ToTable("ProductFitments", table =>
            {
                table.HasCheckConstraint(
                    "CK_ProductFitments_Confidence",
                    "[Confidence] >= 0 AND [Confidence] <= 1");
                table.HasCheckConstraint(
                    "CK_ProductFitments_Validity",
                    "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                table.HasCheckConstraint(
                    "CK_ProductFitments_VerifiedSource",
                    "[IsVerified] = 0 OR [SourceKind] <> 'UnverifiedImport'");
                table.HasCheckConstraint(
                    "CK_ProductFitments_Enums",
                    "[AssertionKind] IN ('Exact', 'Compatible') AND " +
                    "[SourceKind] IN ('UnverifiedImport', 'Manufacturer', 'AuthorizedSupplier', 'LicensedCatalog', 'ManualExpertReview')");
            });
            entity.Property(item => item.AssertionKind).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.SourceKind).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.Confidence).HasPrecision(5, 4);
            entity.HasIndex(item => new { item.ProductId, item.VehicleId }).IsUnique();
            entity.HasIndex(item => item.IdempotencyKey).IsUnique();
            entity.HasIndex(item => new { item.SourceName, item.SourceRecordId }).IsUnique();
            entity.HasIndex(item => new { item.VehicleId, item.ValidFromUtc, item.ValidToUtc });
            entity.HasOne(item => item.Product)
                .WithMany()
                .HasForeignKey(item => item.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Vehicle)
                .WithMany(item => item.ProductFitments)
                .HasForeignKey(item => item.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductIdentifier>(entity =>
        {
            entity.ToTable("ProductIdentifiers", table =>
            {
                table.HasCheckConstraint(
                    "CK_ProductIdentifiers_Validity",
                    "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
                table.HasCheckConstraint(
                    "CK_ProductIdentifiers_VerifiedSource",
                    "[IsVerified] = 0 OR [SourceKind] <> 'UnverifiedImport'");
                table.HasCheckConstraint(
                    "CK_ProductIdentifiers_Enums",
                    "[Kind] IN ('Oem', 'Interchange', 'ManufacturerPartNumber', 'SupplierSku') AND " +
                    "[SourceKind] IN ('UnverifiedImport', 'Manufacturer', 'AuthorizedSupplier', 'LicensedCatalog', 'ManualExpertReview')");
            });
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.SourceKind).HasConversion<string>().HasMaxLength(40);
            entity.HasIndex(item => new
            {
                item.ProductId,
                item.Kind,
                item.SchemeAuthority,
                item.NormalizedValue
            }).IsUnique();
            entity.HasIndex(item => new
            {
                item.Kind,
                item.SchemeAuthority,
                item.NormalizedValue
            });
            entity.HasIndex(item => new { item.SourceName, item.SourceRecordId }).IsUnique();
            entity.HasOne(item => item.Product)
                .WithMany()
                .HasForeignKey(item => item.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
