using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
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
using Microsoft.Data.SqlClient;

namespace AutoPartsStore.API.Services;

public static class HostedCheckoutPaymentMethods
{
    public const string HostedCard = "HostedCard";
}

public sealed class HostedCheckoutOptions
{
    public TimeSpan ReservationLifetime { get; init; } = TimeSpan.FromMinutes(15);

    internal void Validate()
    {
        if (ReservationLifetime <= TimeSpan.Zero || ReservationLifetime > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReservationLifetime),
                "Reservation lifetime must be positive and cannot exceed two hours.");
        }
    }
}

public sealed record HostedCheckoutCustomer(
    string Name,
    string Email,
    string Phone,
    string ShippingAddress,
    string City,
    string PostalCode)
{
    public override string ToString() => $"{nameof(HostedCheckoutCustomer)} {{ Sensitive = true }}";
}

public sealed record HostedCheckoutCommand(
    [property: JsonIgnore] string IdempotencyKey,
    IReadOnlyCollection<InventoryReservationLine> Lines,
    [property: JsonIgnore] HostedCheckoutCustomer Customer,
    Uri CallbackUri,
    Uri ReturnUri,
    int? UserId = null,
    [property: JsonIgnore] PaymentBuyerContext? Buyer = null,
    [property: JsonIgnore] PaymentAddressContext? BillingAddress = null,
    [property: JsonIgnore] PaymentAddressContext? ShippingAddress = null,
    IReadOnlyCollection<LegalAcceptanceDto>? LegalAcceptances = null)
{
    public override string ToString() => $"{nameof(HostedCheckoutCommand)} {{ Sensitive = true }}";
}

public enum HostedCheckoutOutcome
{
    RequiresCustomerAction,
    PendingReconciliation,
    Declined,
    ProviderDisabled,
    ConfigurationUnavailable,
    InventoryUnavailable,
    Conflict,
    InvalidRequest
}

/// <summary>
/// Deliberately excludes hosted tokens, payment credentials and customer data.
/// </summary>
public sealed record HostedCheckoutResult(
    HostedCheckoutOutcome Outcome,
    bool Replayed = false,
    int? OrderId = null,
    string? OrderNumber = null,
    string? OrderStatus = null,
    string? PaymentStatus = null,
    string? AttemptStatus = null,
    Uri? RedirectUri = null,
    string? Message = null);

public sealed class HostedCheckoutService
{
    private const int MaxLines = 100;
    private const int MaxIdempotencyKeyLength = 100;
    private const string Currency = "TRY";

    private readonly AutoPartsDbContext _context;
    private readonly InventoryReservationService _reservationService;
    private readonly OrderLifecycleService _orderLifecycleService;
    private readonly IPaymentGateway _paymentGateway;
    private readonly LegalConsentService _legalConsentService;
    private readonly HostedCheckoutOptions _options;
    private readonly TimeProvider _timeProvider;

    public HostedCheckoutService(
        AutoPartsDbContext context,
        InventoryReservationService reservationService,
        OrderLifecycleService orderLifecycleService,
        IPaymentGateway paymentGateway,
        LegalConsentService legalConsentService,
        HostedCheckoutOptions options,
        TimeProvider timeProvider)
    {
        _context = context;
        _reservationService = reservationService;
        _orderLifecycleService = orderLifecycleService;
        _paymentGateway = paymentGateway;
        _legalConsentService = legalConsentService;
        _options = options;
        _timeProvider = timeProvider;
        _options.Validate();
    }

