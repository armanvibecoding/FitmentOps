using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Data;

public static class MaintenanceConfiguration
{
    public static ModelBuilder ConfigureMaintenance(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var userVehicle = modelBuilder.Entity<UserVehicle>();
        userVehicle.ToTable("UserVehicles", table =>
            table.HasCheckConstraint(
                "CK_UserVehicles_Odometer",
                "[CurrentOdometerKm] IS NULL OR [CurrentOdometerKm] >= 0"));
        userVehicle.HasIndex(candidate => new { candidate.UserId, candidate.IdempotencyKey })
            .IsUnique();
        userVehicle.HasIndex(candidate => new { candidate.UserId, candidate.IsActive });
        userVehicle.HasOne(candidate => candidate.User)
            .WithMany()
            .HasForeignKey(candidate => candidate.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        userVehicle.HasOne(candidate => candidate.Vehicle)
            .WithMany()
            .HasForeignKey(candidate => candidate.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        var record = modelBuilder.Entity<MaintenanceRecord>();
        record.ToTable("MaintenanceRecords", table =>
            table.HasCheckConstraint(
                "CK_MaintenanceRecords_Odometer",
                "[OdometerKm] >= 0"));
        record.HasIndex(candidate => new { candidate.UserVehicleId, candidate.IdempotencyKey })
            .IsUnique();
        record.HasIndex(candidate => new { candidate.UserVehicleId, candidate.ServiceDateUtc });
        record.HasOne(candidate => candidate.UserVehicle)
            .WithMany(candidate => candidate.MaintenanceRecords)
            .HasForeignKey(candidate => candidate.UserVehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        var item = modelBuilder.Entity<MaintenanceRecordItem>();
        item.ToTable("MaintenanceRecordItems", table =>
        {
            table.HasCheckConstraint("CK_MaintenanceRecordItems_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint(
                "CK_MaintenanceRecordItems_UnitCost",
                "[UnitCost] IS NULL OR [UnitCost] >= 0");
        });
        item.Property(candidate => candidate.UnitCost).HasPrecision(18, 2);
        item.HasOne(candidate => candidate.MaintenanceRecord)
            .WithMany(candidate => candidate.Items)
            .HasForeignKey(candidate => candidate.MaintenanceRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        item.HasOne(candidate => candidate.Product)
            .WithMany()
            .HasForeignKey(candidate => candidate.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        var reminder = modelBuilder.Entity<MaintenanceReminder>();
        reminder.ToTable("MaintenanceReminders", table =>
        {
            table.HasCheckConstraint(
                "CK_MaintenanceReminders_DueTarget",
                "[DueDateUtc] IS NOT NULL OR [DueOdometerKm] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_MaintenanceReminders_Odometer",
                "[DueOdometerKm] IS NULL OR [DueOdometerKm] >= 0");
            table.HasCheckConstraint(
                "CK_MaintenanceReminders_Completion",
                "([IsCompleted] = 0 AND [CompletedAtUtc] IS NULL) OR " +
                "([IsCompleted] = 1 AND [CompletedAtUtc] IS NOT NULL)");
        });
        reminder.HasIndex(candidate => new { candidate.UserVehicleId, candidate.IdempotencyKey })
            .IsUnique();
        reminder.HasIndex(candidate => new
        {
            candidate.IsCompleted,
            candidate.DueDateUtc,
            candidate.DueOdometerKm
        });
        reminder.HasOne(candidate => candidate.UserVehicle)
            .WithMany(candidate => candidate.Reminders)
            .HasForeignKey(candidate => candidate.UserVehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }
}
