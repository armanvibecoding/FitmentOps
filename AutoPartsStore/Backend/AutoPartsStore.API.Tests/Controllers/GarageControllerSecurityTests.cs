using System.Reflection;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class GarageControllerSecurityTests
{
    [Fact]
    public void EveryEndpoint_RequiresAuthentication_AndWritesAreRateLimited()
    {
        Assert.NotNull(typeof(GarageController).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(typeof(GarageController).GetCustomAttribute<AllowAnonymousAttribute>());

        var endpoints = typeof(GarageController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToArray();
        Assert.Equal(8, endpoints.Length);
        Assert.All(endpoints, method =>
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>()));

        var writes = endpoints.Where(method =>
            method.GetCustomAttributes<HttpMethodAttribute>()
                .Any(attribute => attribute.HttpMethods.Any(httpMethod => httpMethod != "GET")));
        Assert.All(writes, method =>
        {
            var limiter = Assert.Single(method.GetCustomAttributes<EnableRateLimitingAttribute>());
            Assert.Equal("garage-write", limiter.PolicyName);
        });
    }

    [Fact]
    public void PersistenceModels_DoNotCollectPlateOrVin()
    {
        var persistedTypes = new[]
        {
            typeof(UserVehicle),
            typeof(MaintenanceRecord),
            typeof(MaintenanceRecordItem),
            typeof(MaintenanceReminder)
        };
        var forbiddenFragments = new[] { "vin", "plate", "licenseplate", "şasi", "plaka" };

        foreach (var property in persistedTypes.SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbiddenFragments,
                fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }
}
