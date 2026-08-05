using System.Security.Cryptography;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public sealed class PaymentEventService
{
    private const int MaxProviderLength = 50;
    private const int MaxProviderEventIdLength = 200;
    private const int MaxEventTypeLength = 100;
    private const int MaxPayloadBytes = 256 * 1024;

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public PaymentEventService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PaymentEventRegistrationResult> RegisterAsync(
        string provider,
        string providerEventId,
        string eventType,
        ReadOnlyMemory<byte> payload,
        int? paymentId = null,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(
            provider,
            providerEventId,
            eventType,
            payload,
            paymentId);
        if (validationError != null)
        {
            return new PaymentEventRegistrationResult(
                PaymentEventRegistrationOutcome.InvalidRequest,
                Message: validationError);
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var normalizedProviderEventId = providerEventId.Trim();
        var normalizedEventType = eventType.Trim();
        var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.Span));

        var existingEvent = await FindAsync(
            normalizedProvider,
            normalizedProviderEventId,
            cancellationToken);
        if (existingEvent != null)
        {
            return ResolveExisting(existingEvent, payloadSha256);
        }

        var paymentEvent = PaymentEvent.CreateReceived(
            normalizedProvider,
            normalizedProviderEventId,
            normalizedEventType,
            payloadSha256,
            paymentId,
            _timeProvider.GetUtcNow().UtcDateTime);

        _context.Set<PaymentEvent>().Add(paymentEvent);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new PaymentEventRegistrationResult(
                PaymentEventRegistrationOutcome.Registered,
                paymentEvent);
        }
        catch (DbUpdateException)
        {
            _context.Entry(paymentEvent).State = EntityState.Detached;

            // Another request may have inserted the same provider event after the
            // first lookup. The unique database index remains the final race guard.
            existingEvent = await FindAsync(
                normalizedProvider,
                normalizedProviderEventId,
                cancellationToken);
            if (existingEvent != null)
            {
                return ResolveExisting(existingEvent, payloadSha256);
            }

            throw;
        }
    }

    private Task<PaymentEvent?> FindAsync(
        string provider,
        string providerEventId,
        CancellationToken cancellationToken)
    {
        return _context.Set<PaymentEvent>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                paymentEvent =>
                    paymentEvent.Provider == provider &&
                    paymentEvent.ProviderEventId == providerEventId,
                cancellationToken);
    }

    private static PaymentEventRegistrationResult ResolveExisting(
        PaymentEvent existingEvent,
        string payloadSha256)
    {
        if (CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(existingEvent.PayloadSha256),
                Convert.FromHexString(payloadSha256)))
        {
            return new PaymentEventRegistrationResult(
                PaymentEventRegistrationOutcome.Replayed,
                existingEvent);
        }

        return new PaymentEventRegistrationResult(
            PaymentEventRegistrationOutcome.Conflict,
            existingEvent,
            "Aynı sağlayıcı olay kimliği farklı bir içerikle daha önce kaydedilmiş.");
    }

    private static string? Validate(
        string provider,
        string providerEventId,
        string eventType,
        ReadOnlyMemory<byte> payload,
        int? paymentId)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Trim().Length > MaxProviderLength)
        {
            return $"Sağlayıcı adı 1 ile {MaxProviderLength} karakter arasında olmalıdır.";
        }

        if (string.IsNullOrWhiteSpace(providerEventId) ||
            providerEventId.Trim().Length > MaxProviderEventIdLength)
        {
            return $"Sağlayıcı olay kimliği 1 ile {MaxProviderEventIdLength} karakter arasında olmalıdır.";
        }

        if (string.IsNullOrWhiteSpace(eventType) || eventType.Trim().Length > MaxEventTypeLength)
        {
            return $"Olay türü 1 ile {MaxEventTypeLength} karakter arasında olmalıdır.";
        }

        if (payload.IsEmpty || payload.Length > MaxPayloadBytes)
        {
            return $"Webhook gövdesi 1 ile {MaxPayloadBytes} byte arasında olmalıdır.";
        }

        if (paymentId is <= 0)
        {
            return "Ödeme kimliği pozitif olmalıdır.";
        }

        return null;
    }
}
