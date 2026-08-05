using System.Reflection;
using AutoPartsStore.API.Contracts;
using Xunit;

namespace AutoPartsStore.API.Tests.Contracts;

public sealed class HostedCheckoutContractsTests
{
    [Fact]
    public void RequestAndResponse_DoNotExposeCardOrHostedTokenFields()
    {
        var forbiddenFragments = new[]
        {
            "card",
            "pan",
            "cvv",
            "cvc",
            "expiry",
            "expiration",
            "token"
        };

        foreach (var contractType in new[]
                 {
                     typeof(CreateHostedCheckoutDto),
                     typeof(HostedCheckoutResponseDto)
                 })
        {
            var publicMembers = contractType
                .GetMembers(BindingFlags.Instance | BindingFlags.Public)
                .Select(member => member.Name)
                .ToArray();

            Assert.DoesNotContain(
                publicMembers,
                member => forbiddenFragments.Any(fragment =>
                    member.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Theory]
    [InlineData("http://shop.example/callback", "https://shop.example/return")]
    [InlineData("https://user:secret@shop.example/callback", "https://shop.example/return")]
    [InlineData("https://shop.example/callback", "javascript:alert(1)")]
    [InlineData("", "https://shop.example/return")]
    public void EndpointOptions_RejectUntrustedRedirectConfiguration(
        string callback,
        string returnUri)
    {
        var options = new HostedCheckoutEndpointOptions
        {
            CallbackUri = callback,
            ReturnUri = returnUri
        };

        Assert.False(options.TryGetTrustedUris(out _, out _));
    }

    [Fact]
    public void EndpointOptions_AcceptsHttpsUrisWithoutUserInfo()
    {
        var options = new HostedCheckoutEndpointOptions
        {
            CallbackUri = "https://api.example.test/payments/callback",
            ReturnUri = "https://shop.example.test/payment-result"
        };

        Assert.True(options.TryGetTrustedUris(out var callback, out var returnUri));
        Assert.Equal("api.example.test", callback.Host);
        Assert.Equal("shop.example.test", returnUri.Host);
    }
}
