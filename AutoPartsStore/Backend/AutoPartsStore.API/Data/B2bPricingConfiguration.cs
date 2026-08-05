using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class B2bPricingConfiguration
{
    public static ModelBuilder ConfigureB2bPricing(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var application = modelBuilder.Entity<DealerApplication>();
        application.ToTable("DealerApplications", table =>
        {
            table.HasCheckConstraint(
                "CK_DealerApplications_Status",
                "[Status] IN ('Pending', 'Approved', 'Rejected', 'Suspended')");
            table.HasCheckConstraint(
                "CK_DealerApplications_Group",
                "([Status] = 'Approved' AND [CustomerGroupId] IS NOT NULL) OR " +
                "([Status] <> 'Approved')");
            table.HasCheckConstraint(
                "CK_DealerApplications_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc]");
        });
        application.HasIndex(candidate => candidate.UserId).IsUnique();
        application.HasIndex(candidate => candidate.IdempotencyKey).IsUnique();
        application.HasOne(candidate => candidate.User)
            .WithMany()
            .HasForeignKey(candidate => candidate.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        application.HasOne(candidate => candidate.CustomerGroup)
            .WithMany()
            .HasForeignKey(candidate => candidate.CustomerGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        var group = modelBuilder.Entity<CustomerGroup>();
        group.ToTable("CustomerGroups", table =>
        {
            table.HasCheckConstraint("CK_CustomerGroups_Priority", "[Priority] >= 0");
            table.HasCheckConstraint(
                "CK_CustomerGroups_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc]");
        });
        group.HasIndex(candidate => candidate.Code).IsUnique();

        var priceList = modelBuilder.Entity<PriceList>();
        priceList.ToTable("PriceLists", table =>
            table.HasCheckConstraint(
                "CK_PriceLists_Validity",
                "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]"));
        priceList.HasIndex(candidate => candidate.Code).IsUnique();
        priceList.HasOne(candidate => candidate.CustomerGroup)
            .WithMany(candidate => candidate.PriceLists)
            .HasForeignKey(candidate => candidate.CustomerGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        var rule = modelBuilder.Entity<PriceRule>();
        rule.ToTable("PriceRules", table =>
        {
            table.HasCheckConstraint(
                "CK_PriceRules_QuantityRevenue",
                "[MinimumQuantity] > 0 AND [MinimumPeriodRevenue] >= 0");
            table.HasCheckConstraint(
                "CK_PriceRules_Adjustment",
                "([DiscountPercentage] IS NULL AND [FixedUnitPrice] IS NOT NULL) OR " +
                "([DiscountPercentage] IS NOT NULL AND [FixedUnitPrice] IS NULL)");
            table.HasCheckConstraint(
                "CK_PriceRules_DiscountRange",
                "[DiscountPercentage] IS NULL OR " +
                "(CAST([DiscountPercentage] AS REAL) > 0 AND " +
                "CAST([DiscountPercentage] AS REAL) < 100)");
            table.HasCheckConstraint(
                "CK_PriceRules_FixedPriceRange",
                "[FixedUnitPrice] IS NULL OR [FixedUnitPrice] > 0");
            table.HasCheckConstraint(
                "CK_PriceRules_Validity",
                "[ValidToUtc] IS NULL OR [ValidToUtc] > [ValidFromUtc]");
        });
        rule.Property(candidate => candidate.MinimumPeriodRevenue).HasPrecision(18, 2);
        rule.Property(candidate => candidate.DiscountPercentage).HasPrecision(5, 2);
        rule.Property(candidate => candidate.FixedUnitPrice).HasPrecision(18, 2);
        rule.HasOne(candidate => candidate.PriceList)
            .WithMany(candidate => candidate.Rules)
            .HasForeignKey(candidate => candidate.PriceListId)
            .OnDelete(DeleteBehavior.Restrict);
        rule.HasOne(candidate => candidate.Product)
            .WithMany()
            .HasForeignKey(candidate => candidate.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        rule.HasOne(candidate => candidate.Brand)
            .WithMany()
            .HasForeignKey(candidate => candidate.BrandId)
            .OnDelete(DeleteBehavior.Restrict);
        rule.HasOne(candidate => candidate.Category)
            .WithMany()
            .HasForeignKey(candidate => candidate.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }
}
