using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public enum PaymentStateTransitionOutcome
{
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record PaymentStateTransitionResult(
    PaymentStateTransitionOutcome Outcome,
    Payment? Payment = null,
    string? Message = null);

public sealed class PaymentStateService
{
    private const int MaxProviderLength = 50;
    private const int MaxProviderPaymentIdLength = 200;
    private const int MaxFailureCodeLength = 100;

    private readonly AutoPartsDbContext _context;

    public PaymentStateService(AutoPartsDbContext context)
    {
        _context = context;
    }

    public async Task<PaymentStateTransitionResult> ConfirmPaidAsync(
        int paymentId,
        string expectedProvider,
        string providerPaymentId,
        decimal amount,
        string currency,
        DateTimeOffset paidAt,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateConfirmation(
            paymentId,
            expectedProvider,
            providerPaymentId,
            amount,
            currency,
            paidAt);
        if (validationError != null)
        {
            return InvalidRequest(validationError);
        }

        var normalizedProvider = expectedProvider.Trim();
        var normalizedProviderPaymentId = providerPaymentId.Trim();
        var payment = await _context.Payments.FindAsync([paymentId], cancellationToken);
        if (payment == null)
        {
            return new PaymentStateTransitionResult(PaymentStateTransitionOutcome.NotFound);
        }

        if (!string.Equals(payment.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(payment, "Ödeme sağlayıcısı beklenen sağlayıcıyla eşleşmiyor.");
        }

        if (payment.Amount != amount ||
            !string.Equals(payment.Currency, currency, StringComparison.Ordinal))
        {
            return Conflict(payment, "Sağlayıcı tutarı veya para birimi ödeme kaydıyla eşleşmiyor.");
        }

        if (payment.Status == PaymentStatuses.Paid)
        {
            if (string.Equals(
                    payment.ProviderPaymentId,
                    normalizedProviderPaymentId,
                    StringComparison.Ordinal))
            {
                return new PaymentStateTransitionResult(
                    PaymentStateTransitionOutcome.Replayed,
                    payment);
            }

            return Conflict(payment, "Ödeme daha önce farklı bir sağlayıcı işlem kimliğiyle onaylanmış.");
        }

        if (payment.Status != PaymentStatuses.Pending)
        {
            return Conflict(
                payment,
                $"{payment.Status} durumundaki ödeme Paid durumuna geçirilemez.");
        }

        if (!string.IsNullOrEmpty(payment.ProviderPaymentId) &&
            !string.Equals(
                payment.ProviderPaymentId,
                normalizedProviderPaymentId,
                StringComparison.Ordinal))
        {
            return Conflict(payment, "Bekleyen ödemede farklı bir sağlayıcı işlem kimliği kayıtlı.");
        }

        payment.ProviderPaymentId = normalizedProviderPaymentId;
        payment.Status = PaymentStatuses.Paid;
        payment.PaidAt = paidAt.UtcDateTime;
        payment.UpdatedAt = paidAt.UtcDateTime;
        payment.ConcurrencyToken = Guid.NewGuid();

        return await SaveTransitionAsync(payment, cancellationToken);
    }

    public async Task<PaymentStateTransitionResult> MarkFailedAsync(
        int paymentId,
        string expectedProvider,
        string failureCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateFailure(
            paymentId,
            expectedProvider,
            failureCode,
            failedAt);
        if (validationError != null)
        {
            return InvalidRequest(validationError);
        }

        var normalizedProvider = expectedProvider.Trim();
        var payment = await _context.Payments.FindAsync([paymentId], cancellationToken);
        if (payment == null)
        {
            return new PaymentStateTransitionResult(PaymentStateTransitionOutcome.NotFound);
        }

        if (!string.Equals(payment.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(payment, "Ödeme sağlayıcısı beklenen sağlayıcıyla eşleşmiyor.");
        }

        if (payment.Status != PaymentStatuses.Pending)
        {
            return Conflict(
                payment,
                $"{payment.Status} durumundaki ödeme Failed durumuna geçirilemez.");
        }

        payment.Status = PaymentStatuses.Failed;
        payment.FailureCode = failureCode.Trim();
        payment.UpdatedAt = failedAt.UtcDateTime;
        payment.ConcurrencyToken = Guid.NewGuid();

        return await SaveTransitionAsync(payment, cancellationToken);
    }

    private async Task<PaymentStateTransitionResult> SaveTransitionAsync(
        Payment payment,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new PaymentStateTransitionResult(
                PaymentStateTransitionOutcome.Updated,
                payment);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.Entry(payment).State = EntityState.Detached;

            return new PaymentStateTransitionResult(
                PaymentStateTransitionOutcome.Conflict,
                Message: "Ödeme başka bir işlem tarafından güncellendi; sağlayıcı olayı yeniden değerlendirilmelidir.");
        }
        catch (DbUpdateException)
        {
            var provider = payment.Provider;
            var providerPaymentId = payment.ProviderPaymentId;
            _context.Entry(payment).State = EntityState.Detached;

            var providerIdentifierConflict = providerPaymentId != null &&
                await _context.Payments
                    .AsNoTracking()
                    .AnyAsync(
                        candidate =>
                            candidate.Id != payment.Id &&
                            candidate.Provider == provider &&
                            candidate.ProviderPaymentId == providerPaymentId,
                        cancellationToken);
            if (providerIdentifierConflict)
            {
                return new PaymentStateTransitionResult(
                    PaymentStateTransitionOutcome.Conflict,
                    Message: "Sağlayıcı ödeme kimliği başka bir yerel ödeme tarafından kullanılmış.");
            }

            throw;
        }
    }

    private static string? ValidateConfirmation(
        int paymentId,
        string expectedProvider,
        string providerPaymentId,
        decimal amount,
        string currency,
        DateTimeOffset paidAt)
    {
        var commonValidationError = ValidateCommon(paymentId, expectedProvider);
        if (commonValidationError != null)
        {
            return commonValidationError;
        }

        if (string.IsNullOrWhiteSpace(providerPaymentId) ||
            providerPaymentId.Trim().Length > MaxProviderPaymentIdLength)
        {
            return $"Sağlayıcı ödeme kimliği 1 ile {MaxProviderPaymentIdLength} karakter arasında olmalıdır.";
        }

        if (amount <= 0)
        {
            return "Ödeme tutarı sıfırdan büyük olmalıdır.";
        }

        if (!IsValidCurrency(currency))
        {
            return "Para birimi üç büyük ASCII harften oluşmalıdır.";
        }

        if (paidAt == default)
        {
            return "Ödeme zamanı belirtilmelidir.";
        }

        return null;
    }

    private static string? ValidateFailure(
        int paymentId,
        string expectedProvider,
        string failureCode,
        DateTimeOffset failedAt)
    {
        var commonValidationError = ValidateCommon(paymentId, expectedProvider);
        if (commonValidationError != null)
        {
            return commonValidationError;
        }

        if (string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Trim().Length > MaxFailureCodeLength)
        {
            return $"Hata kodu 1 ile {MaxFailureCodeLength} karakter arasında olmalıdır.";
        }

        if (failedAt == default)
        {
            return "Başarısızlık zamanı belirtilmelidir.";
        }

        return null;
    }

    private static string? ValidateCommon(int paymentId, string expectedProvider)
    {
        if (paymentId <= 0)
        {
            return "Ödeme kimliği pozitif olmalıdır.";
        }

        if (string.IsNullOrWhiteSpace(expectedProvider) ||
            expectedProvider.Trim().Length > MaxProviderLength)
        {
            return $"Sağlayıcı adı 1 ile {MaxProviderLength} karakter arasında olmalıdır.";
        }

        return null;
    }

    private static bool IsValidCurrency(string currency)
    {
        return currency is { Length: 3 } && currency.All(character => character is >= 'A' and <= 'Z');
    }

    private static PaymentStateTransitionResult InvalidRequest(string message)
    {
        return new PaymentStateTransitionResult(
            PaymentStateTransitionOutcome.InvalidRequest,
            Message: message);
    }

    private static PaymentStateTransitionResult Conflict(Payment payment, string message)
    {
        return new PaymentStateTransitionResult(
            PaymentStateTransitionOutcome.Conflict,
            payment,
            message);
    }
}