    public async Task<HostedCheckoutResult> StartAsync(
        HostedCheckoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // No database read, reservation or order is allowed before this gate.
        if (!_paymentGateway.IsEnabled)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.ProviderDisabled,
                Message: "Online payment is not configured.");
        }

        var validation = Validate(command, _paymentGateway.ProviderName);
        if (validation != null)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.InvalidRequest,
                Message: validation);
        }

        if (_context.Database.CurrentTransaction != null)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.InvalidRequest,
                Message: "Hosted checkout requires its own orchestration boundary.");
        }

        var normalizedKey = command.IdempotencyKey.Trim();
        var normalizedLines = command.Lines.OrderBy(line => line.ProductId).ToArray();
        var payloadHash = ComputePayloadHash(
            command,
            normalizedLines,
            _paymentGateway.ProviderName.Trim());
        var existing = await FindSessionAsync(normalizedKey, cancellationToken);
        if (existing != null)
        {
            return await ResolveExistingAsync(
                existing,
                payloadHash,
                command,
                replayed: true,
                cancellationToken);
        }

        var legalValidation = await _legalConsentService.ValidateAsync(
            command.LegalAcceptances,
            cancellationToken);
        if (legalValidation.Outcome != LegalConsentValidationOutcome.Valid)
        {
            return new HostedCheckoutResult(
                legalValidation.Outcome == LegalConsentValidationOutcome.ConfigurationUnavailable
                    ? HostedCheckoutOutcome.ConfigurationUnavailable
                    : HostedCheckoutOutcome.InvalidRequest,
                Message: legalValidation.Message);
        }

        var reservation = await _reservationService.ReserveAsync(
            normalizedKey,
            normalizedLines,
            _timeProvider.GetUtcNow().Add(_options.ReservationLifetime),
            cancellationToken);
        if (reservation.Outcome == InventoryReservationOutcome.InventoryUnavailable)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.InventoryUnavailable,
                Message: "Requested inventory is unavailable.");
        }

        if (reservation.Outcome == InventoryReservationOutcome.InvalidRequest)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.InvalidRequest,
                Message: reservation.Message);
        }

        if (reservation.Outcome == InventoryReservationOutcome.Conflict ||
            reservation.Reservation == null)
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.Conflict,
                Message: "The checkout idempotency key conflicts with an existing reservation.");
        }

        if (reservation.Reservation.Status != InventoryReservationStatuses.Active)
        {
            existing = await FindSessionAsync(normalizedKey, cancellationToken);
            return existing == null
                ? new HostedCheckoutResult(
                    HostedCheckoutOutcome.Conflict,
                    Message: "The reservation is no longer active and has no checkout session.")
                : await ResolveExistingAsync(
                    existing,
                    payloadHash,
                    command,
                    replayed: true,
                    cancellationToken);
        }

        IDbContextTransaction? transaction = null;
        long sessionId;
        try
        {
            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            }

            existing = await FindSessionAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();
                    transaction = null;
                }

                return await ResolveExistingAsync(
                    existing,
                    payloadHash,
                    command,
                    replayed: true,
                    cancellationToken);
            }

            var products = await LoadProductsAsync(normalizedLines, cancellationToken);
            if (products.Count != normalizedLines.Length)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new HostedCheckoutResult(
                    HostedCheckoutOutcome.InventoryUnavailable,
                    Message: "A reserved product no longer exists.");
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var order = BuildOrder(
                command,
                normalizedKey,
                normalizedLines,
                products,
                _paymentGateway.ProviderName.Trim(),
                now);
            _legalConsentService.AttachToOrder(
                order,
                legalValidation.Documents,
                command.UserId,
                normalizedKey,
                now);
            var session = new HostedCheckoutSession
            {
                IdempotencyKey = normalizedKey,
                PayloadHash = payloadHash,
                InventoryReservationId = reservation.Reservation.Id,
                Order = order,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.Set<HostedCheckoutSession>().Add(session);
            await _context.SaveChangesAsync(cancellationToken);

            var commit = await _reservationService.CommitAsync(
                reservation.Reservation.Id,
                order.Id,
                cancellationToken);
            if (commit.Outcome is not InventoryReservationOutcome.Updated and
                not InventoryReservationOutcome.Replayed)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return new HostedCheckoutResult(
                    HostedCheckoutOutcome.Conflict,
                    Message: "The inventory reservation could not be committed atomically.");
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }

            sessionId = session.Id;
        }
        catch (DbUpdateException)
        {
            if (transaction != null)
            {
                await TryRollbackAsync(transaction, cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }

            _context.ChangeTracker.Clear();
            existing = await FindSessionAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                return await ResolveExistingAsync(
                    existing,
                    payloadHash,
                    command,
                    replayed: true,
                    cancellationToken);
            }

            await _reservationService.ReleaseAsync(
                reservation.Reservation.Id,
                cancellationToken);
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.Conflict,
                Message: "Checkout persistence conflicted with existing state.");
        }
        catch (DbException exception) when (IsRetryableConcurrencyException(exception))
        {
            if (transaction != null)
            {
                await TryRollbackAsync(transaction, cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }

            _context.ChangeTracker.Clear();
            existing = await FindSessionAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                return await ResolveExistingAsync(
                    existing,
                    payloadHash,
                    command,
                    replayed: true,
                    cancellationToken);
            }

            return new HostedCheckoutResult(
                HostedCheckoutOutcome.Conflict,
                Message: "Checkout state changed concurrently; retry with the same idempotency key.");
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }

        _context.ChangeTracker.Clear();
        existing = await FindSessionByIdAsync(sessionId, cancellationToken) ??
            throw new InvalidOperationException("Committed hosted checkout session could not be reloaded.");
        return await ResolveExistingAsync(
            existing,
            payloadHash,
            command,
            replayed: false,
            cancellationToken);
    }

    private async Task<HostedCheckoutResult> ResolveExistingAsync(
        HostedCheckoutSession session,
        string payloadHash,
        HostedCheckoutCommand command,
        bool replayed,
        CancellationToken cancellationToken)
    {
        if (!FixedTimeEquals(session.PayloadHash, payloadHash))
        {
            return new HostedCheckoutResult(
                HostedCheckoutOutcome.Conflict,
                Replayed: replayed,
                Message: "The checkout idempotency key was used with a different payload.");
        }

        var attempt = SingleAttempt(session);
        if (attempt.Status != PaymentAttemptStatuses.Created)
        {
            return ToResult(session, attempt, replayed);
        }

        var providerCommand = BuildProviderCommand(session, attempt, command);
        PaymentInitializationResult initialization;
        try
        {
            // Local inventory/order work is committed and no EF transaction is open here.
            initialization = await _paymentGateway.InitializeAsync(
                providerCommand,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkAmbiguousAsync(session, attempt, "request-cancelled", CancellationToken.None);
            throw;
        }
        catch
        {
            await MarkAmbiguousAsync(session, attempt, "provider-exception", cancellationToken);
            return ToResult(session, attempt, replayed);
        }

        if (HasUsableRedirect(initialization))
        {
            attempt.Status = PaymentAttemptStatuses.RequiresCustomerAction;
            attempt.RedirectUrl = initialization.HostedPaymentPageUri!.AbsoluteUri;
            attempt.HostedPaymentToken = initialization.HostedPaymentToken;
            attempt.ProviderPaymentId = NormalizeOptional(
                initialization.ProviderPaymentId,
                200);
            attempt.ProviderResultCode = initialization.ErrorCode.ToString();
            attempt.ExpiresAt = initialization.ExpiresAtUtc?.UtcDateTime;
            attempt.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            if (attempt.ProviderPaymentId != null)
            {
                session.Order.Payment!.ProviderPaymentId = attempt.ProviderPaymentId;
                session.Order.Payment.UpdatedAt = attempt.UpdatedAt;
                session.Order.Payment.ConcurrencyToken = Guid.NewGuid();
            }

            session.UpdatedAt = attempt.UpdatedAt;
            await _context.SaveChangesAsync(cancellationToken);
            return ToResult(session, attempt, replayed);
        }

        if (IsDefiniteFailure(initialization))
        {
            var failureCode = initialization.ErrorCode.ToString();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            attempt.Status = PaymentAttemptStatuses.Failed;
            attempt.FailureCode = failureCode;
            attempt.ProviderResultCode = failureCode;
            attempt.UpdatedAt = now;
            attempt.CompletedAt = now;
            session.Order.Payment!.Status = PaymentStatuses.Failed;
            session.Order.Payment.FailureCode = failureCode;
            session.Order.Payment.UpdatedAt = now;
            session.Order.Payment.ConcurrencyToken = Guid.NewGuid();
            session.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);

            var cancelled = await _orderLifecycleService.UpdateOrderStatusAsync(
                session.OrderId,
                OrderStatuses.Cancelled,
                cancellationToken);
            _context.ChangeTracker.Clear();
            var refreshed = await FindSessionByIdAsync(session.Id, cancellationToken) ?? session;
            var refreshedAttempt = SingleAttempt(refreshed);
            return cancelled.Outcome is OrderLifecycleOutcome.Updated or OrderLifecycleOutcome.Unchanged
                ? ToResult(refreshed, refreshedAttempt, replayed)
                : new HostedCheckoutResult(
                    HostedCheckoutOutcome.PendingReconciliation,
                    Replayed: replayed,
                    OrderId: refreshed.OrderId,
                    OrderNumber: refreshed.Order.OrderNumber,
                    OrderStatus: refreshed.Order.Status,
                    PaymentStatus: refreshed.Order.Payment?.Status,
                    AttemptStatus: refreshedAttempt.Status,
                    Message: "Payment failed but order cancellation requires reconciliation.");
        }

        await MarkAmbiguousAsync(
            session,
            attempt,
            initialization.ErrorCode == PaymentGatewayErrorCode.None
                ? "provider-ambiguous"
                : initialization.ErrorCode.ToString(),
            cancellationToken);
        return ToResult(session, attempt, replayed);
    }

    private async Task MarkAmbiguousAsync(
        HostedCheckoutSession session,
        PaymentAttempt attempt,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        attempt.Status = PaymentAttemptStatuses.Unknown;
        attempt.FailureCode = failureCode;
        attempt.ProviderResultCode = failureCode;
        attempt.UpdatedAt = now;
        session.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static HostedCheckoutResult ToResult(
        HostedCheckoutSession session,
        PaymentAttempt attempt,
        bool replayed)
    {
        var outcome = attempt.Status switch
        {
            PaymentAttemptStatuses.RequiresCustomerAction =>
                HostedCheckoutOutcome.RequiresCustomerAction,
            PaymentAttemptStatuses.Failed or PaymentAttemptStatuses.Cancelled =>
                HostedCheckoutOutcome.Declined,
            _ => HostedCheckoutOutcome.PendingReconciliation
        };

        var redirect = Uri.TryCreate(attempt.RedirectUrl, UriKind.Absolute, out var redirectUri)
            ? redirectUri
            : null;
        return new HostedCheckoutResult(
            outcome,
            replayed,
            session.OrderId,
            session.Order.OrderNumber,
            session.Order.Status,
            session.Order.Payment?.Status,
            attempt.Status,
            redirect);
    }

    private static InitializePaymentCommand BuildProviderCommand(
        HostedCheckoutSession session,
        PaymentAttempt attempt,
        HostedCheckoutCommand command)
    {
        var payment = session.Order.Payment ??
            throw new InvalidOperationException("Hosted checkout payment is missing.");
        var basket = session.Order.OrderItems
            .OrderBy(item => item.ProductId)
            .Select(item => new PaymentBasketItemSnapshot(
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.ProductId.ToString(CultureInfo.InvariantCulture),
                item.Product?.Name ?? $"Product {item.ProductId}",
                item.Quantity,
                ToMinorUnits(item.Price),
                checked(ToMinorUnits(item.Price) * item.Quantity)))
            .ToImmutableArray();
        var expected = new PaymentRequestSnapshot(
            payment.Id,
            attempt.ConversationId,
            session.Order.OrderNumber,
            ToMinorUnits(payment.Amount),
            payment.Currency,
            basket);
        return new InitializePaymentCommand(
            expected,
            command.CallbackUri,
            command.ReturnUri,
            command.IdempotencyKey.Trim(),
            command.Buyer,
            command.BillingAddress,
            command.ShippingAddress);
    }

    private static Order BuildOrder(
        HostedCheckoutCommand command,
        string idempotencyKey,
        IReadOnlyCollection<InventoryReservationLine> lines,
        IReadOnlyDictionary<int, Product> products,
        string provider,
        DateTime now)
    {
        var customer = command.Customer;
        var order = new Order
        {
            OrderNumber = $"ORD-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant(),
            CheckoutIdempotencyKey = idempotencyKey,
            UserId = command.UserId,
            CustomerName = customer.Name.Trim(),
            CustomerEmail = customer.Email.Trim().ToLowerInvariant(),
            CustomerPhone = customer.Phone.Trim(),
            ShippingAddress = customer.ShippingAddress.Trim(),
            City = customer.City.Trim(),
            PostalCode = customer.PostalCode.Trim(),
            Status = OrderStatuses.Pending,
            OrderDate = now
        };

        foreach (var line in lines)
        {
            var product = products[line.ProductId];
            order.OrderItems.Add(new OrderItem
            {
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                Price = product.Price
            });
            order.TotalAmount = checked(order.TotalAmount + product.Price * line.Quantity);
        }

        var payment = new Payment
        {
            Provider = provider,
            Method = HostedCheckoutPaymentMethods.HostedCard,
            Status = PaymentStatuses.Pending,
            Amount = order.TotalAmount,
            Currency = Currency,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        payment.Attempts.Add(new PaymentAttempt
        {
            Provider = provider,
            IdempotencyKey = idempotencyKey,
            ConversationId = ComputeConversationId(idempotencyKey),
            Status = PaymentAttemptStatuses.Created,
            CreatedAt = now,
            UpdatedAt = now
        });
        order.Payment = payment;
        return order;
    }

    private async Task<Dictionary<int, Product>> LoadProductsAsync(
        IReadOnlyCollection<InventoryReservationLine> lines,
        CancellationToken cancellationToken)
    {
        var ids = lines.Select(line => line.ProductId).ToArray();
        return await _context.Products
            .AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
    }

    private Task<HostedCheckoutSession?> FindSessionAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SessionQuery().SingleOrDefaultAsync(
            session => session.IdempotencyKey == idempotencyKey,
            cancellationToken);

    private Task<HostedCheckoutSession?> FindSessionByIdAsync(
        long sessionId,
        CancellationToken cancellationToken) =>
        SessionQuery().SingleOrDefaultAsync(
            session => session.Id == sessionId,
            cancellationToken);

    private IQueryable<HostedCheckoutSession> SessionQuery() =>
        _context.Set<HostedCheckoutSession>()
            .Include(session => session.InventoryReservation)
            .Include(session => session.Order)
                .ThenInclude(order => order.OrderItems)
                    .ThenInclude(item => item.Product)
            .Include(session => session.Order)
                .ThenInclude(order => order.Payment)
                    .ThenInclude(payment => payment!.Attempts);

    private static PaymentAttempt SingleAttempt(HostedCheckoutSession session) =>
        session.Order.Payment?.Attempts.SingleOrDefault() ??
        throw new InvalidOperationException("Hosted checkout payment attempt is missing.");

    private static bool HasUsableRedirect(PaymentInitializationResult result)
    {
        if (result.Outcome is not PaymentGatewayOutcome.Succeeded and
            not PaymentGatewayOutcome.Pending)
        {
            return false;
        }

        var redirectUri = result.HostedPaymentPageUri;
        return redirectUri is
        {
            IsAbsoluteUri: true,
            Scheme: "https"
        } &&
            string.IsNullOrEmpty(redirectUri.UserInfo) &&
            redirectUri.AbsoluteUri.Length <= 2048 &&
            (string.IsNullOrEmpty(result.HostedPaymentToken) ||
             result.HostedPaymentToken.Length <= 500);
    }

    private static bool IsDefiniteFailure(PaymentInitializationResult result) =>
        result.Outcome == PaymentGatewayOutcome.Failed &&
        result.ErrorCode is not PaymentGatewayErrorCode.None and
            not PaymentGatewayErrorCode.ProviderUnavailable and
            not PaymentGatewayErrorCode.UnexpectedProviderResponse;

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : null;
    }

    private static string? Validate(HostedCheckoutCommand command, string providerName)
    {
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (key.Length is < 16 or > MaxIdempotencyKeyLength ||
            key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return "Idempotency key must contain 16 to 100 safe characters.";
        }

        if (command.Lines == null || command.Lines.Count is < 1 or > MaxLines ||
            command.Lines.Any(line => line.ProductId <= 0 || line.Quantity is < 1 or > 100) ||
            command.Lines.Select(line => line.ProductId).Distinct().Count() != command.Lines.Count)
        {
            return "Checkout lines must contain unique positive products and quantities up to 100.";
        }

        if (command.Customer == null ||
            !HasLength(command.Customer.Name, 2, 200) ||
            !HasLength(command.Customer.Email, 3, 200) ||
            !command.Customer.Email.Contains('@', StringComparison.Ordinal) ||
            !HasLength(command.Customer.Phone, 1, 20) ||
            !HasLength(command.Customer.ShippingAddress, 10, 500) ||
            !HasLength(command.Customer.City, 1, 100) ||
            !HasLength(command.Customer.PostalCode, 1, 10))
        {
            return "Customer and shipping fields are invalid.";
        }

        if (!IsSafeProviderName(providerName) ||
            !IsSafeHttpsUri(command.CallbackUri) ||
            !IsSafeHttpsUri(command.ReturnUri))
        {
            return "Payment provider or redirect endpoints are invalid.";
        }

        return null;
    }

    private static bool HasLength(string? value, int minimum, int maximum) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length >= minimum && value.Trim().Length <= maximum;

    private static bool IsSafeProviderName(string? providerName) =>
        !string.IsNullOrWhiteSpace(providerName) && providerName.Trim().Length <= 50;

    private static bool IsSafeHttpsUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static string ComputePayloadHash(
        HostedCheckoutCommand command,
        IEnumerable<InventoryReservationLine> lines,
        string providerName)
    {
        var canonical = new StringBuilder();
        Append(canonical, providerName);
        Append(canonical, command.UserId?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, command.Customer.Name.Trim());
        Append(canonical, command.Customer.Email.Trim().ToLowerInvariant());
        Append(canonical, command.Customer.Phone.Trim());
        Append(canonical, command.Customer.ShippingAddress.Trim());
        Append(canonical, command.Customer.City.Trim());
        Append(canonical, command.Customer.PostalCode.Trim());
        Append(canonical, command.CallbackUri.AbsoluteUri);
        Append(canonical, command.ReturnUri.AbsoluteUri);
        Append(canonical, command.Buyer?.Reference);
        Append(canonical, command.Buyer?.FirstName);
        Append(canonical, command.Buyer?.LastName);
        Append(canonical, command.Buyer?.Email);
        Append(canonical, command.Buyer?.Phone);
        Append(canonical, command.Buyer?.IdentityNumber);
        Append(canonical, command.Buyer?.IpAddress);
        AppendAddress(canonical, command.BillingAddress);
        AppendAddress(canonical, command.ShippingAddress);
        foreach (var acceptance in (command.LegalAcceptances ?? [])
                     .OrderBy(item => item.DocumentType ?? string.Empty, StringComparer.Ordinal))
        {
            Append(canonical, (acceptance.DocumentType ?? string.Empty).Trim());
            Append(canonical, (acceptance.Version ?? string.Empty).Trim());
            Append(canonical, (acceptance.ContentSha256 ?? string.Empty).Trim().ToLowerInvariant());
            Append(canonical, acceptance.Accepted.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var line in lines)
        {
            Append(canonical, line.ProductId.ToString(CultureInfo.InvariantCulture));
            Append(canonical, line.Quantity.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string ComputeConversationId(string idempotencyKey)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"hosted-checkout|{idempotencyKey}")));
        return $"hc_{digest[..32].ToLowerInvariant()}";
    }

    private static void AppendAddress(StringBuilder builder, PaymentAddressContext? address)
    {
        Append(builder, address?.ContactName);
        Append(builder, address?.AddressLine);
        Append(builder, address?.City);
        Append(builder, address?.Country);
        Append(builder, address?.PostalCode);
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static long ToMinorUnits(decimal amount)
    {
        var scaled = amount * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new InvalidOperationException("Product prices must use at most two decimal places.");
        }

        return checked((long)scaled);
    }

    private static async Task TryRollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            // Preserve the original persistence failure.
        }
    }

    private bool IsRetryableConcurrencyException(DbException exception) =>
        _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? exception.ErrorCode is 5 or 6
            : exception is SqlException { Number: 1205 or 1222 };
}
