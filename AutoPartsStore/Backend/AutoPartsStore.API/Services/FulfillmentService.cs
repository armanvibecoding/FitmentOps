using System.Data;
using System.Security.Cryptography;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public sealed record ShipmentLineRequest(int OrderItemId, int Quantity);

public enum FulfillmentOutcome
{
    Created,
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record FulfillmentResult(
    FulfillmentOutcome Outcome,
    Shipment? Shipment = null,
    string? Message = null);

public sealed class FulfillmentService
{
    private const int MaxIdempotencyKeyLength = 100;
    private const int MaxLinesPerShipment = 100;
    private const int MaxCarrierLength = 50;
    private const int MaxTrackingNumberLength = 100;

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedTransitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [ShipmentStatuses.Created] =
            [
                ShipmentStatuses.LabelPending,
                ShipmentStatuses.ReadyToShip,
                ShipmentStatuses.Failed,
                ShipmentStatuses.Cancelled
            ],
            [ShipmentStatuses.LabelPending] =
            [
                ShipmentStatuses.ReadyToShip,
                ShipmentStatuses.Failed,
                ShipmentStatuses.Cancelled
            ],
            [ShipmentStatuses.ReadyToShip] =
            [
                ShipmentStatuses.Shipped,
                ShipmentStatuses.Failed,
                ShipmentStatuses.Cancelled
            ],
            [ShipmentStatuses.Shipped] = [ShipmentStatuses.Delivered],
            [ShipmentStatuses.Delivered] = [],
            [ShipmentStatuses.Failed] = [],
            [ShipmentStatuses.Cancelled] = []
        };

    private readonly AutoPartsDbContext _context;

    public FulfillmentService(AutoPartsDbContext context)
    {
        _context = context;
    }

    public async Task<FulfillmentResult> CreateShipmentAsync(
        int orderId,
        string idempotencyKey,
        IReadOnlyCollection<ShipmentLineRequest> lines,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateCreateRequest(orderId, idempotencyKey, lines, createdAt);
        if (validation != null)
        {
            return Invalid(validation);
        }

        var normalizedKey = idempotencyKey.Trim();
        var normalizedLines = lines
            .OrderBy(line => line.OrderItemId)
            .ToArray();
        var payloadHash = ComputePayloadHash(orderId, normalizedLines);

        var existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
        if (existing != null)
        {
            return ResolveReplay(existing, payloadHash);
        }

        await using var ownedTransaction =
            _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;

        try
        {
            existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.CommitAsync(cancellationToken);
                }

                return ResolveReplay(existing, payloadHash);
            }

