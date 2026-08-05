using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AuthControllerTests
{
    [Fact]
    public async Task Register_NormalizesEmailAndRejectsCaseVariantDuplicate()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var first = await controller.Register(new RegisterDto
        {
            Email = "  Customer@Example.COM ",
            Password = "strong-password-123",
            FullName = "Customer Test"
        });
        var duplicate = await controller.Register(new RegisterDto
        {
            Email = "customer@example.com",
            Password = "another-strong-password",
            FullName = "Duplicate Test"
        });

        Assert.IsType<OkObjectResult>(first.Result);
        Assert.IsType<BadRequestObjectResult>(duplicate.Result);
        Assert.Equal("customer@example.com", (await context.Users.SingleAsync()).Email);
    }

    [Fact]
    public void RegisterDto_RejectsShortPassword()
    {
        var dto = new RegisterDto
        {
            Email = "customer@example.com",
            Password = "short",
            FullName = "Customer Test"
        };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            dto,
            new ValidationContext(dto),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(RegisterDto.Password)));
    }

    private static AuthController CreateController(AutoPartsDbContext context)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-jwt-key-with-at-least-32-characters",
                ["Jwt:Issuer"] = "AutoPartsStore.Tests",
                ["Jwt:Audience"] = "AutoPartsStore.Tests"
            })
            .Build();

        return new AuthController(context, new JwtService(configuration));
    }

    private static AutoPartsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AutoPartsDbContext(options);
    }
}
