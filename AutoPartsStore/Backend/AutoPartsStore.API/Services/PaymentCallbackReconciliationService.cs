using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public enum PaymentReconciliationOutcome
{
    Succeeded,
    Replayed,
    PendingReconciliation,
    Failed,
    ProviderDisabled,
    VerificationFailed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record PaymentCallbackCommand(
    int PaymentId,
    [property: JsonIgnore] string HostedPaymentToken)
{
    public override string ToString() => $"{nameof(PaymentCallbackCommand)} {{ Sensitive = true }}";
}

/// <summary>
/// Contains only public-safe state. Provider identifiers, callback tokens,
/// webhook bodies and customer data are deliberately excluded.
/// </summary>
public sealed record PaymentReconciliationResult(
    PaymentReconciliationOutcome Outcome,
    string? PaymentStatus = null,
    string? AttemptStatus = null,
    string? Message = null);

public sealed class PaymentCallbackReconciliationService
{
    public const int MaxWebhookBodyBytes = 256 * 1024;
    public const int MaxCallbackBodyBytes = 4 * 1024;
    public const int MaxHostedPaymentTokenLength = 500;

    private readonly AutoPartsDbContext _context;
    private readonly IPaymentGateway _paymentGateway;
    private readonly PaymentEventService _paymentEventService;
    private readonly PaymentStateService _paymentStateService;
    private readonly OrderLifecycleService _orderLifecycleService;
    private readonly TimeProvider _timeProvider;

    public PaymentCallbackReconciliationService(
        AutoPartsDbContext context,
        IPaymentGateway paymentGateway,
        PaymentEventService paymentEventService,
        PaymentStateService paymentStateService,
        OrderLifecycleService? orderLifecycleService = null,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _paymentGateway = paymentGateway;
        _paymentEventService = paymentEventService;
        _paymentStateService = paymentStateService;
        _orderLifecycleService = orderLifecycleService ?? new OrderLifecycleService(context);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsProviderEnabled => _paymentGateway.IsEnabled;

    public async Task<PaymentReconciliationResult> ConfirmCallbackAsync(
        PaymentCallbackCommand command,
        CancellationToken cancellationToken = default)
    {
        // This gate intentionally precedes command inspection and all database I/O.
        if (!_paymentGateway.IsEnabled)
        {
            return ProviderDisabled();
        }

        ArgumentNullException.ThrowIfNull(command);
        if (command.PaymentId <= 0 ||
            string.IsNullOrWhiteSpace(command.HostedPaymentToken) ||
            command.HostedPaymentToken.Length > MaxHostedPaymentTokenLength)
        {
            return Invalid("The callback request is invalid.");
        }

        var local = await LoadByPaymentIdAsync(command.PaymentId, cancellationToken);
        if (local == null)
        {
            return new PaymentReconciliationResult(PaymentReconciliationOutcome.NotFound);
        }

        var attempt = FindAttemptByToken(local.Payment, command.HostedPaymentToken);
        if (attempt == null)
        {
            return Conflict(local.Payment, null, "The callback does not match a local payment attempt.");
        }

        var expected = BuildExpectedSnapshot(local.Payment, attempt);
        PaymentConfirmationResult confirmation;
        try
        {
            confirmation = await _paymentGateway.ConfirmAsync(
                new ConfirmPaymentCommand(expected, command.HostedPaymentToken),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkAttemptUnknownAsync(
                local.Payment.Id,
                attempt.Id,
                PaymentGatewayErrorCode.ProviderUnavailable.ToString(),
                CancellationToken.None);
            return Pending(local.Payment.Status, PaymentAttemptStatuses.Unknown);
        }
        catch
        {
            await MarkAttemptUnknownAsync(
                local.Payment.Id,
                attempt.Id,
                PaymentGatewayErrorCode.ProviderUnavailable.ToString(),
                cancellationToken);
            return Pending(local.Payment.Status, PaymentAttemptStatuses.Unknown);
        }

        if (IsDefiniteFailure(confirmation))
        {
            if (confirmation.Payment != null &&
                !MatchesExpected(expected, confirmation.Payment, requireProviderPaymentId: false))
            {
                await MarkAttemptUnknownAsync(
                    local.Payment.Id,
                    attempt.Id,
                    PaymentGatewayErrorCode.Conflict.ToString(),
                    cancellationToken);
                return Conflict(
                    local.Payment,
                    attempt,
                    "Provider payment details did not match the local payment.");
            }

            return await ApplyDefiniteFailureAsync(
                local.Payment.Id,
                attempt.Id,
                NormalizeFailureCode(confirmation.ErrorCode),
                replayedEvent: false,
                cancellationToken);
        }

        if (confirmation.Payment == null ||
            confirmation.Payment.Status != GatewayPaymentStatus.Paid ||
            !MatchesExpected(expected, confirmation.Payment, requireProviderPaymentId: true))
        {
            var mismatch = confirmation.Payment?.Status == GatewayPaymentStatus.Paid;
            await MarkAttemptUnknownAsync(
                local.Payment.Id,
                attempt.Id,
                mismatch
                    ? PaymentGatewayErrorCode.Conflict.ToString()
                    : NormalizeFailureCode(confirmation.ErrorCode),
                cancellationToken);
            return mismatch
                ? Conflict(
                    local.Payment,
                    attempt,
                    "Provider payment details did not match the local payment.")
                : Pending(local.Payment.Status, PaymentAttemptStatuses.Unknown);
        }

        return await ApplyPaidAsync(
            local.Payment.Id,
            attempt.Id,
            confirmation.Payment,
            replayedEvent: false,
            cancellationToken);
    }

    public async Task<PaymentReconciliationResult> HandleWebhookAsync(
        ReadOnlyMemory<byte> rawBody,
        ImmutableDictionary<string, ImmutableArray<string>> headers,
        CancellationToken cancellationToken = default)
    {
        // This gate intentionally precedes request validation, verification and DB I/O.
        if (!_paymentGateway.IsEnabled)
        {
            return ProviderDisabled();
        }

        if (rawBody.IsEmpty || rawBody.Length > MaxWebhookBodyBytes || headers == null)
        {
            return Invalid("The webhook request is invalid.");
        }

        PaymentWebhookVerificationResult verification;
        try
        {
            verification = await _paymentGateway.VerifyWebhookAsync(
                new VerifyPaymentWebhookCommand(rawBody, headers),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PaymentReconciliationResult(
                PaymentReconciliationOutcome.PendingReconciliation,
                Message: "Webhook verification is temporarily unavailable.");
        }
        catch
        {
            return new PaymentReconciliationResult(
                PaymentReconciliationOutcome.PendingReconciliation,
                Message: "Webhook verification is temporarily unavailable.");
        }

        // Nothing durable is written until the provider has authenticated the raw request.
        if (verification.Outcome != PaymentGatewayOutcome.Succeeded)
        {
            return new PaymentReconciliationResult(
                verification.ErrorCode == PaymentGatewayErrorCode.ProviderUnavailable
                    ? PaymentReconciliationOutcome.PendingReconciliation
                    : PaymentReconciliationOutcome.VerificationFailed,
                Message: verification.ErrorCode == PaymentGatewayErrorCode.ProviderUnavailable
                    ? "Webhook verification is temporarily unavailable."
                    : "Webhook verification failed.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProviderEventId) ||
            string.IsNullOrWhiteSpace(verification.EventType) ||
            verification.Payment == null ||
            string.IsNullOrWhiteSpace(verification.Payment.OrderNumber))
        {
            return Invalid("The verified webhook payload is incomplete.");
        }

        var local = await LoadByOrderNumberAsync(
            verification.Payment.OrderNumber,
            cancellationToken);
        if (local == null)
        {
            return new PaymentReconciliationResult(PaymentReconciliationOutcome.NotFound);
        }

        var attempt = FindWebhookAttempt(local.Payment, verification.Payment);
        if (attempt == null)
        {
            return Conflict(local.Payment, null, "No local payment attempt matches the webhook.");
        }

        var expected = BuildExpectedSnapshot(local.Payment, attempt);
        if (!MatchesExpected(expected, verification.Payment, requireProviderPaymentId: false))
        {
            return Conflict(local.Payment, attempt, "Provider payment details did not match the local payment.");
        }

        var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        try
        {
            var registration = await _paymentEventService.RegisterAsync(
                _paymentGateway.ProviderName,
                verification.ProviderEventId,
                verification.EventType,
                rawBody,
                local.Payment.Id,
                cancellationToken);
            if (registration.Outcome == PaymentEventRegistrationOutcome.InvalidRequest)
            {
                return Invalid("The verified webhook metadata is invalid.");
            }

            if (registration.Outcome == PaymentEventRegistrationOutcome.Conflict)
            {
                return Conflict(local.Payment, attempt, "The provider event identifier conflicts with an earlier event.");
            }

            var replayedEvent = registration.Outcome == PaymentEventRegistrationOutcome.Replayed;
            PaymentReconciliationResult result;
            if (verification.Payment.Status == GatewayPaymentStatus.Paid)
            {
                if (!MatchesExpected(expected, verification.Payment, requireProviderPaymentId: true))
                {
                    result = Conflict(
                        local.Payment,
                        attempt,
                        "Provider payment details did not match the local payment.");
                }
                else
                {
                    result = await ApplyPaidCoreAsync(
                        local.Payment,
                        attempt,
                        verification.Payment,
                        replayedEvent,
                        cancellationToken);
                }
            }
            else if (verification.Payment.Status is
                GatewayPaymentStatus.Failed or GatewayPaymentStatus.Cancelled)
            {
                result = await ApplyDefiniteFailureCoreAsync(
                    local.Payment,
                    attempt,
                    verification.Payment.Status.ToString(),
                    replayedEvent,
                    cancellationToken);
            }
            else
            {
                await SetAttemptUnknownAsync(
                    attempt,
                    verification.Payment.Status.ToString(),
                    cancellationToken);
                result = Pending(local.Payment.Status, attempt.Status);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<PaymentReconciliationResult> ApplyPaidAsync(
        int paymentId,
        long attemptId,
        ProviderPaymentSnapshot snapshot,
        bool replayedEvent,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        try
        {
            var local = await LoadByPaymentIdAsync(paymentId, cancellationToken);
            var attempt = local?.Payment.Attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
            if (local == null || attempt == null)
            {
                return new PaymentReconciliationResult(PaymentReconciliationOutcome.NotFound);
            }

            if (!MatchesExpected(
                    BuildExpectedSnapshot(local.Payment, attempt),
                    snapshot,
                    requireProviderPaymentId: true))
            {
                return Conflict(local.Payment, attempt, "Provider payment details did not match the local payment.");
            }

            var result = await ApplyPaidCoreAsync(
                local.Payment,
                attempt,
                snapshot,
                replayedEvent,
                cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<PaymentReconciliationResult> ApplyPaidCoreAsync(
        Payment payment,
        PaymentAttempt attempt,
        ProviderPaymentSnapshot snapshot,
        bool replayedEvent,
        CancellationToken cancellationToken)
    {
        var providerPaymentId = snapshot.ProviderPaymentId!.Trim();
        var existingTransaction = await _context.PaymentTransactions
            .SingleOrDefaultAsync(candidate =>
                candidate.Provider == payment.Provider &&
                candidate.ProviderTransactionId == providerPaymentId,
                cancellationToken);
        if (existingTransaction != null && existingTransaction.PaymentId != payment.Id)
        {
            return Conflict(payment, attempt, "The provider payment identifier belongs to another local payment.");
        }

        var paidAt = _timeProvider.GetUtcNow();
        var transition = await _paymentStateService.ConfirmPaidAsync(
            payment.Id,
            _paymentGateway.ProviderName,
            providerPaymentId,
            FromMinorUnits(snapshot.AmountMinor!.Value),
            snapshot.Currency!,
            paidAt,
            cancellationToken);
        if (transition.Outcome is not PaymentStateTransitionOutcome.Updated and
            not PaymentStateTransitionOutcome.Replayed)
        {
            return TerminalOrConflict(payment, attempt);
        }

        attempt.ProviderPaymentId = providerPaymentId;
        attempt.Status = PaymentAttemptStatuses.Succeeded;
        attempt.ProviderResultCode = GatewayPaymentStatus.Paid.ToString();
        attempt.FailureCode = null;
        attempt.UpdatedAt = paidAt.UtcDateTime;
        attempt.CompletedAt ??= paidAt.UtcDateTime;

        // The current provider snapshot has one payment-level transaction ID.
        // It can be represented without inventing provider identifiers only for
        // a single-line order; split settlements require richer adapter data.
        if (existingTransaction == null && payment.Order.OrderItems.Count == 1)
        {
            _context.PaymentTransactions.Add(new PaymentTransaction
            {
                PaymentId = payment.Id,
                OrderItemId = payment.Order.OrderItems.Single().Id,
                Provider = payment.Provider,
                ProviderTransactionId = providerPaymentId,
                PaidAmount = payment.Amount,
                RefundedAmount = 0m,
                Currency = payment.Currency,
                CreatedAt = paidAt.UtcDateTime,
                UpdatedAt = paidAt.UtcDateTime
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new PaymentReconciliationResult(
            replayedEvent || transition.Outcome == PaymentStateTransitionOutcome.Replayed
                ? PaymentReconciliationOutcome.Replayed
                : PaymentReconciliationOutcome.Succeeded,
            transition.Payment?.Status ?? payment.Status,
            attempt.Status);
    }

    private async Task<PaymentReconciliationResult> ApplyDefiniteFailureAsync(
        int paymentId,
        long attemptId,
        string failureCode,
        bool replayedEvent,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        try
        {
            var local = await LoadByPaymentIdAsync(paymentId, cancellationToken);
            var attempt = local?.Payment.Attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
            if (local == null || attempt == null)
            {
                return new PaymentReconciliationResult(PaymentReconciliationOutcome.NotFound);
            }

            var result = await ApplyDefiniteFailureCoreAsync(
                local.Payment,
                attempt,
                failureCode,
                replayedEvent,
                cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<PaymentReconciliationResult> ApplyDefiniteFailureCoreAsync(
        Payment payment,
        PaymentAttempt attempt,
        string failureCode,
        bool replayedEvent,
        CancellationToken cancellationToken)
    {
        if (payment.Status != PaymentStatuses.Pending)
        {
            return TerminalOrConflict(payment, attempt);
        }

        var failedAt = _timeProvider.GetUtcNow();
        var transition = await _paymentStateService.MarkFailedAsync(
            payment.Id,
            _paymentGateway.ProviderName,
            failureCode,
            failedAt,
            cancellationToken);
        if (transition.Outcome != PaymentStateTransitionOutcome.Updated)
        {
            return TerminalOrConflict(payment, attempt);
        }

        var cancellation = await _orderLifecycleService.UpdateOrderStatusAsync(
            payment.OrderId,
            OrderStatuses.Cancelled,
            cancellationToken);
        if (cancellation.Outcome is not OrderLifecycleOutcome.Updated and
            not OrderLifecycleOutcome.Unchanged)
        {
            throw new InvalidOperationException(
                "Failed hosted payment could not atomically cancel its order.");
        }

        // Order cancellation takes the aggregate lock and clears tracked state.
        // Reload the attempt before completing the same outer transaction.
        var currentAttempt = await _context.PaymentAttempts
            .SingleAsync(candidate => candidate.Id == attempt.Id, cancellationToken);
        currentAttempt.Status = PaymentAttemptStatuses.Failed;
        currentAttempt.ProviderResultCode = failureCode;
        currentAttempt.FailureCode = failureCode;
        currentAttempt.UpdatedAt = failedAt.UtcDateTime;
        currentAttempt.CompletedAt ??= failedAt.UtcDateTime;
        await _context.SaveChangesAsync(cancellationToken);
        return new PaymentReconciliationResult(
            replayedEvent
                ? PaymentReconciliationOutcome.Replayed
                : PaymentReconciliationOutcome.Failed,
            transition.Payment?.Status ?? payment.Status,
            currentAttempt.Status);
    }

    private async Task MarkAttemptUnknownAsync(
        int paymentId,
        long attemptId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var payment = await _context.Payments
            .Include(candidate => candidate.Attempts)
            .SingleOrDefaultAsync(candidate => candidate.Id == paymentId, cancellationToken);
        var attempt = payment?.Attempts.SingleOrDefault(candidate => candidate.Id == attemptId);
        if (attempt != null)
        {
            await SetAttemptUnknownAsync(attempt, failureCode, cancellationToken);
        }
    }

    private async Task SetAttemptUnknownAsync(
        PaymentAttempt attempt,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (attempt.Status is PaymentAttemptStatuses.Succeeded or PaymentAttemptStatuses.Failed)
        {
            return;
        }

        attempt.Status = PaymentAttemptStatuses.Unknown;
        attempt.FailureCode = NormalizeStoredCode(failureCode);
        attempt.ProviderResultCode = attempt.FailureCode;
        attempt.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private Task<LocalPayment?> LoadByPaymentIdAsync(
        int paymentId,
        CancellationToken cancellationToken) =>
        PaymentQuery()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => new LocalPayment(payment))
            .SingleOrDefaultAsync(cancellationToken);

    private Task<LocalPayment?> LoadByOrderNumberAsync(
        string orderNumber,
        CancellationToken cancellationToken) =>
        PaymentQuery()
            .Where(payment => payment.Order.OrderNumber == orderNumber)
            .Select(payment => new LocalPayment(payment))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<Payment> PaymentQuery() =>
        _context.Payments
            .Include(payment => payment.Order)
                .ThenInclude(order => order.OrderItems)
                    .ThenInclude(item => item.Product)
            .Include(payment => payment.Attempts)
            .Include(payment => payment.Transactions);

    private PaymentAttempt? FindAttemptByToken(Payment payment, string token) =>
        payment.Attempts
            .Where(attempt =>
                string.Equals(attempt.Provider, _paymentGateway.ProviderName, StringComparison.OrdinalIgnoreCase) &&
                attempt.HostedPaymentToken != null &&
                FixedTimeEquals(attempt.HostedPaymentToken, token))
            .OrderByDescending(attempt => attempt.CreatedAt)
            .FirstOrDefault();

    private PaymentAttempt? FindWebhookAttempt(
        Payment payment,
        ProviderPaymentSnapshot snapshot) =>
        payment.Attempts
            .Where(attempt => string.Equals(
                attempt.Provider,
                _paymentGateway.ProviderName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(attempt =>
                !string.IsNullOrWhiteSpace(snapshot.ProviderPaymentId) &&
                string.Equals(
                    attempt.ProviderPaymentId,
                    snapshot.ProviderPaymentId,
                    StringComparison.Ordinal))
            .ThenByDescending(attempt => attempt.CreatedAt)
            .FirstOrDefault();

    private static PaymentRequestSnapshot BuildExpectedSnapshot(
        Payment payment,
        PaymentAttempt attempt)
    {
        var basket = payment.Order.OrderItems
            .OrderBy(item => item.ProductId)
            .Select(item => new PaymentBasketItemSnapshot(
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.ProductId.ToString(CultureInfo.InvariantCulture),
                item.Product?.Name ?? $"Product {item.ProductId}",
                item.Quantity,
                ToMinorUnits(item.Price),
                checked(ToMinorUnits(item.Price) * item.Quantity)))
            .ToImmutableArray();
        return new PaymentRequestSnapshot(
            payment.Id,
            attempt.ConversationId,
            payment.Order.OrderNumber,
            ToMinorUnits(payment.Amount),
            payment.Currency,
            basket);
    }

    private static bool MatchesExpected(
        PaymentRequestSnapshot expected,
        ProviderPaymentSnapshot actual,
        bool requireProviderPaymentId) =>
        string.Equals(actual.OrderNumber, expected.OrderNumber, StringComparison.Ordinal) &&
        actual.AmountMinor == expected.AmountMinor &&
        string.Equals(actual.Currency, expected.Currency, StringComparison.Ordinal) &&
        (!requireProviderPaymentId || !string.IsNullOrWhiteSpace(actual.ProviderPaymentId));

    private static bool IsDefiniteFailure(PaymentConfirmationResult result) =>
        result.Payment?.Status is GatewayPaymentStatus.Failed or GatewayPaymentStatus.Cancelled ||
        (result.Outcome == PaymentGatewayOutcome.Failed &&
         result.ErrorCode is not PaymentGatewayErrorCode.None and
             not PaymentGatewayErrorCode.ProviderUnavailable and
             not PaymentGatewayErrorCode.UnexpectedProviderResponse);

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
    }

    private static long ToMinorUnits(decimal amount)
    {
        var scaled = amount * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new InvalidOperationException("Payment values must use at most two decimal places.");
        }

        return checked((long)scaled);
    }

    private static decimal FromMinorUnits(long amountMinor) => amountMinor / 100m;

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static string NormalizeFailureCode(PaymentGatewayErrorCode errorCode) =>
        errorCode == PaymentGatewayErrorCode.None
            ? PaymentGatewayErrorCode.UnexpectedProviderResponse.ToString()
            : errorCode.ToString();

    private static string NormalizeStoredCode(string failureCode)
    {
        var normalized = string.IsNullOrWhiteSpace(failureCode)
            ? PaymentGatewayErrorCode.UnexpectedProviderResponse.ToString()
            : failureCode.Trim();
        return normalized.Length <= 100 ? normalized : normalized[..100];
    }

    private static PaymentReconciliationResult ProviderDisabled() =>
        new(
            PaymentReconciliationOutcome.ProviderDisabled,
            Message: "Online payment is not configured.");

    private static PaymentReconciliationResult Invalid(string message) =>
        new(PaymentReconciliationOutcome.InvalidRequest, Message: message);

    private static PaymentReconciliationResult Pending(
        string? paymentStatus,
        string? attemptStatus) =>
        new(
            PaymentReconciliationOutcome.PendingReconciliation,
            paymentStatus,
            attemptStatus,
            "Payment is pending provider reconciliation.");

    private static PaymentReconciliationResult Conflict(
        Payment payment,
        PaymentAttempt? attempt,
        string message) =>
        new(
            PaymentReconciliationOutcome.Conflict,
            payment.Status,
            attempt?.Status,
            message);

    private static PaymentReconciliationResult TerminalOrConflict(
        Payment payment,
        PaymentAttempt attempt)
    {
        if (payment.Status is PaymentStatuses.Paid or PaymentStatuses.Failed)
        {
            return new PaymentReconciliationResult(
                PaymentReconciliationOutcome.Replayed,
                payment.Status,
                attempt.Status,
                "The existing terminal payment state was retained.");
        }

        return Conflict(payment, attempt, "The payment state could not be reconciled.");
    }

    private sealed record LocalPayment(Payment Payment);
}
