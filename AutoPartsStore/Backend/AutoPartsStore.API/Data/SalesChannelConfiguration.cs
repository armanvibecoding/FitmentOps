using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class SalesChannelConfiguration
{
    private static readonly DateTime SeedCreatedAt =
        new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

    public static ModelBuilder ConfigureSalesChannels(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var channel = modelBuilder.Entity<SalesChannel>();
        channel.ToTable("SalesChannels", table =>
            table.HasCheckConstraint(
                "CK_SalesChannels_Mode",
                "[Mode] IN ('Disabled', 'Sandbox', 'Production')"));
        channel.HasIndex(candidate => candidate.Code).IsUnique();
        channel.HasData(
            new SalesChannel
            {
                Id = 1,
                Code = SalesChannelCodes.Trendyol,
                DisplayName = "Trendyol",
                RequestedEnabled = false,
                Mode = SalesChannelModes.Disabled,
                CreatedAtUtc = SeedCreatedAt,
                UpdatedAtUtc = SeedCreatedAt,
                ConcurrencyToken = Guid.Parse("0d847fc5-94c8-4309-a14b-e8dd38cc8036")
            },
            new SalesChannel
            {
                Id = 2,
                Code = SalesChannelCodes.Hepsiburada,
                DisplayName = "Hepsiburada",
                RequestedEnabled = false,
                Mode = SalesChannelModes.Disabled,
                CreatedAtUtc = SeedCreatedAt,
                UpdatedAtUtc = SeedCreatedAt,
                ConcurrencyToken = Guid.Parse("e7b5fd9e-418d-46d2-9951-c12944850b7b")
            });

        var listing = modelBuilder.Entity<ChannelListing>();
        listing.ToTable("ChannelListings", table =>
        {
            table.HasCheckConstraint(
                "CK_ChannelListings_Status",
                "[Status] IN ('Blocked', 'Pending', 'Active', 'Error')");
            table.HasCheckConstraint(
                "CK_ChannelListings_Desired",
                "[DesiredPrice] > 0 AND [DesiredStock] >= 0");
            table.HasCheckConstraint(
                "CK_ChannelListings_Observed",
                "([ObservedPrice] IS NULL OR [ObservedPrice] > 0) AND " +
                "([ObservedStock] IS NULL OR [ObservedStock] >= 0)");
        });
        listing.Property(candidate => candidate.DesiredPrice).HasPrecision(18, 2);
        listing.Property(candidate => candidate.ObservedPrice).HasPrecision(18, 2);
        listing.HasIndex(candidate => new { candidate.SalesChannelId, candidate.ProductId }).IsUnique();
        listing.HasIndex(candidate => new { candidate.SalesChannelId, candidate.ExternalListingId })
            .IsUnique()
            .HasFilter("[ExternalListingId] IS NOT NULL");
        listing.HasOne(candidate => candidate.SalesChannel)
            .WithMany(candidate => candidate.Listings)
            .HasForeignKey(candidate => candidate.SalesChannelId)
            .OnDelete(DeleteBehavior.Restrict);
        listing.HasOne(candidate => candidate.Product)
            .WithMany()
            .HasForeignKey(candidate => candidate.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        var orderLink = modelBuilder.Entity<ChannelOrderLink>();
        orderLink.HasIndex(candidate => new { candidate.SalesChannelId, candidate.ExternalOrderId })
            .IsUnique();
        orderLink.HasIndex(candidate => candidate.OrderId).IsUnique();
        orderLink.HasOne(candidate => candidate.SalesChannel)
            .WithMany(candidate => candidate.Orders)
            .HasForeignKey(candidate => candidate.SalesChannelId)
            .OnDelete(DeleteBehavior.Restrict);
        orderLink.HasOne(candidate => candidate.Order)
            .WithOne()
            .HasForeignKey<ChannelOrderLink>(candidate => candidate.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        var inbox = modelBuilder.Entity<ChannelInboxEvent>();
        inbox.ToTable("ChannelInboxEvents", table =>
            table.HasCheckConstraint(
                "CK_ChannelInboxEvents_Status",
                "[Status] IN ('Processed', 'Failed')"));
        inbox.HasIndex(candidate => new { candidate.SalesChannelId, candidate.ExternalEventId })
            .IsUnique();
        inbox.HasIndex(candidate => new { candidate.Status, candidate.ReceivedAtUtc });
        inbox.HasOne(candidate => candidate.SalesChannel)
            .WithMany(candidate => candidate.InboxEvents)
            .HasForeignKey(candidate => candidate.SalesChannelId)
            .OnDelete(DeleteBehavior.Restrict);
        inbox.HasOne(candidate => candidate.ChannelOrderLink)
            .WithMany(candidate => candidate.InboxEvents)
            .HasForeignKey(candidate => candidate.ChannelOrderLinkId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }
}
