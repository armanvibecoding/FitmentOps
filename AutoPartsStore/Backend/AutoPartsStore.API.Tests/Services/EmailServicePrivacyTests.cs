using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class EmailServicePrivacyTests
{
    [Fact]
    public async Task MissingConfiguration_DoesNotLogRecipientSubjectOrBody()
    {
        var logger = new CapturingLogger<EmailService>();
        var service = new EmailService(
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            logger);
        var order = new Order
        {
            OrderNumber = "PRIVATE-ORDER-123",
            CustomerName = "Private Customer",
            CustomerEmail = "private@example.test",
            CustomerPhone = "+905550000000",
            ShippingAddress = "Private shipping address",
            City = "Istanbul",
            PostalCode = "34000",
            TotalAmount = 10m
        };

        await service.SendOrderConfirmationEmail(order);

        var logs = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(order.CustomerEmail, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(order.CustomerName, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(order.CustomerPhone, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(order.ShippingAddress, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(order.OrderNumber, logs, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
