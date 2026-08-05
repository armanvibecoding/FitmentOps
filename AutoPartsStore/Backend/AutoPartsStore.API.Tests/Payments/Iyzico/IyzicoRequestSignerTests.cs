using System.Text;
using System.Text.Json;
using AutoPartsStore.API.Payments.Iyzico;
using Xunit;

namespace AutoPartsStore.API.Tests.Payments.Iyzico;

public sealed class IyzicoRequestSignerTests
{
    private const string ApiKey = "sandbox-api-key";
    private const string SecretKey = "sandbox-secret-key";
    private const string RandomKey = "0123456789abcdef";
    private const string UriPath = "/payment/iyzipos/checkoutform/initialize/auth/ecom";
    private const string ExactBody = "{\"conversationId\":\"order-42\",\"price\":\"125.00\"}";
    private const string ExpectedSignature =
        "929d7103adb61fc9a3b73a532421b0543fd6873fe2d4a94191db4e1f8703ab63";
    private const string ExpectedBase64 =
        "YXBpS2V5OnNhbmRib3gtYXBpLWtleSZyYW5kb21LZXk6MDEyMzQ1Njc4OWFiY2RlZiZzaWduYXR1cmU6OTI5ZDcxMDNhZGI2MWZjOWEzYjczYTUzMjQyMWIwNTQzZmQ2ODczZmUyZDRhOTQxOTFkYjRlMWY4NzAzYWI2Mw==";

    [Fact]
    public void Sign_MatchesIndependentGoldenVector()
    {
        var result = IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey);

        Assert.Equal($"IYZWSv2 {ExpectedBase64}", result.ReadAuthorizationHeaderValue());
        Assert.Equal(RandomKey, result.ReadRandomKeyHeaderValue());

        var authorizationPlaintext = Encoding.UTF8.GetString(
            Convert.FromBase64String(result.ReadAuthorizationHeaderValue()["IYZWSv2 ".Length..]));
        Assert.Equal(
            $"apiKey:{ApiKey}&randomKey:{RandomKey}&signature:{ExpectedSignature}",
            authorizationPlaintext);
    }

    [Fact]
    public void Sign_UsesExactBodyBytes()
    {
        var original = IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey);
        var tampered = IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody.Replace(":\"125.00\"", ": \"125.00\"")),
            RandomKey);

        Assert.NotEqual(
            original.ReadAuthorizationHeaderValue(),
            tampered.ReadAuthorizationHeaderValue());
    }

    [Fact]
    public void GeneratedRandomKeys_AreIndependentLowercaseHexValues()
    {
        var first = IyzicoRequestSigner.GenerateRandomKey();
        var second = IyzicoRequestSigner.GenerateRandomKey();

        Assert.Equal(64, first.Length);
        Assert.All(first, character =>
            Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SignedHeaders_AreRedactedFromRoutineLoggingAndJsonSerialization()
    {
        var result = IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey);

        var serialized = JsonSerializer.Serialize(result);
        var logged = result.ToString();

        Assert.Equal("{}", serialized);
        Assert.DoesNotContain(ApiKey, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(RandomKey, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(ExpectedSignature, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(ExactBody, logged, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedHeaders_ApplyBothCorrelatedValuesToTheRequest()
    {
        var result = IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://sandbox-api.iyzipay.com{UriPath}");

        result.ApplyTo(request);

        Assert.Equal(
            result.ReadAuthorizationHeaderValue(),
            request.Headers.GetValues(IyzicoSignedRequestHeaders.AuthorizationHeaderName).Single());
        Assert.Equal(
            RandomKey,
            request.Headers.GetValues(IyzicoSignedRequestHeaders.RandomKeyHeaderName).Single());
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("", "secret")]
    [InlineData("api", null)]
    [InlineData("api", "")]
    public void Sign_RejectsMissingCredentials(string? apiKey, string? secretKey)
    {
        Assert.ThrowsAny<ArgumentException>(() => IyzicoRequestSigner.Sign(
            apiKey!,
            secretKey!,
            UriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey));
    }

    [Theory]
    [InlineData("payment/initialize")]
    [InlineData("//sandbox-api.iyzipay.com/payment/initialize")]
    [InlineData("https://sandbox-api.iyzipay.com/payment/initialize")]
    [InlineData("/payment/initialize?conversationId=order-42")]
    [InlineData("/payment/initialize#fragment")]
    [InlineData("/payment\\initialize")]
    [InlineData("/payment initialize")]
    [InlineData("/payment\ninitialize")]
    public void Sign_RejectsUnsafeOrNonPathSigningInputs(string uriPath)
    {
        Assert.Throws<ArgumentException>(() => IyzicoRequestSigner.Sign(
            ApiKey,
            SecretKey,
            uriPath,
            Encoding.UTF8.GetBytes(ExactBody),
            RandomKey));
    }
}
