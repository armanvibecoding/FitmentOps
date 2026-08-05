using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class SupplierConfiguration
{
    /// <summary>Must be called from AutoPartsDbContext.OnModelCreating.</summary>
    public static ModelBuilder ConfigureSupplierSourcing(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var supplier = modelBuilder.Entity<Supplier>();
        supplier.ToTable("Suppliers", table =>
        {
            table.HasCheckConstraint(
                "CK_Suppliers_HealthStatus",
                "[HealthStatus] IN ('Healthy', 'Degraded', 'Unhealthy')");
            table.HasCheckConstraint("CK_Suppliers_Priority", "[Priority] >= 0");
            table.HasCheckConstraint(
                "CK_Suppliers_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc]");
        });
        supplier.HasIndex(candidate => candidate.Code).IsUnique();

        var offer = modelBuilder.Entity<SupplierOffer>();
        offer.ToTable("SupplierOffers", table =>
        {
            table.HasCheckConstraint(
                "CK_SupplierOffers_Costs",
                "[UnitCost] >= 0 AND [ShippingCost] >= 0");
            table.HasCheckConstraint(
                "CK_SupplierOffers_Quantities",
                "[AvailableQuantity] >= 0 AND [MinimumOrderQuantity] > 0");
            table.HasCheckConstraint(
                "CK_SupplierOffers_LeadTime",
                "[LeadTimeDays] >= 0");
            table.HasCheckConstraint(
                "CK_SupplierOffers_Capability",
                "[CanDropship] = 1 OR [CanSupplyWarehouse] = 1");
            table.HasCheckConstraint(
                "CK_SupplierOffers_Validity",
                "[ValidUntilUtc] > [CreatedAtUtc]");
        });
        offer.Property(candidate => candidate.UnitCost).HasPrecision(18, 4);
        offer.Property(candidate => candidate.ShippingCost).HasPrecision(18, 4);
        offer.HasIndex(candidate => new { candidate.SupplierId, candidate.ExternalOfferId })
            .IsUnique();
        offer.HasIndex(candidate => new
        {
            candidate.ProductId,
            candidate.OemNumber,
            candidate.Currency,
            candidate.IsActive,
            candidate.ValidUntilUtc
        });
        offer.HasOne(candidate => candidate.Supplier)
            .WithMany(candidate => candidate.Offers)
            .HasForeignKey(candidate => candidate.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
        offer.HasOne(candidate => candidate.Product)
            .WithMany()
            .HasForeignKey(candidate => candidate.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }
}
