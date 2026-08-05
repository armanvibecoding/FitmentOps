using AutoPartsStore.API.Payments.Iyzico;
using Xunit;

namespace AutoPartsStore.API.Tests.Payments.Iyzico;

public sealed class IyzicoResponseSignatureVerifierTests
{
    private const string SecretKey = "sandbox-secret-key";

    [Fact]
    public void Initialization_MatchesIndependentGoldenVector()
    {
        var verified = IyzicoResponseSignatureVerifier.VerifyCheckoutFormInitialization(
            SecretKey,
            "23bb388b1c71e116104c2b8a460380117822c0387a00a42aa268a962616f632c",
            "conversation-42",
            "checkout-token-42");

        Assert.True(verified);
    }

    [Fact]
    public void Retrieve_UsesDocumentedFieldOrderAndNormalizesDecimals()
    {
        const string signature =
            "d4039f7009ff84c754838befc327590d4b13f38e5d387550f4d2d2d99ac19453";

        var verified = IyzicoResponseSignatureVerifier.VerifyCheckoutFormRetrieve(
            SecretKey,
            signature,
            "SUCCESS",
            "payment-42",
            "TRY",
            "basket-42",
            "conversation-42",
            125.5000m,
            125.000m,
            "checkout-token-42");
        var wrongFieldOrder = IyzicoResponseSignatureVerifier.VerifyCheckoutFormRetrieve(
            SecretKey,
            signature,
            "payment-42",
            "SUCCESS",
            "TRY",
            "basket-42",
            "conversation-42",
            125.5000m,
            125.000m,
            "checkout-token-42");

        Assert.True(verified);
        Assert.False(wrongFieldOrder);
    }

    [Fact]
    public void Retrieve_DecimalFormattingNeverUsesScientificNotation()
    {
        var verified = IyzicoResponseSignatureVerifier.VerifyCheckoutFormRetrieve(
            SecretKey,
            "a06c86381cc77e9092356417cc1e63c38e504b0b4bc0091db227a226e180fc25",
            "SUCCESS",
            "payment-42",
            "TRY",
            "basket-42",
            "conversation-42",
            0.0000000000000000000000000001m,
            decimal.MaxValue,
            "checkout-token-42");

        Assert.True(verified);
    }

    [Fact]
    public void Retrieve_RejectsTamperedSignedField()
    {
        var verified = IyzicoResponseSignatureVerifier.VerifyCheckoutFormRetrieve(
            SecretKey,
            "d4039f7009ff84c754838befc327590d4b13f38e5d387550f4d2d2d99ac19453",
            "SUCCESS",
            "payment-42",
            "TRY",
            "basket-tampered",
            "conversation-42",
            125.5m,
            125m,
            "checkout-token-42");

        Assert.False(verified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("23BB388B1C71E116104C2B8A460380117822C0387A00A42AA268A962616F632C")]
    [InlineData("23bb388b1c71e116104c2b8a460380117822c0387a00a42aa268a962616f632")]
    public void Initialization_InvalidOrMissingHexFailsClosed(string? signature)
    {
        var verified = IyzicoResponseSignatureVerifier.VerifyCheckoutFormInitialization(
            SecretKey,
            signature,
            "conversation-42",
            "checkout-token-42");

        Assert.False(verified);
    }

    [Fact]
    public void Verification_RejectsMissingSecret()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            IyzicoResponseSignatureVerifier.VerifyCheckoutFormInitialization(
                "",
                "23bb388b1c71e116104c2b8a460380117822c0387a00a42aa268a962616f632c",
                "conversation-42",
                "checkout-token-42"));
    }
}
