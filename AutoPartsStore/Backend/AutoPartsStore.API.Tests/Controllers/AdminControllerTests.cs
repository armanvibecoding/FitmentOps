using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminControllerTests
{
    [Fact]
    public async Task GetAllUsers_DoesNotExposePasswordHash()
    {
        await using var context = CreateContext();
        context.Users.Add(new User
        {
            Email = "admin-user-list@example.com",
            Password = "$2a$10$sensitive-password-hash",
            FullName = "Admin User List Test",
            Phone = "+905551112233",
            Role = "User",
            IsActive = true
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.GetAllUsers();

        var user = Assert.Single(Assert.IsAssignableFrom<IEnumerable<AdminUserDto>>(result.Value));
        Assert.Equal("admin-user-list@example.com", user.Email);
        Assert.DoesNotContain("Password", JsonSerializer.Serialize(user));
        Assert.DoesNotContain("sensitive-password-hash", JsonSerializer.Serialize(user));
    }

    [Fact]
    public void UserEntitySerialization_IgnoresPassword()
    {
        var user = new User
        {
            Email = "serialization@example.com",
            Password = "sensitive-password-hash",
            FullName = "Serialization Test"
        };

        var json = JsonSerializer.Serialize(user);

        Assert.DoesNotContain("Password", json);
        Assert.DoesNotContain("sensitive-password-hash", json);
    }

    [Fact]
    public async Task PaymentOperationsReportGrossRefundedAndNetAmounts()
    {
        await using var context = CreateContext();
        var order = new Order
        {
            OrderNumber = "ORDER-FINANCE-1",
            CustomerName = "Finance Test",
            CustomerEmail = "finance@example.test",
            CustomerPhone = "+905550000000",
            ShippingAddress = "Test shipping address",
            City = "Istanbul",
            PostalCode = "34000",
            TotalAmount = 100m,
            Status = OrderStatuses.Processing
        };
        var payment = new Payment
        {
            Order = order,
            Provider = PaymentProviders.Manual,
            Method = PaymentMethods.PayAtDelivery,
            Status = PaymentStatuses.PartiallyRefunded,
            Amount = 100m,
            Currency = "TRY",
            IdempotencyKey = "payment-finance-1",
            PaidAt = DateTime.UtcNow
        };
        context.Payments.Add(payment);
        await context.SaveChangesAsync();
        context.Refunds.AddRange(
            new Refund
            {
                PaymentId = payment.Id,
                Provider = PaymentProviders.Manual.ToLowerInvariant(),
                IdempotencyKey = "refund-finance-succeeded",
                Status = RefundStatuses.Succeeded,
                Amount = 30m,
                Currency = "TRY"
            },
            new Refund
            {
                PaymentId = payment.Id,
                Provider = PaymentProviders.Manual.ToLowerInvariant(),
                IdempotencyKey = "refund-finance-pending",
                Status = RefundStatuses.Processing,
                Amount = 10m,
                Currency = "TRY"
            },
            new Refund
            {
                PaymentId = payment.Id,
                Provider = PaymentProviders.Manual.ToLowerInvariant(),
                IdempotencyKey = "refund-finance-unknown",
                Status = RefundStatuses.Unknown,
                Amount = 5m,
                Currency = "TRY"
            });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var paymentsResult = await controller.GetAllPayments();
        var paymentDto = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<AdminPaymentDto>>(paymentsResult.Value));
        var statsResult = await controller.GetStats();
        var ok = Assert.IsType<OkObjectResult>(statsResult.Result);
        var stats = Assert.IsType<DashboardStats>(ok.Value);

        Assert.Equal(30m, paymentDto.RefundedAmount);
        Assert.Equal(15m, paymentDto.PendingRefundAmount);
        Assert.Equal(100m, stats.GrossRevenue);
        Assert.Equal(30m, stats.RefundedAmount);
        Assert.Equal(70m, stats.TotalRevenue);
    }

    [Fact]
    public async Task DeleteProduct_ReferencedByOrderHistoryReturnsConflict()
    {
        await using var context = CreateContext();
        var product = new Product
        {
            Name = "Historical product",
            Description = "Must remain in order history",
            PartNumber = "HISTORY-1",
            Price = 25m,
            Stock = 0,
            CategoryId = 1,
            BrandId = 1,
            PartBrandId = 1
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();
        context.OrderItems.Add(new OrderItem
        {
            OrderId = 42,
            ProductId = product.Id,
            Quantity = 1,
            Price = 25m
        });
        await context.SaveChangesAsync();

        var controller = CreateController(context);
        var result = await controller.DeleteProduct(product.Id);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(await context.Products.AnyAsync(candidate => candidate.Id == product.Id));
    }

    private static AutoPartsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AutoPartsDbContext(options);
    }

    private static AdminController CreateController(AutoPartsDbContext context) =>
        new(
            context,
            new OrderLifecycleService(context),
            new AdminAuditService(context));
}