            if (_context.Database.CurrentTransaction != null)
            {
                var lockedOrder = await _context.Orders
                    .Where(order =>
                        order.Id == orderId &&
                        (order.Status == OrderStatuses.Pending ||
                         order.Status == OrderStatuses.Processing))
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            order => order.Status,
                            order => order.Status),
                        cancellationToken);
                if (lockedOrder != 1)
                {
                    var orderExists = await _context.Orders
                        .AsNoTracking()
                        .AnyAsync(order => order.Id == orderId, cancellationToken);
                    if (ownedTransaction != null)
                    {
                        await ownedTransaction.RollbackAsync(cancellationToken);
                    }
                    return orderExists
                        ? new FulfillmentResult(
                            FulfillmentOutcome.Conflict,
                            Message: "Yalnız Pending veya Processing siparişler için sevkiyat oluşturulabilir.")
                        : new FulfillmentResult(FulfillmentOutcome.NotFound);
                }

                // A transaction created here owns the complete unit of work, so it
                // can discard a pre-lock snapshot. An ambient transaction belongs
                // to the caller and its tracked state must remain intact.
                if (ownedTransaction != null)
                {
                    _context.ChangeTracker.Clear();
                }

                var lockedAllLines = await AcquireOrderItemGatesAsync(
                    normalizedLines,
                    cancellationToken);
                if (!lockedAllLines)
                {
                    if (ownedTransaction != null)
                    {
                        await ownedTransaction.RollbackAsync(cancellationToken);
                    }
                    return new FulfillmentResult(
                        FulfillmentOutcome.NotFound,
                        Message: "Sipariş kalemlerinden biri bulunamadı.");
                }
            }

            var requestedIds = normalizedLines
                .Select(line => line.OrderItemId)
                .ToArray();
            var order = await _context.Orders
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == orderId,
                    cancellationToken);
            if (order == null)
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return new FulfillmentResult(FulfillmentOutcome.NotFound);
            }

            if (order.Status is not (OrderStatuses.Pending or OrderStatuses.Processing))
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return new FulfillmentResult(
                    FulfillmentOutcome.Conflict,
                    Message: "Yalnız Pending veya Processing siparişler için sevkiyat oluşturulabilir.");
            }

            var orderItems = await _context.OrderItems
                .AsNoTracking()
                .Where(item => requestedIds.Contains(item.Id))
                .Select(item => new { item.Id, item.OrderId, item.Quantity })
                .ToListAsync(cancellationToken);

            if (orderItems.Count != normalizedLines.Length ||
                orderItems.Any(item => item.OrderId != orderId))
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                return new FulfillmentResult(
                    FulfillmentOutcome.NotFound,
                    Message: "Sipariş veya sipariş kalemlerinden biri bulunamadı.");
            }

            var alreadyAllocated = await _context.Set<ShipmentItem>()
                .AsNoTracking()
                .Where(item =>
                    requestedIds.Contains(item.OrderItemId) &&
                    item.Shipment.Status != ShipmentStatuses.Cancelled &&
                    item.Shipment.Status != ShipmentStatuses.Failed)
                .GroupBy(item => item.OrderItemId)
                .Select(group => new
                {
                    OrderItemId = group.Key,
                    Quantity = group.Sum(item => item.Quantity)
                })
                .ToDictionaryAsync(
                    item => item.OrderItemId,
                    item => item.Quantity,
                    cancellationToken);

            foreach (var requestedLine in normalizedLines)
            {
                var orderedQuantity = orderItems
                    .Single(item => item.Id == requestedLine.OrderItemId)
                    .Quantity;
                var allocatedQuantity = alreadyAllocated.GetValueOrDefault(
                    requestedLine.OrderItemId);
                if (allocatedQuantity + requestedLine.Quantity > orderedQuantity)
                {
                    if (ownedTransaction != null)
                    {
                        await ownedTransaction.RollbackAsync(cancellationToken);
                    }

                    return new FulfillmentResult(
                        FulfillmentOutcome.Conflict,
                        Message: $"Sipariş kalemi {requestedLine.OrderItemId} için sevk adedi sipariş miktarını aşıyor.");
                }
            }

            var utcCreatedAt = createdAt.UtcDateTime;
            var shipment = new Shipment
            {
                OrderId = orderId,
                IdempotencyKey = normalizedKey,
                PayloadHash = payloadHash,
                Status = ShipmentStatuses.Created,
                CreatedAt = utcCreatedAt,
                UpdatedAt = utcCreatedAt,
                Items = normalizedLines
                    .Select(line => new ShipmentItem
                    {
                        OrderItemId = line.OrderItemId,
                        Quantity = line.Quantity
                    })
                    .ToList()
            };

            if (order.Status == OrderStatuses.Pending)
            {
                order.Status = OrderStatuses.Processing;
            }

            _context.Set<Shipment>().Add(shipment);
            await _context.SaveChangesAsync(cancellationToken);
            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return new FulfillmentResult(FulfillmentOutcome.Created, shipment);
        }
        catch (DbUpdateException) when (ownedTransaction != null)
        {
            await TryRollbackAsync(ownedTransaction, cancellationToken);

            _context.ChangeTracker.Clear();
            existing = await FindByIdempotencyKeyAsync(normalizedKey, cancellationToken);
            if (existing != null)
            {
                return ResolveReplay(existing, payloadHash);
            }

            throw;
        }
    }

    public async Task<FulfillmentResult> TransitionAsync(
        int shipmentId,
        string targetStatus,
        DateTimeOffset occurredAt,
        string? carrier = null,
        string? trackingNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (shipmentId <= 0 ||
            string.IsNullOrWhiteSpace(targetStatus) ||
            !AllowedTransitions.ContainsKey(targetStatus) ||
            occurredAt == default)
        {
            return Invalid("Geçerli sevkiyat, hedef durum ve işlem zamanı belirtilmelidir.");
        }

        var orderId = await _context.Set<Shipment>()
            .AsNoTracking()
            .Where(item => item.Id == shipmentId)
            .Select(item => (int?)item.OrderId)
            .SingleOrDefaultAsync(cancellationToken);
        if (orderId == null)
        {
            return new FulfillmentResult(FulfillmentOutcome.NotFound);
        }

        await using var ownedTransaction =
            _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            : null;

        string? normalizedCarrier = null;
        string? normalizedTrackingNumber = null;
        try
        {
            if (_context.Database.CurrentTransaction != null)
            {
                var lockedOrder = await _context.Orders
                    .Where(order => order.Id == orderId.Value)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            order => order.Status,
                            order => order.Status),
                        cancellationToken);
                if (lockedOrder != 1)
                {
                    return new FulfillmentResult(FulfillmentOutcome.NotFound);
                }
            }

            var shipment = await _context.Set<Shipment>()
                .SingleOrDefaultAsync(item => item.Id == shipmentId, cancellationToken);
            if (shipment == null)
            {
                return new FulfillmentResult(FulfillmentOutcome.NotFound);
            }

            if (occurredAt.UtcDateTime < shipment.CreatedAt)
            {
                return Invalid("Durum zamanı sevkiyat oluşturma zamanından önce olamaz.");
            }

            var isShipping = targetStatus == ShipmentStatuses.Shipped;
            if (isShipping)
            {
                if (string.IsNullOrWhiteSpace(carrier) ||
                    carrier.Trim().Length > MaxCarrierLength ||
                    string.IsNullOrWhiteSpace(trackingNumber) ||
                    trackingNumber.Trim().Length > MaxTrackingNumberLength)
                {
                    return Invalid("Shipped durumu için geçerli kargo firması ve takip numarası zorunludur.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(carrier) ||
                     !string.IsNullOrWhiteSpace(trackingNumber))
            {
                return Invalid("Kargo firması ve takip numarası yalnız Shipped geçişinde verilebilir.");
            }

            normalizedCarrier = isShipping
                ? carrier!.Trim().ToUpperInvariant()
                : null;
            normalizedTrackingNumber = isShipping
                ? trackingNumber!.Trim().ToUpperInvariant()
                : null;

            if (shipment.Status == targetStatus)
            {
                var exactReplay = !isShipping ||
                    (shipment.Carrier == normalizedCarrier &&
                     shipment.TrackingNumber == normalizedTrackingNumber);
                return exactReplay
                    ? new FulfillmentResult(FulfillmentOutcome.Replayed, shipment)
                    : new FulfillmentResult(
                        FulfillmentOutcome.Conflict,
                        shipment,
                        "Aynı durum için farklı kargo bilgileri gönderildi.");
            }

            if (!AllowedTransitions.TryGetValue(shipment.Status, out var allowedTargets) ||
                !allowedTargets.Contains(targetStatus))
            {
                return new FulfillmentResult(
                    FulfillmentOutcome.Conflict,
                    shipment,
                    $"{shipment.Status} durumundan {targetStatus} durumuna geçilemez.");
            }

            var utcOccurredAt = occurredAt.UtcDateTime;
            shipment.Status = targetStatus;
            shipment.UpdatedAt = utcOccurredAt;
            shipment.ConcurrencyToken = Guid.NewGuid();
            if (isShipping)
            {
                shipment.Carrier = normalizedCarrier;
                shipment.TrackingNumber = normalizedTrackingNumber;
                shipment.ShippedAt = utcOccurredAt;
            }
            else if (targetStatus == ShipmentStatuses.Delivered)
            {
                shipment.DeliveredAt = utcOccurredAt;
            }

            await _context.SaveChangesAsync(cancellationToken);
            if (targetStatus is ShipmentStatuses.Shipped or ShipmentStatuses.Delivered)
            {
                await AdvanceOrderStatusIfCompleteAsync(
                    orderId.Value,
                    targetStatus,
                    cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
            }

            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return new FulfillmentResult(FulfillmentOutcome.Updated, shipment);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (ownedTransaction != null)
            {
                await TryRollbackAsync(ownedTransaction, cancellationToken);
            }

            return await ReloadConflictAsync(
                shipmentId,
                "Sevkiyat başka bir işlem tarafından güncellendi.",
                cancellationToken);
        }
        catch (DbUpdateException) when (ownedTransaction != null)
        {
            await TryRollbackAsync(ownedTransaction, cancellationToken);

            _context.ChangeTracker.Clear();
            var trackingConflict = normalizedCarrier != null && normalizedTrackingNumber != null &&
                await _context.Set<Shipment>()
                    .AsNoTracking()
                    .AnyAsync(
                        candidate =>
                            candidate.Id != shipmentId &&
                            candidate.Carrier == normalizedCarrier &&
                            candidate.TrackingNumber == normalizedTrackingNumber,
                        cancellationToken);
            if (trackingConflict)
            {
                return await ReloadConflictAsync(
                    shipmentId,
                    "Kargo firması ve takip numarası başka bir sevkiyatta kullanılıyor.",
                    cancellationToken);
            }

            throw;
        }
    }

    private async Task AdvanceOrderStatusIfCompleteAsync(
        int orderId,
        string shipmentStatus,
        CancellationToken cancellationToken)
    {
        var orderedQuantities = await _context.OrderItems
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .ToDictionaryAsync(
                item => item.Id,
                item => item.Quantity,
                cancellationToken);
        if (orderedQuantities.Count == 0)
        {
            return;
        }

        var completedQuantities = await _context.ShipmentItems
            .AsNoTracking()
            .Where(item =>
                item.Shipment.OrderId == orderId &&
                (shipmentStatus == ShipmentStatuses.Delivered
                    ? item.Shipment.Status == ShipmentStatuses.Delivered
                    : item.Shipment.Status == ShipmentStatuses.Shipped ||
                      item.Shipment.Status == ShipmentStatuses.Delivered))
            .GroupBy(item => item.OrderItemId)
            .Select(group => new
            {
                OrderItemId = group.Key,
                Quantity = group.Sum(item => item.Quantity)
            })
            .ToDictionaryAsync(
                item => item.OrderItemId,
                item => item.Quantity,
                cancellationToken);

        if (orderedQuantities.Any(item =>
                completedQuantities.GetValueOrDefault(item.Key) != item.Value))
        {
            return;
        }

        var order = await _context.Orders
            .SingleAsync(item => item.Id == orderId, cancellationToken);
        if (shipmentStatus == ShipmentStatuses.Delivered)
        {
            if (order.Status is OrderStatuses.Pending or
                OrderStatuses.Processing or
                OrderStatuses.Shipped)
            {
                order.Status = OrderStatuses.Delivered;
            }
        }
        else if (order.Status is OrderStatuses.Pending or OrderStatuses.Processing)
        {
            order.Status = OrderStatuses.Shipped;
        }
    }

    private async Task<bool> AcquireOrderItemGatesAsync(
        IReadOnlyCollection<ShipmentLineRequest> lines,
        CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            var affected = await _context.OrderItems
                .Where(item => item.Id == line.OrderItemId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.Quantity,
                        item => item.Quantity),
                    cancellationToken);
            if (affected != 1)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Shipment?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await _context.Set<Shipment>()
            .AsNoTracking()
            .Include(shipment => shipment.Items)
            .SingleOrDefaultAsync(
                shipment => shipment.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    private async Task<FulfillmentResult> ReloadConflictAsync(
        int shipmentId,
        string message,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var stored = await _context.Set<Shipment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                shipment => shipment.Id == shipmentId,
                cancellationToken);
        return new FulfillmentResult(FulfillmentOutcome.Conflict, stored, message);
    }

    private static string? ValidateCreateRequest(
        int orderId,
        string idempotencyKey,
        IReadOnlyCollection<ShipmentLineRequest>? lines,
        DateTimeOffset createdAt)
    {
        if (orderId <= 0)
        {
            return "Sipariş kimliği pozitif olmalıdır.";
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Trim().Length > MaxIdempotencyKeyLength)
        {
            return $"Idempotency anahtarı 1 ile {MaxIdempotencyKeyLength} karakter arasında olmalıdır.";
        }

        if (lines == null || lines.Count == 0 || lines.Count > MaxLinesPerShipment)
        {
            return $"Sevkiyat 1 ile {MaxLinesPerShipment} sipariş kalemi içermelidir.";
        }

        if (lines.Any(line => line.OrderItemId <= 0 || line.Quantity <= 0))
        {
            return "Sipariş kalemi kimlikleri ve sevk adetleri pozitif olmalıdır.";
        }

        if (lines.Select(line => line.OrderItemId).Distinct().Count() != lines.Count)
        {
            return "Bir sipariş kalemi aynı sevkiyatta yalnız bir kez yer alabilir.";
        }

        return createdAt == default
            ? "Sevkiyat oluşturma zamanı belirtilmelidir."
            : null;
    }

    private static string ComputePayloadHash(
        int orderId,
        IEnumerable<ShipmentLineRequest> lines)
    {
        var canonicalPayload = new StringBuilder()
            .Append(orderId)
            .Append('|');
        foreach (var line in lines)
        {
            canonicalPayload
                .Append(line.OrderItemId)
                .Append(':')
                .Append(line.Quantity)
                .Append(';');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload.ToString())));
    }

    private static FulfillmentResult ResolveReplay(
        Shipment existing,
        string payloadHash)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(existing.PayloadHash),
            Encoding.ASCII.GetBytes(payloadHash))
            ? new FulfillmentResult(FulfillmentOutcome.Replayed, existing)
            : new FulfillmentResult(
                FulfillmentOutcome.Conflict,
                existing,
                "Idempotency anahtarı farklı bir sevkiyat isteği için kullanılmış.");
    }

    private static async Task TryRollbackAsync(
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The provider can already have rolled back the failed transaction.
        }
    }

    private static FulfillmentResult Invalid(string message)
    {
        return new FulfillmentResult(
            FulfillmentOutcome.InvalidRequest,
            Message: message);
    }
}
