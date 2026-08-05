using AutoPartsStore.API.Models;
using Xunit;

namespace AutoPartsStore.API.Tests.Models;

public sealed class WebSecurityOptionsTests
{
    [Fact]
    public void CorsOrigins_NormalizeAndDeduplicateTrustedOrigins()
    {
        var settings = new CorsSettings
        {
            AllowedOrigins =
            [
                "https://shop.example.test/",
                "https://SHOP.example.test",
                "http://localhost:5173"
            ]
        };

        Assert.Equal(
            ["https://shop.example.test", "http://localhost:5173"],
            settings.GetValidatedOrigins());
    }

    [Theory]
    [InlineData("http://shop.example.test")]
    [InlineData("https://user:password@shop.example.test")]
    [InlineData("https://shop.example.test/path")]
    [InlineData("https://shop.example.test?redirect=evil")]
    [InlineData("*")]
    public void CorsOrigins_RejectUnsafeValues(string origin)
    {
        var settings = new CorsSettings { AllowedOrigins = [origin] };

        Assert.Throws<InvalidOperationException>(() => settings.GetValidatedOrigins());
    }
}
