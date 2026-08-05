using System.Data;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public enum OrderLifecycleOutcome
{
    Updated,
    Unchanged,
    NotFound,
    InvalidTransition
}

public sealed record OrderLifecycleResult(
    OrderLifecycleOutcome Outcome,
    string? Message = null);

public enum PaymentLifecycleOutcome
{
    Updated,
    Unchanged,
    NotFound,
    InvalidTransition
}

public sealed record PaymentLifecycleResult(
    PaymentLifecycleOutcome Outcome,
    string? Message = null);

public sealed class OrderLifecycleService
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [OrderStatuses.Pending] = new HashSet<string>(StringComparer.Ordinal)
            {
                OrderStatuses.Processing,
                OrderStatuses.Cancelled
            },
            [OrderStatuses.Processing] = new HashSet<string>(StringComparer.Ordinal)
            {
                OrderStatuses.Cancelled
            },
            [OrderStatuses.Shipped] = new HashSet<string>(StringComparer.Ordinal),
            [OrderStatuses.Delivered] = new HashSet<string>(StringComparer.Ordinal),
            [OrderStatuses.Cancelled] = new HashSet<string>(StringComparer.Ordinal)
        };

    private readonly AutoPartsDbContext _context;

    public OrderLifecycleService(AutoPartsDbContext context)
    {
        _context = context;
    }

    public async Task<OrderLifecycleResult> UpdateOrderStatusAsync(
        int orderId,
        string requestedStatus,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (_context.Database.IsRelational())
            {
                if (_context.Database.CurrentTransaction == null)
                {
                    ownedTransaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);
                }

                // Fulfillment uses the same aggregate gate. Taking it before reading
                // serializes cancellation, shipment creation, and fulfillment progress.
                var lockedOrder = await _context.Orders
                    .Where(order => order.Id == orderId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            order => order.Status,
                            order => order.Status),
                        cancellationToken);
                if (lockedOrder != 1)
                {
                    return new OrderLifecycleResult(OrderLifecycleOutcome.NotFound);
                }

                // A long-lived caller may already track a pre-lock snapshot. Decisions
                // after the aggregate gate must be based on the now-serialized DB state.
                _context.ChangeTracker.Clear();
            }

            var order = await _context.Orders
                .Include(item => item.OrderItems)
                .Include(item => item.Payment)
                .FirstOrDefaultAsync(item => item.Id == orderId, cancellationToken);

            if (order == null)
            {
                return new OrderLifecycleResult(OrderLifecycleOutcome.NotFound);
            }

            if (string.Equals(order.Status, requestedStatus, StringComparison.Ordinal))
            {
                return new OrderLifecycleResult(OrderLifecycleOutcome.Unchanged);
            }

            if (!AllowedTransitions.TryGetValue(order.Status, out var allowedStatuses) ||
                !allowedStatuses.Contains(requestedStatus))
            {
                return new OrderLifecycleResult(
                    OrderLifecycleOutcome.InvalidTransition,
                    $"{order.Status} durumundan {requestedStatus} durumuna geçilemez.");
            }

            if (requestedStatus == OrderStatuses.Cancelled)
            {
                var hasActiveShipment = await _context.Shipments
                    .AsNoTracking()
                    .AnyAsync(
                        shipment =>
                            shipment.OrderId == orderId &&
                            shipment.Status != ShipmentStatuses.Cancelled &&
                            shipment.Status != ShipmentStatuses.Failed,
                        cancellationToken);
                if (hasActiveShipment)
                {
                    return new OrderLifecycleResult(
                        OrderLifecycleOutcome.InvalidTransition,
                        "Active shipments must be cancelled or failed before the order can be cancelled.");
                }

                if (order.Payment?.Status is
                    PaymentStatuses.Paid or
                    PaymentStatuses.PartiallyRefunded or
                    PaymentStatuses.Refunded)
                {
                    return new OrderLifecycleResult(
                        OrderLifecycleOutcome.InvalidTransition,
                        "Ödenmiş sipariş iade işlemi tamamlanmadan iptal edilemez.");
                }

                var now = DateTime.UtcNow;
                foreach (var item in order.OrderItems.OrderBy(item => item.ProductId))
                {
                    if (_context.Database.IsRelational())
                    {
                        await _context.Database.ExecuteSqlInterpolatedAsync(
                            $"UPDATE [Products] SET [Stock] = [Stock] + {item.Quantity}, [UpdatedAt] = {now} WHERE [Id] = {item.ProductId}",
                            cancellationToken);
                    }
                    else
                    {
                        var product = await _context.Products.FindAsync(
                            [item.ProductId],
                            cancellationToken);
                        if (product != null)
                        {
                            product.Stock += item.Quantity;
                            product.UpdatedAt = now;
                        }
                    }
                }

                if (order.Payment?.Status == PaymentStatuses.Pending)
                {
                    order.Payment.Status = PaymentStatuses.Cancelled;
                    order.Payment.UpdatedAt = now;
                    order.Payment.ConcurrencyToken = Guid.NewGuid();
                }
            }

            order.Status = requestedStatus;
            await _context.SaveChangesAsync(cancellationToken);

            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return new OrderLifecycleResult(OrderLifecycleOutcome.Updated);
        }
        finally
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    public async Task<PaymentLifecycleResult> MarkManualPaymentPaidAsync(
        int paymentId,
        CancellationToken cancellationToken = default)
    {
        var payment = await _context.Payments.FindAsync([paymentId], cancellationToken);
        if (payment == null)
        {
            return new PaymentLifecycleResult(PaymentLifecycleOutcome.NotFound);
        }

        if (payment.Status == PaymentStatuses.Paid)
        {
            return new PaymentLifecycleResult(PaymentLifecycleOutcome.Unchanged);
        }

        if (payment.Provider != PaymentProviders.Manual ||
            payment.Method != PaymentMethods.PayAtDelivery ||
            payment.Status != PaymentStatuses.Pending)
        {
            return new PaymentLifecycleResult(
                PaymentLifecycleOutcome.InvalidTransition,
                "Yalnız bekleyen teslimatta ödeme kayıtları manuel olarak tahsil edildi işaretlenebilir.");
        }

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatuses.Paid;
        payment.PaidAt = now;
        payment.UpdatedAt = now;
        payment.ConcurrencyToken = Guid.NewGuid();
        await _context.SaveChangesAsync(cancellationToken);

        return new PaymentLifecycleResult(PaymentLifecycleOutcome.Updated);
    }
}
