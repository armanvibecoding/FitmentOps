using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class BulkQuoteConfiguration
{
    public static ModelBuilder ConfigureBulkQuotes(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var request = modelBuilder.Entity<BulkQuoteRequest>();
        request.ToTable("BulkQuoteRequests", table =>
        {
            table.HasCheckConstraint(
                "CK_BulkQuoteRequests_Status",
                "[Status] IN ('Submitted', 'UnderReview', 'Quoted', 'Accepted', 'Rejected', 'Expired')");
            table.HasCheckConstraint(
                "CK_BulkQuoteRequests_Timestamps",
                "[UpdatedAtUtc] >= [CreatedAtUtc] AND " +
                "([QuoteValidUntilUtc] IS NULL OR [QuotedAtUtc] IS NOT NULL) AND " +
                "([AcceptedAtUtc] IS NULL OR [Status] = 'Accepted')");
        });
        request.HasIndex(candidate => candidate.RequestNumber).IsUnique();
        request.HasIndex(candidate => candidate.IdempotencyKey).IsUnique();
        request.HasOne(candidate => candidate.User)
            .WithMany()
            .HasForeignKey(candidate => candidate.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        var line = modelBuilder.Entity<BulkQuoteLine>();
        line.ToTable("BulkQuoteLines", table =>
        {
            table.HasCheckConstraint(
                "CK_BulkQuoteLines_Status",
                "[Status] IN ('Unmatched', 'Matched', 'Quoted', 'Unavailable')");
            table.HasCheckConstraint(
                "CK_BulkQuoteLines_Quantity",
                "[LineNumber] > 0 AND [RequestedQuantity] > 0 AND " +
                "([AvailableQuantity] IS NULL OR [AvailableQuantity] >= 0) AND " +
                "([LeadTimeDays] IS NULL OR [LeadTimeDays] >= 0)");
            table.HasCheckConstraint(
                "CK_BulkQuoteLines_Quote",
                "([Status] = 'Quoted' AND [QuotedUnitPrice] IS NOT NULL AND [QuotedUnitPrice] > 0) OR " +
                "([Status] <> 'Quoted' AND [QuotedUnitPrice] IS NULL)");
        });
        line.Property(candidate => candidate.QuotedUnitPrice).HasPrecision(18, 2);
        line.HasIndex(candidate => new { candidate.BulkQuoteRequestId, candidate.LineNumber })
            .IsUnique();
        line.HasOne(candidate => candidate.BulkQuoteRequest)
            .WithMany(candidate => candidate.Lines)
            .HasForeignKey(candidate => candidate.BulkQuoteRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        line.HasOne(candidate => candidate.Product)
            .WithMany()
            .HasForeignKey(candidate => candidate.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }
}
