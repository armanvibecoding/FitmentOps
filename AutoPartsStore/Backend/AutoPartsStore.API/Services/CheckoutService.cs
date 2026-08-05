using System.Data;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Services;

public enum CheckoutOutcome
{
    Created,
    Replayed,
    InvalidRequest,
    ConfigurationUnavailable,
    IdempotencyConflict,
    InventoryUnavailable
}

public sealed record CheckoutResult(
    CheckoutOutcome Outcome,
    Order? Order = null,
    string? Message = null);

public sealed class CheckoutService
{
    private readonly AutoPartsDbContext _context;
    private readonly LegalConsentService _legalConsentService;

    public CheckoutService(
        AutoPartsDbContext context,
        LegalConsentService legalConsentService)
    {
        _context = context;
        _legalConsentService = legalConsentService;
    }

    public async Task<CheckoutResult> CreateOrderAsync(
        CreateOrderDto dto,
        string idempotencyKey,
        int? userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedItemsResult = NormalizeItems(dto.Items);
        if (normalizedItemsResult.Items == null)
        {
            return new CheckoutResult(
                CheckoutOutcome.InvalidRequest,
                Message: normalizedItemsResult.Error);
        }

        var normalizedItems = normalizedItemsResult.Items;
        var existingOrder = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existingOrder != null)
        {
            return MatchesRequest(existingOrder, dto, normalizedItems, userId)
                ? new CheckoutResult(CheckoutOutcome.Replayed, existingOrder)
                : new CheckoutResult(
                    CheckoutOutcome.IdempotencyConflict,
                    Message: "Bu idempotency anahtarı farklı bir sipariş isteği için kullanılmış.");
        }

