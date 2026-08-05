using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class PaymentsControllerTests
{
    [Fact]
    public void Capabilities_FailClosedUntilHostedProviderIsConfigured()
    {
        var controller = new PaymentsController(new DisabledPaymentGateway());

        var result = controller.GetCapabilities();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var capabilities = Assert.IsType<PaymentCapabilitiesDto>(ok.Value);
        Assert.True(capabilities.PayAtDelivery);
        Assert.False(capabilities.OnlineCard);
        Assert.True(capabilities.HostedCardEntryOnly);
        Assert.Null(capabilities.OnlineProvider);
    }

    [Fact]
    public async Task DisabledCallback_FailsBeforeReadingSensitiveBody()
    {
        var controller = CreateDisabledControllerWithUnreadableBody();

        var result = await controller.ConfirmCallback(CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task DisabledWebhook_FailsBeforeReadingUntrustedBody()
    {
        var controller = CreateDisabledControllerWithUnreadableBody();

        var result = await controller.HandleWebhook("disabled", CancellationToken.None);

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Theory]
    [InlineData(nameof(PaymentsController.ConfirmCallback), "payment-callback")]
    [InlineData(nameof(PaymentsController.HandleWebhook), "payment-webhook")]
    public void PublicPaymentMutationEndpoints_HaveNamedRateLimits(
        string methodName,
        string expectedPolicy)
    {
        var method = typeof(PaymentsController).GetMethod(methodName);
        var rateLimit = Assert.Single(
            method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
                .Cast<EnableRateLimitingAttribute>());

        Assert.Equal(expectedPolicy, rateLimit.PolicyName);
    }

    private static PaymentsController CreateDisabledControllerWithUnreadableBody()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new ThrowOnReadStream();
        return new PaymentsController(new DisabledPaymentGateway())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Body must not be read.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
