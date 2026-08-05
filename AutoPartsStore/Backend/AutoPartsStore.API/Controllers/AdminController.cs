using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly AutoPartsDbContext _context;
        private readonly OrderLifecycleService _orderLifecycleService;
        private readonly AdminAuditService _adminAuditService;
        private readonly AdminAuditIntentService _adminAuditIntentService;
        private readonly AdminAuditIntentOptions _adminAuditIntentOptions;

        public AdminController(
            AutoPartsDbContext context,
            OrderLifecycleService orderLifecycleService,
            AdminAuditService adminAuditService,
            AdminAuditIntentService? adminAuditIntentService = null,
            AdminAuditIntentOptions? adminAuditIntentOptions = null)
        {
            _context = context;
            _orderLifecycleService = orderLifecycleService;
            _adminAuditService = adminAuditService;
            _adminAuditIntentService = adminAuditIntentService ?? new AdminAuditIntentService(context);
            _adminAuditIntentOptions = adminAuditIntentOptions ?? new AdminAuditIntentOptions();
        }

        // GET: api/Admin/products
        [HttpGet("products")]
        [Authorize(Policy = AdminPolicyNames.Catalog)]
        public async Task<ActionResult<IEnumerable<object>>> GetAllProducts()
        {
            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.Brand)
                .Include(p => p.PartBrand)
                .ToListAsync();

            return products.Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                Brand = new { p.Brand.Id, p.Brand.Name, p.Brand.Slug },
                BrandId = p.BrandId,
                PartBrand = new { p.PartBrand.Id, p.PartBrand.Name, p.PartBrand.Slug },
                PartBrandId = p.PartBrandId,
                p.PartNumber,
                p.Price,
                p.OldPrice,
                p.Stock,
                p.ImageUrl,
                p.Rating,
                p.ReviewCount,
                p.DiscountPercentage,
                p.BadgeText,
                p.IsFeatured,
                p.IsNew,
                Category = new { p.Category.Id, p.Category.Name, p.Category.Slug },
                p.CategoryId,
                p.CreatedAt,
                p.UpdatedAt
            }).ToList();
        }

        // POST: api/Admin/products
        [HttpPost("products")]
        [Authorize(Policy = AdminPolicyNames.Catalog)]
        public async Task<ActionResult<Product>> CreateProduct(
            ProductCreateDto dto,
            CancellationToken cancellationToken = default)
        {
            var product = new Product
            {
                Name = dto.Name,
                Description = dto.Description,
                BrandId = dto.BrandId,
                PartBrandId = dto.PartBrandId,
                PartNumber = dto.PartNumber,
                Price = dto.Price,
                OldPrice = dto.OldPrice,
                Stock = dto.Stock,
                ImageUrl = dto.ImageUrl ?? "/images/products/default.jpg",
                Rating = 0,
                ReviewCount = 0,
                DiscountPercentage = dto.DiscountPercentage,
                BadgeText = dto.BadgeText,
                IsFeatured = dto.IsFeatured,
                IsNew = dto.IsNew,
                CategoryId = dto.CategoryId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            try
            {
                _context.Products.Add(product);
                await _context.SaveChangesAsync(cancellationToken);
                StageAuditIntent(
                    AdminAuditActions.ProductCreated,
                    AdminAuditAggregateTypes.Product,
                    product.Id,
                    AdminAuditOutcomes.Succeeded);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            await DispatchAuditBestEffortAsync(cancellationToken);

            return CreatedAtAction(nameof(GetAllProducts), new { id = product.Id }, product);
        }

        // PUT: api/Admin/products/5
        [HttpPut("products/{id}")]
        [Authorize(Policy = AdminPolicyNames.Catalog)]
        public async Task<IActionResult> UpdateProduct(
            int id,
            ProductUpdateDto dto,
            CancellationToken cancellationToken = default)
        {
            var product = await _context.Products
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (product == null)
            {
                return NotFound();
            }

            // Validate foreign keys
            var categoryExists = await _context.Categories.AnyAsync(
                c => c.Id == dto.CategoryId,
                cancellationToken);
            if (!categoryExists)
            {
                return BadRequest(new { error = $"Category with ID {dto.CategoryId} does not exist." });
            }

            var brandExists = await _context.Brands.AnyAsync(
                b => b.Id == dto.BrandId,
                cancellationToken);
            if (!brandExists)
            {
                return BadRequest(new { error = $"Brand with ID {dto.BrandId} does not exist." });
            }

            var partBrandExists = await _context.PartBrands.AnyAsync(
                pb => pb.Id == dto.PartBrandId,
                cancellationToken);
            if (!partBrandExists)
            {
                return BadRequest(new { error = $"PartBrand with ID {dto.PartBrandId} does not exist." });
            }

            // Update product
            product.Name = dto.Name;
            product.Description = dto.Description;
            product.BrandId = dto.BrandId;
            product.PartBrandId = dto.PartBrandId;
            product.PartNumber = dto.PartNumber;
            product.Price = dto.Price;
            product.OldPrice = dto.OldPrice;
            product.Stock = dto.Stock;
            product.ImageUrl = dto.ImageUrl ?? product.ImageUrl;
            product.DiscountPercentage = dto.DiscountPercentage;
            product.BadgeText = dto.BadgeText;
            product.IsFeatured = dto.IsFeatured;
            product.IsNew = dto.IsNew;
            product.CategoryId = dto.CategoryId;
            product.UpdatedAt = DateTime.UtcNow;

            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            try
            {
                _context.Products.Update(product);
                StageAuditIntent(
                    AdminAuditActions.ProductUpdated,
                    AdminAuditAggregateTypes.Product,
                    product.Id,
                    AdminAuditOutcomes.Succeeded);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            await DispatchAuditBestEffortAsync(cancellationToken);

            return NoContent();
        }

        // DELETE: api/Admin/products/5
        [HttpDelete("products/{id}")]
        [Authorize(Policy = AdminPolicyNames.Catalog)]
        public async Task<IActionResult> DeleteProduct(
            int id,
            CancellationToken cancellationToken = default)
        {
            var product = await _context.Products.FindAsync([id], cancellationToken);
            if (product == null)
            {
                return NotFound();
            }

            if (await _context.OrderItems.AnyAsync(
                    orderItem => orderItem.ProductId == id,
                    cancellationToken))
            {
                return Conflict(new
                {
                    message = "Sipariş geçmişinde kullanılan ürün silinemez; ürün arşivleme akışı kullanılmalıdır."
                });
            }

            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            try
            {
                _context.Products.Remove(product);
                StageAuditIntent(
                    AdminAuditActions.ProductDeleted,
                    AdminAuditAggregateTypes.Product,
                    id,
                    AdminAuditOutcomes.Succeeded);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch (DbUpdateException)
            {
                _context.Entry(product).State = EntityState.Unchanged;
                if (await _context.OrderItems
                        .AsNoTracking()
                        .AnyAsync(
                            orderItem => orderItem.ProductId == id,
                            cancellationToken))
                {
                    return Conflict(new
                    {
                        message = "Sipariş geçmişinde kullanılan ürün silinemez; ürün arşivleme akışı kullanılmalıdır."
                    });
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

            await DispatchAuditBestEffortAsync(cancellationToken);

            return NoContent();
        }

        // GET: api/Admin/orders
        [HttpGet("orders")]
        [Authorize(Policy = AdminPolicyNames.OperationsRead)]
        public async Task<ActionResult<IEnumerable<Order>>> GetAllOrders()
        {
            return await _context.Orders
                .Include(o => o.Payment)
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .OrderByDescending(o => o.OrderDate)
                .ToListAsync();
        }

        // GET: api/Admin/payments
        [HttpGet("payments")]
        [Authorize(Policy = AdminPolicyNames.Finance)]
        public async Task<ActionResult<IEnumerable<AdminPaymentDto>>> GetAllPayments()
        {
            return await _context.Payments
                .AsNoTracking()
                .OrderByDescending(payment => payment.CreatedAt)
                .Select(payment => new AdminPaymentDto
                {
                    Id = payment.Id,
                    OrderId = payment.OrderId,
                    OrderNumber = payment.Order.OrderNumber,
                    CustomerEmail = payment.Order.CustomerEmail,
                    Provider = payment.Provider,
                    Method = payment.Method,
                    Status = payment.Status,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    ProviderPaymentId = payment.ProviderPaymentId,
                    FailureCode = payment.FailureCode,
                    RefundedAmount = payment.Refunds
                        .Where(refund => refund.Status == RefundStatuses.Succeeded)
                        .Sum(refund => (decimal?)refund.Amount) ?? 0m,
                    PendingRefundAmount = payment.Refunds
                        .Where(refund =>
                            refund.Status == RefundStatuses.Requested ||
                            refund.Status == RefundStatuses.Processing ||
                            refund.Status == RefundStatuses.Unknown)
                        .Sum(refund => (decimal?)refund.Amount) ?? 0m,
                    CreatedAt = payment.CreatedAt,
                    UpdatedAt = payment.UpdatedAt,
                    PaidAt = payment.PaidAt
                })
                .ToListAsync();
        }

        // PUT: api/Admin/orders/5/status
        [HttpPut("orders/{id}/status")]
        [Authorize(Policy = AdminPolicyNames.Support)]
        public async Task<IActionResult> UpdateOrderStatus(
            int id,
            UpdateOrderStatusDto dto,
            CancellationToken cancellationToken)
        {
            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            OrderLifecycleResult result;
            try
            {
                result = await _orderLifecycleService.UpdateOrderStatusAsync(
                    id,
                    dto.Status,
                    cancellationToken);

                if (result.Outcome is OrderLifecycleOutcome.Updated or OrderLifecycleOutcome.Unchanged)
                {
                    StageAuditIntent(
                        AdminAuditActions.ForOrderStatus(dto.Status),
                        AdminAuditAggregateTypes.Order,
                        id,
                        result.Outcome == OrderLifecycleOutcome.Unchanged
                            ? AdminAuditOutcomes.Replayed
                            : AdminAuditOutcomes.Succeeded);
                    await _context.SaveChangesAsync(cancellationToken);

                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            if (result.Outcome is OrderLifecycleOutcome.Updated or OrderLifecycleOutcome.Unchanged)
            {
                await DispatchAuditBestEffortAsync(cancellationToken);
            }

            return result.Outcome switch
            {
                OrderLifecycleOutcome.Updated => NoContent(),
                OrderLifecycleOutcome.Unchanged => NoContent(),
                OrderLifecycleOutcome.NotFound => NotFound(),
                OrderLifecycleOutcome.InvalidTransition => Conflict(new { message = result.Message }),
                _ => StatusCode(StatusCodes.Status500InternalServerError)
            };
        }

        // POST: api/Admin/payments/5/mark-paid
        [HttpPost("payments/{id}/mark-paid")]
        [Authorize(Policy = AdminPolicyNames.Finance)]
        public async Task<IActionResult> MarkPaymentPaid(
            int id,
            CancellationToken cancellationToken)
        {
            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            PaymentLifecycleResult result;
            try
            {
                result = await _orderLifecycleService.MarkManualPaymentPaidAsync(
                    id,
                    cancellationToken);

                if (result.Outcome is PaymentLifecycleOutcome.Updated or PaymentLifecycleOutcome.Unchanged)
                {
                    StageAuditIntent(
                        AdminAuditActions.PaymentMarkedPaid,
                        AdminAuditAggregateTypes.Payment,
                        id,
                        result.Outcome == PaymentLifecycleOutcome.Unchanged
                            ? AdminAuditOutcomes.Replayed
                            : AdminAuditOutcomes.Succeeded);
                    await _context.SaveChangesAsync(cancellationToken);

                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            if (result.Outcome is PaymentLifecycleOutcome.Updated or PaymentLifecycleOutcome.Unchanged)
            {
                await DispatchAuditBestEffortAsync(cancellationToken);
            }

            return result.Outcome switch
            {
                PaymentLifecycleOutcome.Updated => NoContent(),
                PaymentLifecycleOutcome.Unchanged => NoContent(),
                PaymentLifecycleOutcome.NotFound => NotFound(),
                PaymentLifecycleOutcome.InvalidTransition => Conflict(new { message = result.Message }),
                _ => StatusCode(StatusCodes.Status500InternalServerError)
            };
        }

        // GET: api/Admin/users
        [HttpGet("users")]
        [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
        public async Task<ActionResult<IEnumerable<AdminUserDto>>> GetAllUsers()
        {
            return await _context.Users
                .AsNoTracking()
                .OrderByDescending(user => user.CreatedAt)
                .Select(user => new AdminUserDto
                {
                    Id = user.Id,
                    Email = user.Email,
                    FullName = user.FullName,
                    Phone = user.Phone,
                    Role = user.Role,
                    IsActive = user.IsActive,
                    CreatedAt = user.CreatedAt
                })
                .ToListAsync();
        }

        // PUT: api/Admin/users/5/role
        [HttpPut("users/{id:int}/role")]
        [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
        public async Task<IActionResult> UpdateUserRole(
            int id,
            UpdateUserRoleDto dto,
            CancellationToken cancellationToken)
        {
            var normalizedRole = SupportedUserRoles.FirstOrDefault(role =>
                string.Equals(role, dto.Role?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (normalizedRole == null)
            {
                return BadRequest(new { message = "Desteklenmeyen kullanıcı rolü." });
            }

            var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            try
            {
                var user = await _context.Users.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    cancellationToken);
                if (user == null)
                {
                    return NotFound();
                }

                var auditOutcome = AdminAuditOutcomes.Replayed;
                if (!string.Equals(user.Role, normalizedRole, StringComparison.Ordinal))
                {
                    if (IsPrivilegedAdministrator(user.Role) &&
                        !IsPrivilegedAdministrator(normalizedRole))
                    {
                        var privilegedAdministrators = await _context.Users.CountAsync(
                            candidate => candidate.IsActive &&
                                (candidate.Role == AdminAuditRoles.LegacyAdmin ||
                                 candidate.Role == AdminAuditRoles.SuperAdmin),
                            cancellationToken);
                        if (privilegedAdministrators <= 1)
                        {
                            return Conflict(new { message = "Son yetkili yönetici daha düşük bir role geçirilemez." });
                        }
                    }

                    user.Role = normalizedRole;
                    auditOutcome = AdminAuditOutcomes.Succeeded;
                }

                StageAuditIntent(
                    AdminAuditActions.UserRoleChanged,
                    AdminAuditAggregateTypes.User,
                    id,
                    auditOutcome);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            await DispatchAuditBestEffortAsync(cancellationToken);
            return NoContent();
        }

        // GET: api/Admin/stats
        [HttpGet("stats")]
        [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
        public async Task<ActionResult<DashboardStats>> GetStats()
        {
            var totalProducts = await _context.Products.CountAsync();
            var totalOrders = await _context.Orders.CountAsync();
            var totalUsers = await _context.Users.CountAsync();
            var grossRevenue = await _context.Payments
                .Where(payment =>
                    payment.Status == PaymentStatuses.Paid ||
                    payment.Status == PaymentStatuses.PartiallyRefunded ||
                    payment.Status == PaymentStatuses.Refunded)
                .SumAsync(payment => (decimal?)payment.Amount) ?? 0m;
            var refundedAmount = await _context.Refunds
                .Where(refund => refund.Status == RefundStatuses.Succeeded)
                .SumAsync(refund => (decimal?)refund.Amount) ?? 0m;
            var pendingOrders = await _context.Orders.CountAsync(o => o.Status == "Pending");
            var pendingPayments = await _context.Payments
                .CountAsync(payment => payment.Status == PaymentStatuses.Pending);

            return Ok(new DashboardStats
            {
                TotalProducts = totalProducts,
                TotalOrders = totalOrders,
                TotalUsers = totalUsers,
                TotalRevenue = grossRevenue - refundedAmount,
                GrossRevenue = grossRevenue,
                RefundedAmount = refundedAmount,
                PendingOrders = pendingOrders,
                PendingPayments = pendingPayments
            });
        }

        private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
            CancellationToken cancellationToken)
        {
            if (!_context.Database.IsRelational() ||
                _context.Database.CurrentTransaction != null)
            {
                return null;
            }

            return await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        }

        private void StageAuditIntent(
            string action,
            string aggregateType,
            long aggregateId,
            string outcome)
        {
            var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var actorRole = User.FindFirstValue(ClaimTypes.Role);
            if (!int.TryParse(actorClaim, out var actorUserId) ||
                string.IsNullOrWhiteSpace(actorRole))
            {
                throw new InvalidOperationException("Authenticated admin audit identity is missing.");
            }

            var correlationId = HttpContext.TraceIdentifier;
            var result = _adminAuditIntentService.Stage(
                new AdminAuditIntentStageRequest(
                    Guid.NewGuid(),
                    actorUserId,
                    actorRole,
                    action,
                    aggregateType,
                    aggregateId,
                    correlationId,
                    outcome));
            if (result.Outcome == AdminAuditIntentStageOutcome.InvalidRequest)
            {
                throw new InvalidOperationException($"Admin audit intent staging failed: {result.ErrorCode}");
            }
        }

        private async Task DispatchAuditBestEffortAsync(CancellationToken cancellationToken)
        {
            if (_context.Database.CurrentTransaction != null)
            {
                return;
            }

            try
            {
                await _adminAuditIntentService.DispatchBatchAsync(
                    _adminAuditService,
                    _adminAuditIntentOptions,
                    cancellationToken);
            }
            catch (Exception)
            {
                // The committed intent is durable and will be retried by the worker.
            }
        }

        private static readonly IReadOnlyList<string> SupportedUserRoles = Array.AsReadOnly(
        [
            "User",
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Finance,
            AdminAuditRoles.Warehouse,
            AdminAuditRoles.Catalog,
            AdminAuditRoles.Support,
            AdminAuditRoles.SuperAdmin
        ]);

        private static bool IsPrivilegedAdministrator(string role) =>
            role is AdminAuditRoles.LegacyAdmin or AdminAuditRoles.SuperAdmin;
    }

    // DTOs
    public sealed class UpdateUserRoleDto
    {
        [Required]
        [StringLength(20)]
        public string Role { get; set; } = string.Empty;
    }

    public class ProductCreateDto
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int BrandId { get; set; }
        public int PartBrandId { get; set; }
        public string PartNumber { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal? OldPrice { get; set; }
        public int Stock { get; set; }
        public string? ImageUrl { get; set; }
        public int? DiscountPercentage { get; set; }
        public string? BadgeText { get; set; }
        public bool IsFeatured { get; set; }
        public bool IsNew { get; set; }
        public int CategoryId { get; set; }
    }

    public class ProductUpdateDto : ProductCreateDto
    {
    }

    public class UpdateOrderStatusDto
    {
        [Required]
        [RegularExpression("^(Processing|Cancelled)$")]
        public string Status { get; set; } = string.Empty;
    }

    public class DashboardStats
    {
        public int TotalProducts { get; set; }
        public int TotalOrders { get; set; }
        public int TotalUsers { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal GrossRevenue { get; set; }
        public decimal RefundedAmount { get; set; }
        public int PendingOrders { get; set; }
        public int PendingPayments { get; set; }
    }

    public sealed class AdminPaymentDto
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string? ProviderPaymentId { get; set; }
        public string? FailureCode { get; set; }
        public decimal RefundedAmount { get; set; }
        public decimal PendingRefundAmount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? PaidAt { get; set; }
    }

    public sealed class AdminUserDto
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
