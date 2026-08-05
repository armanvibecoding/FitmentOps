using AutoPartsStore.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class JwtServiceTests
{
    [Fact]
    public void Constructor_WithoutJwtKey_Throws()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new JwtService(configuration));

        Assert.Contains("Jwt:Key", exception.Message);
    }

    [Fact]
    public void Constructor_WithShortJwtKey_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "too-short"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new JwtService(configuration));

        Assert.Contains("at least 32 characters", exception.Message);
    }
}
