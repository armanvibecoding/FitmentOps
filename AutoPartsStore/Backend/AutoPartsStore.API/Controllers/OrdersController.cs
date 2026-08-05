using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace AutoPartsStore.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class OrdersController : ControllerBase
    {
        private readonly AutoPartsDbContext _context;
        private readonly EmailService _emailService;
        private readonly CheckoutService _checkoutService;
        private readonly HostedCheckoutService? _hostedCheckoutService;
        private readonly HostedCheckoutEndpointOptions _hostedCheckoutEndpointOptions;
        private readonly ILogger<OrdersController> _logger;

        public OrdersController(
            AutoPartsDbContext context,
            EmailService emailService,
            CheckoutService checkoutService,
            ILogger<OrdersController> logger,
            HostedCheckoutService? hostedCheckoutService = null,
            HostedCheckoutEndpointOptions? hostedCheckoutEndpointOptions = null)
        {
            _context = context;
            _emailService = emailService;
            _checkoutService = checkoutService;
            _logger = logger;
            _hostedCheckoutService = hostedCheckoutService;
            _hostedCheckoutEndpointOptions = hostedCheckoutEndpointOptions ?? new();
        }

        // GET: api/Orders
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Order>>> GetOrders()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var roleClaim = User.FindFirst(ClaimTypes.Role)?.Value;

            // Adminler tüm siparişleri görebilir
            if (roleClaim == "Admin")
            {
                return await _context.Orders
                    .Include(o => o.Payment)
                    .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Product)
                    .ToListAsync();
            }

            // Normal kullanıcılar sadece kendi siparişlerini görebilir
            if (!int.TryParse(userIdClaim, out int userId))
            {
                return Unauthorized();
            }

            return await _context.Orders
                .Where(o => o.UserId == userId)
                .Include(o => o.Payment)
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .ToListAsync();
        }

        // GET: api/Orders/5
        [HttpGet("{id}")]
        public async Task<ActionResult<Order>> GetOrder(int id)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var roleClaim = User.FindFirst(ClaimTypes.Role)?.Value;

            var order = await _context.Orders
                .Include(o => o.Payment)
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null)
            {
                return NotFound();
            }

            // Adminler tüm siparişleri görebilir, normal kullanıcılar sadece kendi siparişlerini
            if (roleClaim != "Admin")
            {
                if (!int.TryParse(userIdClaim, out int userId) || order.UserId != userId)
                {
                    return Forbid();
                }
            }

            return order;
        }

        // GET: api/Orders/number/ORD-123456
        // Authenticated users can only access their own orders. Guest tracking uses POST /track.
        [HttpGet("number/{orderNumber}")]
        public async Task<ActionResult<Order>> GetOrderByNumber(string orderNumber)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var roleClaim = User.FindFirst(ClaimTypes.Role)?.Value;

            var order = await _context.Orders
                .Include(o => o.Payment)
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

            if (order == null)
            {
                return NotFound();
            }

            if (roleClaim != "Admin" &&
                (!int.TryParse(userIdClaim, out var userId) || order.UserId != userId))
            {
                return Forbid();
            }

            return order;
        }

        // POST: api/Orders/track
        // Returns only the data needed for guest tracking and never exposes address or phone data.
        [HttpPost("track")]
        [AllowAnonymous]
        [EnableRateLimiting("order-tracking")]
        public async Task<ActionResult<OrderTrackingResponseDto>> TrackOrder(TrackOrderDto dto)
        {
            var orderNumber = dto.OrderNumber.Trim();
            var normalizedEmail = dto.Email.Trim().ToLowerInvariant();

            var order = await _context.Orders
                .AsNoTracking()
                .Where(o => o.OrderNumber == orderNumber &&
                            o.CustomerEmail.ToLower() == normalizedEmail)
                .Select(o => new OrderTrackingResponseDto
                {
                    OrderNumber = o.OrderNumber,
                    OrderDate = o.OrderDate,
                    Status = o.Status,
                    PaymentStatus = o.Payment != null ? o.Payment.Status : "Unknown",
                    TotalAmount = o.TotalAmount,
                    Items = o.OrderItems.Select(item => new OrderTrackingItemDto
                    {
                        ProductName = item.Product.Name,
                        Quantity = item.Quantity,
                        UnitPrice = item.Price
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (order == null)
            {
                return NotFound(new
                {
                    message = "Sipariş numarası veya e-posta adresi eşleşmedi."
                });
            }

            return Ok(order);
        }

        // POST: api/Orders
        [HttpPost]
        [AllowAnonymous]
        public async Task<ActionResult<CheckoutResponseDto>> PostOrder(
            CreateOrderDto orderDto,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (!IsValidIdempotencyKey(idempotencyKey))
            {
                return BadRequest(new
                {
                    message = "Idempotency-Key başlığı 16-100 karakter olmalı ve yalnızca harf, rakam, tire veya alt çizgi içermelidir."
                });
            }

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var result = await _checkoutService.CreateOrderAsync(
                orderDto,
                idempotencyKey!,
                userId,
                cancellationToken);

            if (result.Outcome == CheckoutOutcome.Created && result.Order != null)
            {
                try
                {
                    await _emailService.SendOrderConfirmationEmail(result.Order);
                    foreach (var item in result.Order.OrderItems)
                    {
                        if (item.Product != null && item.Product.Stock <= 10)
                        {
                            await _emailService.SendLowStockAlert(item.Product);
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Order {OrderNumber} was created, but its notification could not be sent.",
                        result.Order.OrderNumber);
                }
            }

            return result.Outcome switch
            {
                CheckoutOutcome.Created => StatusCode(
                    StatusCodes.Status201Created,
                    ToCheckoutResponse(result.Order!, replayed: false)),
                CheckoutOutcome.Replayed => Ok(ToCheckoutResponse(result.Order!, replayed: true)),
                CheckoutOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
                CheckoutOutcome.ConfigurationUnavailable => StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { message = result.Message }),
                CheckoutOutcome.IdempotencyConflict => Conflict(new { message = result.Message }),
                CheckoutOutcome.InventoryUnavailable => Conflict(new { message = result.Message }),
                _ => StatusCode(StatusCodes.Status500InternalServerError)
            };
        }

        // POST: api/Orders/hosted-checkout
        [HttpPost("hosted-checkout")]
        [AllowAnonymous]
        [EnableRateLimiting("checkout-initialization")]
        public async Task<ActionResult<HostedCheckoutResponseDto>> StartHostedCheckout(
            CreateHostedCheckoutDto dto,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (!IsValidIdempotencyKey(idempotencyKey))
            {
                return BadRequest(new
                {
                    message = "Idempotency-Key must contain 16 to 100 letters, numbers, dashes or underscores."
                });
            }

            if (_hostedCheckoutService == null ||
                !_hostedCheckoutEndpointOptions.TryGetTrustedUris(
                    out var callbackUri,
                    out var returnUri))
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { message = "Hosted payment initialization is not configured." });
            }

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var buyerReference = userId?.ToString() ?? CreateGuestBuyerReference(idempotencyKey!);
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
            var contactName = $"{dto.FirstName.Trim()} {dto.LastName.Trim()}";
            var address = new PaymentAddressContext(
                contactName,
                dto.ShippingAddress.Trim(),
                dto.City.Trim(),
                "Türkiye",
                dto.PostalCode.Trim());
            var result = await _hostedCheckoutService.StartAsync(
                new HostedCheckoutCommand(
                    idempotencyKey!,
                    dto.Items.Select(item => new InventoryReservationLine(
                        item.ProductId,
                        item.Quantity)).ToArray(),
                    new HostedCheckoutCustomer(
                        contactName,
                        dto.Email.Trim(),
                        dto.Phone.Trim(),
                        dto.ShippingAddress.Trim(),
                        dto.City.Trim(),
                        dto.PostalCode.Trim()),
                    callbackUri,
                    returnUri,
                    userId,
                    new PaymentBuyerContext(
                        buyerReference,
                        dto.FirstName.Trim(),
                        dto.LastName.Trim(),
                        dto.Email.Trim(),
                        dto.Phone.Trim(),
                        dto.IdentityNumber.Trim(),
                        remoteIp),
                    address,
                    address,
                    dto.LegalAcceptances),
                cancellationToken);

            var response = ToHostedCheckoutResponse(result);
            return result.Outcome switch
            {
                HostedCheckoutOutcome.RequiresCustomerAction when !result.Replayed =>
                    StatusCode(StatusCodes.Status201Created, response),
                HostedCheckoutOutcome.RequiresCustomerAction => Ok(response),
                HostedCheckoutOutcome.PendingReconciliation => Accepted(response),
                HostedCheckoutOutcome.Declined =>
                    StatusCode(StatusCodes.Status402PaymentRequired, response),
                HostedCheckoutOutcome.ProviderDisabled =>
                    StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                HostedCheckoutOutcome.ConfigurationUnavailable =>
                    StatusCode(StatusCodes.Status503ServiceUnavailable, response),
                HostedCheckoutOutcome.InventoryUnavailable => Conflict(response),
                HostedCheckoutOutcome.Conflict => Conflict(response),
                HostedCheckoutOutcome.InvalidRequest => BadRequest(response),
                _ => StatusCode(StatusCodes.Status500InternalServerError, response)
            };
        }

        private static bool IsValidIdempotencyKey(string? idempotencyKey)
        {
            return idempotencyKey is { Length: >= 16 and <= 100 } &&
                   idempotencyKey.All(character =>
                       char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        }

        private static CheckoutResponseDto ToCheckoutResponse(Order order, bool replayed)
        {
            return new CheckoutResponseDto
            {
                OrderId = order.Id,
                OrderNumber = order.OrderNumber,
                OrderStatus = order.Status,
                TotalAmount = order.TotalAmount,
                Currency = order.Payment?.Currency ?? "TRY",
                PaymentMethod = order.Payment?.Method ?? string.Empty,
                PaymentStatus = order.Payment?.Status ?? string.Empty,
                Replayed = replayed
            };
        }

        private static HostedCheckoutResponseDto ToHostedCheckoutResponse(
            HostedCheckoutResult result) => new()
            {
                Outcome = result.Outcome.ToString(),
                Replayed = result.Replayed,
                OrderId = result.OrderId,
                OrderNumber = result.OrderNumber,
                OrderStatus = result.OrderStatus,
                PaymentStatus = result.PaymentStatus,
                AttemptStatus = result.AttemptStatus,
                RedirectUri = result.RedirectUri,
                Message = result.Message
            };

        private static string CreateGuestBuyerReference(string idempotencyKey)
        {
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));
            return $"guest-{digest[..16].ToLowerInvariant()}";
        }
    }

    // DTOs
    public sealed class TrackOrderDto
    {
        [Required]
        [StringLength(50)]
        public string OrderNumber { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;
    }

    public sealed class OrderTrackingResponseDto
    {
        public string OrderNumber { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string Status { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public List<OrderTrackingItemDto> Items { get; set; } = new();
    }

    public sealed class OrderTrackingItemDto
    {
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