        var legalValidation = await _legalConsentService.ValidateAsync(
            dto.LegalAcceptances,
            cancellationToken);
        if (legalValidation.Outcome != LegalConsentValidationOutcome.Valid)
        {
            return new CheckoutResult(
                legalValidation.Outcome == LegalConsentValidationOutcome.ConfigurationUnavailable
                    ? CheckoutOutcome.ConfigurationUnavailable
                    : CheckoutOutcome.InvalidRequest,
                Message: legalValidation.Message);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (_context.Database.IsRelational())
            {
                transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            existingOrder = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            if (existingOrder != null)
            {
                return MatchesRequest(existingOrder, dto, normalizedItems, userId)
                    ? new CheckoutResult(CheckoutOutcome.Replayed, existingOrder)
                    : new CheckoutResult(
                        CheckoutOutcome.IdempotencyConflict,
                        Message: "Bu idempotency anahtarı farklı bir sipariş isteği için kullanılmış.");
            }

            var now = DateTime.UtcNow;
            Dictionary<int, Product> products;

            if (_context.Database.IsRelational())
            {
                foreach (var item in normalizedItems)
                {
                    var affectedRows = await _context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE [Products] SET [Stock] = [Stock] - {item.Quantity}, [UpdatedAt] = {now} WHERE [Id] = {item.ProductId} AND [Stock] >= {item.Quantity}",
                        cancellationToken);

                    if (affectedRows != 1)
                    {
                        await RollbackAsync(transaction, cancellationToken);
                        _context.ChangeTracker.Clear();
                        return new CheckoutResult(
                            CheckoutOutcome.InventoryUnavailable,
                            Message: "Sepetteki ürünlerden biri artık mevcut değil veya yeterli stok bulunmuyor.");
                    }
                }

                var productIds = normalizedItems.Select(item => item.ProductId).ToArray();
                products = await _context.Products
                    .AsNoTracking()
                    .Where(product => productIds.Contains(product.Id))
                    .ToDictionaryAsync(product => product.Id, cancellationToken);
            }
            else
            {
                var productIds = normalizedItems.Select(item => item.ProductId).ToArray();
                products = await _context.Products
                    .Where(product => productIds.Contains(product.Id))
                    .ToDictionaryAsync(product => product.Id, cancellationToken);

                if (normalizedItems.Any(item =>
                        !products.TryGetValue(item.ProductId, out var product) ||
                        product.Stock < item.Quantity))
                {
                    _context.ChangeTracker.Clear();
                    return new CheckoutResult(
                        CheckoutOutcome.InventoryUnavailable,
                        Message: "Sepetteki ürünlerden biri artık mevcut değil veya yeterli stok bulunmuyor.");
                }

                foreach (var item in normalizedItems)
                {
                    products[item.ProductId].Stock -= item.Quantity;
                    products[item.ProductId].UpdatedAt = now;
                }
            }

            if (products.Count != normalizedItems.Count)
            {
                await RollbackAsync(transaction, cancellationToken);
                _context.ChangeTracker.Clear();
                return new CheckoutResult(
                    CheckoutOutcome.InventoryUnavailable,
                    Message: "Sepetteki ürünlerden biri artık mevcut değil veya yeterli stok bulunmuyor.");
            }

            var order = BuildOrder(dto, normalizedItems, products, idempotencyKey, userId, now);
            _legalConsentService.AttachToOrder(
                order,
                legalValidation.Documents,
                userId,
                idempotencyKey,
                now);
            _context.Orders.Add(order);
            await _context.SaveChangesAsync(cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            _context.ChangeTracker.Clear();
            var createdOrder = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

            return new CheckoutResult(CheckoutOutcome.Created, createdOrder);
        }
        catch (DbUpdateException)
        {
            await RollbackAsync(transaction, cancellationToken);
            _context.ChangeTracker.Clear();

            if (transaction != null)
            {
                await transaction.DisposeAsync();
                transaction = null;
            }

            existingOrder = await FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
            if (existingOrder != null)
            {
                return MatchesRequest(existingOrder, dto, normalizedItems, userId)
                    ? new CheckoutResult(CheckoutOutcome.Replayed, existingOrder)
                    : new CheckoutResult(
                        CheckoutOutcome.IdempotencyConflict,
                        Message: "Bu idempotency anahtarı farklı bir sipariş isteği için kullanılmış.");
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<Order?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        return await _context.Orders
            .AsNoTracking()
            .Include(order => order.OrderItems)
            .ThenInclude(item => item.Product)
            .Include(order => order.Payment)
            .Include(order => order.LegalAcceptances)
            .FirstOrDefaultAsync(
                order => order.CheckoutIdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    private static Order BuildOrder(
        CreateOrderDto dto,
        IReadOnlyCollection<NormalizedOrderItem> items,
        IReadOnlyDictionary<int, Product> products,
        string idempotencyKey,
        int? userId,
        DateTime now)
    {
        var order = new Order
        {
            OrderNumber = $"ORD-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..22].ToUpperInvariant(),
            CheckoutIdempotencyKey = idempotencyKey,
            UserId = userId,
            CustomerName = dto.CustomerName.Trim(),
            CustomerEmail = dto.CustomerEmail.Trim().ToLowerInvariant(),
            CustomerPhone = dto.CustomerPhone.Trim(),
            ShippingAddress = dto.ShippingAddress.Trim(),
            City = dto.City.Trim(),
            PostalCode = dto.PostalCode.Trim(),
            Status = OrderStatuses.Pending,
            OrderDate = now
        };

        foreach (var item in items)
        {
            var product = products[item.ProductId];
            order.OrderItems.Add(new OrderItem
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                Price = product.Price
            });
            order.TotalAmount += product.Price * item.Quantity;
        }

        order.Payment = new Payment
        {
            Provider = PaymentProviders.Manual,
            Method = PaymentMethods.PayAtDelivery,
            Status = PaymentStatuses.Pending,
            Amount = order.TotalAmount,
            Currency = "TRY",
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };

        return order;
    }

    private static bool MatchesRequest(
        Order order,
        CreateOrderDto dto,
        IReadOnlyCollection<NormalizedOrderItem> normalizedItems,
        int? userId)
    {
        if (order.UserId != userId ||
            !string.Equals(order.CustomerName, dto.CustomerName.Trim(), StringComparison.Ordinal) ||
            !string.Equals(order.CustomerEmail, dto.CustomerEmail.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(order.CustomerPhone, dto.CustomerPhone.Trim(), StringComparison.Ordinal) ||
            !string.Equals(order.ShippingAddress, dto.ShippingAddress.Trim(), StringComparison.Ordinal) ||
            !string.Equals(order.City, dto.City.Trim(), StringComparison.Ordinal) ||
            !string.Equals(order.PostalCode, dto.PostalCode.Trim(), StringComparison.Ordinal) ||
            !string.Equals(order.Payment?.Method, dto.PaymentMethod, StringComparison.Ordinal))
        {
            return false;
        }

        var existingItems = order.OrderItems
            .OrderBy(item => item.ProductId)
            .Select(item => new NormalizedOrderItem(item.ProductId, item.Quantity))
            .ToArray();

        if (!existingItems.SequenceEqual(normalizedItems)) return false;

        var submittedAcceptances = (dto.LegalAcceptances ?? [])
            .Where(acceptance => acceptance.Accepted)
            .OrderBy(acceptance => acceptance.DocumentType ?? string.Empty, StringComparer.Ordinal)
            .Select(acceptance => new
            {
                Type = (acceptance.DocumentType ?? string.Empty).Trim(),
                Version = (acceptance.Version ?? string.Empty).Trim(),
                Hash = (acceptance.ContentSha256 ?? string.Empty).Trim().ToLowerInvariant()
            })
            .ToArray();
        var storedAcceptances = order.LegalAcceptances
            .OrderBy(acceptance => acceptance.DocumentTypeSnapshot, StringComparer.Ordinal)
            .Select(acceptance => new
            {
                Type = acceptance.DocumentTypeSnapshot,
                Version = acceptance.VersionSnapshot,
                Hash = acceptance.ContentSha256Snapshot.ToLowerInvariant()
            })
            .ToArray();
        return submittedAcceptances.SequenceEqual(storedAcceptances);
    }

    private static (IReadOnlyList<NormalizedOrderItem>? Items, string? Error) NormalizeItems(
        IEnumerable<OrderItemDto> items)
    {
        var normalizedItems = new List<NormalizedOrderItem>();

        foreach (var group in items.GroupBy(item => item.ProductId).OrderBy(group => group.Key))
        {
            var totalQuantity = group.Sum(item => (long)item.Quantity);
            if (group.Key <= 0 || totalQuantity <= 0 || totalQuantity > 100)
            {
                return (null, "Bir ürün için toplam miktar 1 ile 100 arasında olmalıdır.");
            }

            normalizedItems.Add(new NormalizedOrderItem(group.Key, (int)totalQuantity));
        }

        return normalizedItems.Count == 0
            ? (null, "Sepet en az bir ürün içermelidir.")
            : (normalizedItems, null);
    }

    private static async Task RollbackAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction != null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private sealed record NormalizedOrderItem(int ProductId, int Quantity);
}
