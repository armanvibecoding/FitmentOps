using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class LegalConfiguration
{
    public static void ConfigureLegalDocuments(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LegalDocumentVersion>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_LegalDocumentVersions_Status",
                "[Status] IN ('Draft', 'Published', 'Retired')"));
            entity.HasIndex(document => new { document.DocumentType, document.Version })
                .IsUnique();
            entity.HasIndex(document => document.DocumentType)
                .IsUnique()
                .HasFilter("[Status] = 'Published'");
            entity.Property(document => document.DocumentType).HasMaxLength(50);
            entity.Property(document => document.Version).HasMaxLength(40);
            entity.Property(document => document.Title).HasMaxLength(200);
            entity.Property(document => document.Content).HasMaxLength(100_000);
            entity.Property(document => document.ContentSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(document => document.Status).HasMaxLength(20);
        });

        modelBuilder.Entity<LegalAcceptance>(entity =>
        {
            entity.HasIndex(acceptance => new
            {
                acceptance.OrderId,
                acceptance.DocumentTypeSnapshot
            })
                .IsUnique();
            entity.HasIndex(acceptance => acceptance.LegalDocumentVersionId);
            entity.Property(acceptance => acceptance.DocumentTypeSnapshot).HasMaxLength(50);
            entity.Property(acceptance => acceptance.VersionSnapshot).HasMaxLength(40);
            entity.Property(acceptance => acceptance.ContentSha256Snapshot).HasMaxLength(64).IsFixedLength();
            entity.Property(acceptance => acceptance.CheckoutReferenceSha256).HasMaxLength(64).IsFixedLength();
            entity.HasOne(acceptance => acceptance.Order)
                .WithMany(order => order.LegalAcceptances)
                .HasForeignKey(acceptance => acceptance.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(acceptance => acceptance.LegalDocumentVersion)
                .WithMany(document => document.Acceptances)
                .HasForeignKey(acceptance => acceptance.LegalDocumentVersionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
