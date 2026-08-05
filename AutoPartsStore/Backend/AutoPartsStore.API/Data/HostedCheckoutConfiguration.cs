using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoPartsStore.API.Data;

public static class HostedCheckoutConfiguration
{
    /// <summary>Must be called from AutoPartsDbContext.OnModelCreating.</summary>
    public static ModelBuilder ConfigureHostedCheckout(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var session = modelBuilder.Entity<HostedCheckoutSession>();
        session.ToTable("HostedCheckoutSessions");
        session.HasIndex(candidate => candidate.IdempotencyKey).IsUnique();
        session.HasIndex(candidate => candidate.InventoryReservationId).IsUnique();
        session.HasIndex(candidate => candidate.OrderId).IsUnique();
        session.HasOne(candidate => candidate.InventoryReservation)
            .WithOne()
            .HasForeignKey<HostedCheckoutSession>(candidate => candidate.InventoryReservationId)
            .OnDelete(DeleteBehavior.Restrict);
        session.HasOne(candidate => candidate.Order)
            .WithOne()
            .HasForeignKey<HostedCheckoutSession>(candidate => candidate.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        return modelBuilder;
    }

    public static IServiceCollection AddHostedCheckout(
        this IServiceCollection services,
        HostedCheckoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        options ??= new HostedCheckoutOptions();
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddScoped<InventoryReservationService>();
        services.TryAddScoped<OrderLifecycleService>();
        services.TryAddScoped<HostedCheckoutService>();
        return services;
    }
}
