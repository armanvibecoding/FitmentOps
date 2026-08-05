using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Models;

public sealed class OnlinePaymentPersistenceModelsTests
{
    [Fact]
    public void PaymentAttemptDoesNotSerializeProviderSecrets()
    {
        var attempt = new PaymentAttempt
        {
            Provider = "ExampleProvider",
            IdempotencyKey = "attempt-idempotency-key",
            ConversationId = "conversation-id",
            ProviderPaymentId = "provider-payment-id",
            HostedPaymentToken = "hosted-payment-token",
            RedirectUrl = "https://provider.example/checkout/token"
        };

        var json = JsonSerializer.Serialize(attempt);

        Assert.DoesNotContain("attempt-idempotency-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-payment-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hosted-payment-token", json, StringComparison.Ordinal);
        Assert.Contains("https://provider.example/checkout/token", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceModelsContainNoCardDataFields()
    {
        var forbiddenTerms = new[]
        {
            "CardNumber", "Pan", "Cvv", "Cvc", "ExpiryMonth", "ExpiryYear"
        };
        var modelTypes = new[]
        {
            typeof(PaymentAttempt), typeof(PaymentTransaction), typeof(Refund),
            typeof(OutboxMessage)
        };

        var forbiddenProperties = modelTypes
            .SelectMany(type => type.GetProperties())
            .Where(property => forbiddenTerms.Any(term =>
                property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
            .ToArray();

        Assert.Empty(forbiddenProperties);
    }

    [Fact]
    public void RetryAndProviderIdentifiersHaveUniqueDatabaseContracts()
    {
        AssertUniqueIndex<PaymentAttempt>(nameof(PaymentAttempt.IdempotencyKey));
        AssertUniqueIndex<PaymentAttempt>(
            nameof(PaymentAttempt.Provider),
            nameof(PaymentAttempt.ConversationId));
        AssertUniqueIndex<PaymentTransaction>(
            nameof(PaymentTransaction.Provider),
            nameof(PaymentTransaction.ProviderTransactionId));
        AssertUniqueIndex<Refund>(nameof(Refund.IdempotencyKey));
        AssertUniqueIndex<OutboxMessage>(nameof(OutboxMessage.EventId));
    }

    [Fact]
    public void SensitivePersistencePropertiesAreJsonIgnored()
    {
        AssertJsonIgnored<PaymentAttempt>(nameof(PaymentAttempt.HostedPaymentToken));
        AssertJsonIgnored<PaymentAttempt>(nameof(PaymentAttempt.IdempotencyKey));
        AssertJsonIgnored<PaymentTransaction>(nameof(PaymentTransaction.ProviderTransactionId));
        AssertJsonIgnored<Refund>(nameof(Refund.IdempotencyKey));
        AssertJsonIgnored<Refund>(nameof(Refund.ProviderRefundId));
        AssertJsonIgnored<Refund>(nameof(Refund.ConcurrencyToken));
        AssertJsonIgnored<OutboxMessage>(nameof(OutboxMessage.Payload));
        AssertJsonIgnored<OutboxMessage>(nameof(OutboxMessage.LastError));
    }

    private static void AssertUniqueIndex<T>(params string[] propertyNames)
    {
        var matchingIndex = typeof(T)
            .GetCustomAttributes<IndexAttribute>()
            .SingleOrDefault(index => index.PropertyNames.SequenceEqual(propertyNames));

        Assert.NotNull(matchingIndex);
        Assert.True(matchingIndex.IsUnique);
    }

    private static void AssertJsonIgnored<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.NotNull(property.GetCustomAttribute<JsonIgnoreAttribute>());
    }
}
