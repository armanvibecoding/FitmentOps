using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class OrdersControllerTests
{
    [Fact]
    public async Task TrackOrder_WithMatchingEmail_ReturnsMinimalTrackingData()
    {
        await using var context = CreateContext();
        SeedOrder(context);
        var controller = CreateController(context);

        var result = await controller.TrackOrder(new TrackOrderDto
        {
            OrderNumber = "  ORD-TRACK-1  ",
            Email = "  CUSTOMER@EXAMPLE.COM  "
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<OrderTrackingResponseDto>(ok.Value);

        Assert.Equal("ORD-TRACK-1", response.OrderNumber);
        Assert.Equal("Pending", response.Status);
        Assert.Equal(250m, response.TotalAmount);
        var item = Assert.Single(response.Items);
        Assert.Equal("Test Ürünü", item.ProductName);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(125m, item.UnitPrice);

        var responsePropertyNames = typeof(OrderTrackingResponseDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(nameof(Order.CustomerEmail), responsePropertyNames);
        Assert.DoesNotContain(nameof(Order.CustomerPhone), responsePropertyNames);
        Assert.DoesNotContain(nameof(Order.ShippingAddress), responsePropertyNames);
    }

    [Fact]
    public async Task TrackOrder_WithWrongEmail_ReturnsNotFound()
    {
        await using var context = CreateContext();
        SeedOrder(context);
        var controller = CreateController(context);

        var result = await controller.TrackOrder(new TrackOrderDto
        {
            OrderNumber = "ORD-TRACK-1",
            Email = "attacker@example.com"
        });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public void LegacyOrderNumberEndpoint_IsNotAnonymous()
    {
        var method = typeof(OrdersController).GetMethod(nameof(OrdersController.GetOrderByNumber));

        Assert.NotNull(method);
        Assert.NotEmpty(typeof(OrdersController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
    }

    private static AutoPartsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AutoPartsDbContext(options);
    }

    private static OrdersController CreateController(AutoPartsDbContext context)
    {
        var configuration = new ConfigurationBuilder().Build();
        var emailService = new EmailService(
            configuration,
            NullLogger<EmailService>.Instance);
        var checkoutService = new CheckoutService(
            context,
            new LegalConsentService(context, new LegalCheckoutOptions()));

        return new OrdersController(
            context,
            emailService,
            checkoutService,
            NullLogger<OrdersController>.Instance);
    }

    private static void SeedOrder(AutoPartsDbContext context)
    {
        var product = new Product
        {
            Id = 1001,
            Name = "Test Ürünü",
            Description = "Sipariş takip testi için ürün.",
            PartNumber = "TEST-001",
            Price = 125m,
            Stock = 10,
            ImageUrl = "/images/test.jpg",
            CategoryId = 1,
            BrandId = 1,
            PartBrandId = 1
        };

        var order = new Order
        {
            Id = 2001,
            OrderNumber = "ORD-TRACK-1",
            CustomerName = "Test Müşterisi",
            CustomerEmail = "customer@example.com",
            CustomerPhone = "+905551112233",
            ShippingAddress = "Test Mahallesi, Test Sokak No: 1",
            City = "İstanbul",
            PostalCode = "34000",
            TotalAmount = 250m,
            Status = "Pending",
            OrderDate = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            OrderItems =
            {
                new OrderItem
                {
                    Id = 3001,
                    Product = product,
                    ProductId = product.Id,
                    Quantity = 2,
                    Price = 125m
                }
            }
        };

        context.Orders.Add(order);
        context.SaveChanges();
    }
}
