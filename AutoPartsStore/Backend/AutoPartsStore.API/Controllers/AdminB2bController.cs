using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AutoPartsStore.API.Controllers;

[Route("api/admin/b2b")]
[ApiController]
[Authorize]
public sealed class AdminB2bController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly DealerApplicationService _applicationService;
    private readonly BulkQuoteService _quoteService;
    private readonly SupplierSourcingService _sourcingService;
    private readonly AdminAuditIntentService _auditIntentService;
    private readonly AdminAuditService _auditService;
    private readonly AdminAuditIntentOptions _auditOptions;
    private readonly ILogger<AdminB2bController> _logger;

    public AdminB2bController(
        AutoPartsDbContext context,
        DealerApplicationService applicationService,
        BulkQuoteService quoteService,
        SupplierSourcingService sourcingService,
        AdminAuditIntentService auditIntentService,
        AdminAuditService auditService,
        AdminAuditIntentOptions auditOptions,
        ILogger<AdminB2bController> logger)
    {
        _context = context;
        _applicationService = applicationService;
        _quoteService = quoteService;
        _sourcingService = sourcingService;
        _auditIntentService = auditIntentService;
        _auditService = auditService;
        _auditOptions = auditOptions;
        _logger = logger;
    }

    [HttpGet("applications")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public async Task<IActionResult> GetApplications(CancellationToken cancellationToken)
    {
        var applications = await _context.DealerApplications
            .AsNoTracking()
            .OrderByDescending(application => application.CreatedAtUtc)
            .Take(200)
            .Select(application => new
            {
                application.Id,
                application.UserId,
                application.CompanyName,
                application.TaxNumber,
                application.ContactName,
                application.ContactEmail,
                application.ContactPhone,
                application.Status,
                application.CustomerGroupId,
                CustomerGroup = application.CustomerGroup == null
                    ? null
                    : application.CustomerGroup.Name,
                application.CreatedAtUtc,
                application.ReviewedAtUtc
            })
            .ToListAsync(cancellationToken);
        return Ok(applications);
    }

    [HttpPut("applications/{id:long}/review")]
    [Authorize(Policy = AdminPolicyNames.SuperAdmin)]
    public Task<IActionResult> ReviewApplication(
        long id,
        ReviewDealerApplicationDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                if (!Enum.TryParse<DealerReviewDecision>(dto.Decision, true, out var decision))
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Invalid dealer review decision." }));
                }

                var result = await _applicationService.ReviewAsync(
                    id,
                    decision,
                    dto.CustomerGroupId,
                    token);
                var response = new { result.ApplicationId, result.Status, result.Message };
                return result.Outcome switch
                {
                    DealerApplicationOutcome.Updated => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.DealerApplicationReviewed,
                        AdminAuditAggregateTypes.DealerApplication,
                        id,
                        AdminAuditOutcomes.Succeeded),
                    DealerApplicationOutcome.NotFound => Mutation.NoAudit(NotFound(response)),
                    DealerApplicationOutcome.Conflict => Mutation.NoAudit(Conflict(response)),
                    DealerApplicationOutcome.InvalidRequest => Mutation.NoAudit(BadRequest(response)),
                    _ => Mutation.NoAudit(StatusCode(StatusCodes.Status500InternalServerError))
                };
            },
            cancellationToken);

    [HttpGet("pricing")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public async Task<IActionResult> GetPricing(CancellationToken cancellationToken)
    {
        var groups = await _context.CustomerGroups
            .AsNoTracking()
            .OrderBy(group => group.Priority)
            .ThenBy(group => group.Code)
            .Select(group => new
            {
                group.Id,
                group.Code,
                group.Name,
                group.IsActive,
                group.Priority,
                group.CreatedAtUtc,
                group.UpdatedAtUtc,
                group.ConcurrencyToken
            })
            .ToListAsync(cancellationToken);
        var lists = await _context.PriceLists
            .AsNoTracking()
            .OrderBy(list => list.Code)
            .Select(list => new
            {
                list.Id,
                list.Code,
                list.Name,
                list.CustomerGroupId,
                CustomerGroup = list.CustomerGroup.Name,
                list.Currency,
                list.IsActive,
                list.ValidFromUtc,
                list.ValidToUtc,
                list.ConcurrencyToken
            })
            .ToListAsync(cancellationToken);
        var rules = await _context.PriceRules
            .AsNoTracking()
            .OrderBy(rule => rule.PriceListId)
            .ThenBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .Select(rule => new
            {
                rule.Id,
                rule.PriceListId,
                PriceList = rule.PriceList.Name,
                rule.ProductId,
                Product = rule.Product == null ? null : rule.Product.Name,
                rule.BrandId,
                Brand = rule.Brand == null ? null : rule.Brand.Name,
                rule.CategoryId,
                Category = rule.Category == null ? null : rule.Category.Name,
                rule.MinimumQuantity,
                rule.MinimumPeriodRevenue,
                rule.Priority,
                rule.DiscountPercentage,
                rule.FixedUnitPrice,
                rule.ValidFromUtc,
                rule.ValidToUtc,
                rule.IsActive,
                rule.ConcurrencyToken
            })
            .ToListAsync(cancellationToken);

        return Ok(new { groups, lists, rules });
    }

    [HttpPost("customer-groups")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> CreateCustomerGroup(
        CreateCustomerGroupDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var code = dto.Code.Trim().ToUpperInvariant();
                if (await _context.CustomerGroups.AnyAsync(group => group.Code == code, token))
                {
                    return Mutation.NoAudit(Conflict(new { message = "Customer group code already exists." }));
                }

                var now = DateTime.UtcNow;
                var group = new CustomerGroup
                {
                    Code = code,
                    Name = dto.Name.Trim(),
                    IsActive = dto.IsActive,
                    Priority = dto.Priority,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _context.CustomerGroups.Add(group);
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    StatusCode(StatusCodes.Status201Created, new { group.Id, group.Code, group.Name }),
                    AdminAuditActions.CustomerGroupUpserted,
                    AdminAuditAggregateTypes.CustomerGroup,
                    group.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPut("customer-groups/{id:long}")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> UpdateCustomerGroup(
        long id,
        UpdateCustomerGroupDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var group = await _context.CustomerGroups.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    token);
                if (group == null)
                {
                    return Mutation.NoAudit(NotFound(new { message = "Customer group not found." }));
                }

                if (group.ConcurrencyToken != dto.ConcurrencyToken)
                {
                    return Mutation.NoAudit(Conflict(new { message = "Customer group changed; reload and retry." }));
                }

                group.Name = dto.Name.Trim();
                group.IsActive = dto.IsActive;
                group.Priority = dto.Priority;
                group.UpdatedAtUtc = DateTime.UtcNow;
                group.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    Ok(new { group.Id, group.Code, group.Name, group.IsActive, group.Priority, group.ConcurrencyToken }),
                    AdminAuditActions.CustomerGroupUpserted,
                    AdminAuditAggregateTypes.CustomerGroup,
                    group.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPost("price-lists")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> CreatePriceList(
        CreatePriceListDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var code = dto.Code.Trim().ToUpperInvariant();
                var groupExists = await _context.CustomerGroups.AsNoTracking().AnyAsync(
                    group => group.Id == dto.CustomerGroupId && group.IsActive,
                    token);
                if (!groupExists)
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Active customer group not found." }));
                }

                if (await _context.PriceLists.AnyAsync(list => list.Code == code, token))
                {
                    return Mutation.NoAudit(Conflict(new { message = "Price list code already exists." }));
                }

                var list = new PriceList
                {
                    Code = code,
                    Name = dto.Name.Trim(),
                    CustomerGroupId = dto.CustomerGroupId,
                    Currency = "TRY",
                    IsActive = dto.IsActive,
                    ValidFromUtc = dto.ValidFromUtc.UtcDateTime,
                    ValidToUtc = dto.ValidToUtc?.UtcDateTime
                };
                _context.PriceLists.Add(list);
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    StatusCode(StatusCodes.Status201Created, new { list.Id, list.Code, list.Name }),
                    AdminAuditActions.PriceListUpserted,
                    AdminAuditAggregateTypes.PriceList,
                    list.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPut("price-lists/{id:long}")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> UpdatePriceList(
        long id,
        UpdatePriceListDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var list = await _context.PriceLists.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    token);
                if (list == null)
                {
                    return Mutation.NoAudit(NotFound(new { message = "Price list not found." }));
                }

                if (list.ConcurrencyToken != dto.ConcurrencyToken)
                {
                    return Mutation.NoAudit(Conflict(new { message = "Price list changed; reload and retry." }));
                }

                var groupExists = await _context.CustomerGroups.AsNoTracking().AnyAsync(
                    group => group.Id == dto.CustomerGroupId && group.IsActive,
                    token);
                if (!groupExists || (dto.ValidToUtc.HasValue && dto.ValidToUtc <= dto.ValidFromUtc))
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Price list fields are invalid." }));
                }

                list.Name = dto.Name.Trim();
                list.CustomerGroupId = dto.CustomerGroupId;
                list.IsActive = dto.IsActive;
                list.ValidFromUtc = dto.ValidFromUtc.UtcDateTime;
                list.ValidToUtc = dto.ValidToUtc?.UtcDateTime;
                list.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    Ok(new { list.Id, list.Code, list.Name, list.IsActive, list.ConcurrencyToken }),
                    AdminAuditActions.PriceListUpserted,
                    AdminAuditAggregateTypes.PriceList,
                    list.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPost("price-rules")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> CreatePriceRule(
        CreatePriceRuleDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var listExists = await _context.PriceLists.AsNoTracking().AnyAsync(
                    list => list.Id == dto.PriceListId,
                    token);
                if (!listExists ||
                    (dto.DiscountPercentage.HasValue == dto.FixedUnitPrice.HasValue) ||
                    dto.ValidToUtc <= dto.ValidFromUtc)
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Price rule fields are invalid." }));
                }

                var rule = new PriceRule
                {
                    PriceListId = dto.PriceListId,
                    ProductId = dto.ProductId,
                    BrandId = dto.BrandId,
                    CategoryId = dto.CategoryId,
                    MinimumQuantity = dto.MinimumQuantity,
                    MinimumPeriodRevenue = dto.MinimumPeriodRevenue,
                    Priority = dto.Priority,
                    DiscountPercentage = dto.DiscountPercentage,
                    FixedUnitPrice = dto.FixedUnitPrice,
                    ValidFromUtc = dto.ValidFromUtc.UtcDateTime,
                    ValidToUtc = dto.ValidToUtc?.UtcDateTime,
                    IsActive = dto.IsActive
                };
                _context.PriceRules.Add(rule);
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    StatusCode(StatusCodes.Status201Created, new { rule.Id }),
                    AdminAuditActions.PriceRuleUpserted,
                    AdminAuditAggregateTypes.PriceRule,
                    rule.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPut("price-rules/{id:long}")]
    [Authorize(Policy = AdminPolicyNames.Finance)]
    public Task<IActionResult> UpdatePriceRule(
        long id,
        UpdatePriceRuleDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var rule = await _context.PriceRules.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    token);
                if (rule == null)
                {
                    return Mutation.NoAudit(NotFound(new { message = "Price rule not found." }));
                }

                if (rule.ConcurrencyToken != dto.ConcurrencyToken)
                {
                    return Mutation.NoAudit(Conflict(new { message = "Price rule changed; reload and retry." }));
                }

                var listExists = await _context.PriceLists.AsNoTracking().AnyAsync(
                    list => list.Id == dto.PriceListId,
                    token);
                var productExists = !dto.ProductId.HasValue || await _context.Products.AsNoTracking().AnyAsync(
                    product => product.Id == dto.ProductId,
                    token);
                var brandExists = !dto.BrandId.HasValue || await _context.Brands.AsNoTracking().AnyAsync(
                    brand => brand.Id == dto.BrandId,
                    token);
                var categoryExists = !dto.CategoryId.HasValue || await _context.Categories.AsNoTracking().AnyAsync(
                    category => category.Id == dto.CategoryId,
                    token);
                if (!listExists || !productExists || !brandExists || !categoryExists ||
                    (dto.DiscountPercentage.HasValue == dto.FixedUnitPrice.HasValue) ||
                    (dto.ValidToUtc.HasValue && dto.ValidToUtc <= dto.ValidFromUtc))
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Price rule fields are invalid." }));
                }

                rule.PriceListId = dto.PriceListId;
                rule.ProductId = dto.ProductId;
                rule.BrandId = dto.BrandId;
                rule.CategoryId = dto.CategoryId;
                rule.MinimumQuantity = dto.MinimumQuantity;
                rule.MinimumPeriodRevenue = dto.MinimumPeriodRevenue;
                rule.Priority = dto.Priority;
                rule.DiscountPercentage = dto.DiscountPercentage;
                rule.FixedUnitPrice = dto.FixedUnitPrice;
                rule.ValidFromUtc = dto.ValidFromUtc.UtcDateTime;
                rule.ValidToUtc = dto.ValidToUtc?.UtcDateTime;
                rule.IsActive = dto.IsActive;
                rule.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    Ok(new { rule.Id, rule.IsActive, rule.ConcurrencyToken }),
                    AdminAuditActions.PriceRuleUpserted,
                    AdminAuditAggregateTypes.PriceRule,
                    rule.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpGet("quotes")]
    [Authorize(Policy = AdminPolicyNames.Support)]
    public async Task<IActionResult> GetQuotes(CancellationToken cancellationToken)
    {
        var requests = await _context.BulkQuoteRequests
            .AsNoTracking()
            .OrderByDescending(request => request.CreatedAtUtc)
            .Take(200)
            .Select(request => new
            {
                request.Id,
                request.RequestNumber,
                request.UserId,
                request.Status,
                request.Currency,
                request.CreatedAtUtc,
                request.QuoteValidUntilUtc,
                Lines = request.Lines.OrderBy(line => line.LineNumber).Select(line => new
                {
                    line.Id,
                    line.LineNumber,
                    line.RequestedIdentifier,
                    line.RequestedQuantity,
                    line.ProductId,
                    line.Status,
                    line.QuotedUnitPrice,
                    line.AvailableQuantity,
                    line.LeadTimeDays
                })
            })
            .ToListAsync(cancellationToken);
        return Ok(requests);
    }

    [HttpPut("quotes/{id:long}/quote")]
    [Authorize(Policy = AdminPolicyNames.Support)]
    public Task<IActionResult> PrepareQuote(
        long id,
        PrepareBulkQuoteDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var result = await _quoteService.PrepareQuoteAsync(
                    id,
                    dto.Lines.Select(line => new BulkQuoteOfferLine(
                        line.LineId,
                        line.UnitPrice,
                        line.AvailableQuantity,
                        line.LeadTimeDays)).ToArray(),
                    dto.ValidUntilUtc,
                    token);
                var response = new
                {
                    result.RequestId,
                    result.RequestNumber,
                    result.Status,
                    result.Replayed,
                    result.Message
                };
                return result.Outcome switch
                {
                    BulkQuoteOutcome.Updated => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.BulkQuotePrepared,
                        AdminAuditAggregateTypes.BulkQuote,
                        id,
                        AdminAuditOutcomes.Succeeded),
                    BulkQuoteOutcome.Replayed => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.BulkQuotePrepared,
                        AdminAuditAggregateTypes.BulkQuote,
                        id,
                        AdminAuditOutcomes.Replayed),
                    BulkQuoteOutcome.NotFound => Mutation.NoAudit(NotFound(response)),
                    BulkQuoteOutcome.Conflict => Mutation.NoAudit(Conflict(response)),
                    BulkQuoteOutcome.InvalidRequest => Mutation.NoAudit(BadRequest(response)),
                    _ => Mutation.NoAudit(StatusCode(StatusCodes.Status500InternalServerError))
                };
            },
            cancellationToken);

    [HttpGet("suppliers")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public async Task<IActionResult> GetSuppliers(CancellationToken cancellationToken)
    {
        var suppliers = await _context.Suppliers
            .AsNoTracking()
            .OrderBy(supplier => supplier.Priority)
            .ThenBy(supplier => supplier.Code)
            .Take(200)
            .Select(supplier => new
            {
                supplier.Id,
                supplier.Code,
                supplier.Name,
                supplier.IsActive,
                supplier.HealthStatus,
                supplier.Priority,
                supplier.CreatedAtUtc,
                supplier.UpdatedAtUtc,
                supplier.ConcurrencyToken,
                Offers = supplier.Offers
                    .OrderByDescending(offer => offer.CreatedAtUtc)
                    .Take(200)
                    .Select(offer => new
                    {
                        offer.Id,
                        offer.ExternalOfferId,
                        offer.ProductId,
                        Product = offer.Product.Name,
                        offer.OemNumber,
                        offer.Currency,
                        offer.UnitCost,
                        offer.ShippingCost,
                        offer.AvailableQuantity,
                        offer.LeadTimeDays,
                        offer.MinimumOrderQuantity,
                        offer.ValidUntilUtc,
                        offer.CanDropship,
                        offer.CanSupplyWarehouse,
                        offer.IsActive,
                        offer.CreatedAtUtc,
                        offer.ConcurrencyToken
                    })
            })
            .ToListAsync(cancellationToken);
        return Ok(suppliers);
    }

    [HttpPost("suppliers")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> CreateSupplier(
        CreateSupplierDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var code = dto.Code.Trim().ToUpperInvariant();
                if (!SupplierHealthStatuses.All.Contains(dto.HealthStatus) ||
                    await _context.Suppliers.AnyAsync(supplier => supplier.Code == code, token))
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Supplier fields are invalid or code exists." }));
                }

                var now = DateTime.UtcNow;
                var supplier = new Supplier
                {
                    Code = code,
                    Name = dto.Name.Trim(),
                    IsActive = dto.IsActive,
                    HealthStatus = dto.HealthStatus,
                    Priority = dto.Priority,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ConcurrencyToken = Guid.NewGuid()
                };
                _context.Suppliers.Add(supplier);
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    StatusCode(StatusCodes.Status201Created, new { supplier.Id, supplier.Code, supplier.Name }),
                    AdminAuditActions.SupplierUpserted,
                    AdminAuditAggregateTypes.Supplier,
                    supplier.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPut("suppliers/{id:long}")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> UpdateSupplier(
        long id,
        UpdateSupplierDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var supplier = await _context.Suppliers.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    token);
                if (supplier == null)
                {
                    return Mutation.NoAudit(NotFound(new { message = "Supplier not found." }));
                }

                if (supplier.ConcurrencyToken != dto.ConcurrencyToken)
                {
                    return Mutation.NoAudit(Conflict(new { message = "Supplier changed; reload and retry." }));
                }

                if (!SupplierHealthStatuses.All.Contains(dto.HealthStatus))
                {
                    return Mutation.NoAudit(BadRequest(new { message = "Supplier health status is invalid." }));
                }

                supplier.Name = dto.Name.Trim();
                supplier.IsActive = dto.IsActive;
                supplier.HealthStatus = dto.HealthStatus;
                supplier.Priority = dto.Priority;
                supplier.UpdatedAtUtc = DateTime.UtcNow;
                supplier.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    Ok(new
                    {
                        supplier.Id,
                        supplier.Code,
                        supplier.Name,
                        supplier.IsActive,
                        supplier.HealthStatus,
                        supplier.Priority,
                        supplier.ConcurrencyToken
                    }),
                    AdminAuditActions.SupplierUpserted,
                    AdminAuditAggregateTypes.Supplier,
                    supplier.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPost("supplier-offers")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> RegisterSupplierOffer(
        RegisterSupplierOfferDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var result = await _sourcingService.RegisterOfferAsync(
                    new SupplierOfferCommand(
                        dto.SupplierId,
                        dto.ExternalOfferId,
                        dto.ProductId,
                        dto.OemNumber,
                        "TRY",
                        dto.UnitCost,
                        dto.ShippingCost,
                        dto.AvailableQuantity,
                        dto.LeadTimeDays,
                        dto.MinimumOrderQuantity,
                        dto.ValidUntilUtc.UtcDateTime,
                        dto.CanDropship,
                        dto.CanSupplyWarehouse),
                    token);
                var response = new { result.OfferId, result.Message };
                return result.Outcome switch
                {
                    SupplierOfferRegistrationOutcome.Registered => Mutation.Audited(
                        StatusCode(StatusCodes.Status201Created, response),
                        AdminAuditActions.SupplierOfferRegistered,
                        AdminAuditAggregateTypes.SupplierOffer,
                        result.OfferId!.Value,
                        AdminAuditOutcomes.Succeeded),
                    SupplierOfferRegistrationOutcome.Replayed => Mutation.Audited(
                        Ok(response),
                        AdminAuditActions.SupplierOfferRegistered,
                        AdminAuditAggregateTypes.SupplierOffer,
                        result.OfferId!.Value,
                        AdminAuditOutcomes.Replayed),
                    SupplierOfferRegistrationOutcome.Conflict => Mutation.NoAudit(Conflict(response)),
                    SupplierOfferRegistrationOutcome.NotFound => Mutation.NoAudit(NotFound(response)),
                    SupplierOfferRegistrationOutcome.InvalidRequest => Mutation.NoAudit(BadRequest(response)),
                    _ => Mutation.NoAudit(StatusCode(StatusCodes.Status500InternalServerError))
                };
            },
            cancellationToken);

    [HttpPut("supplier-offers/{id:long}/active")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public Task<IActionResult> SetSupplierOfferActive(
        long id,
        SetSupplierOfferActiveDto dto,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            async token =>
            {
                var offer = await _context.SupplierOffers.SingleOrDefaultAsync(
                    candidate => candidate.Id == id,
                    token);
                if (offer == null)
                {
                    return Mutation.NoAudit(NotFound(new { message = "Supplier offer not found." }));
                }

                if (offer.ConcurrencyToken != dto.ConcurrencyToken)
                {
                    return Mutation.NoAudit(Conflict(new { message = "Supplier offer changed; reload and retry." }));
                }

                if (offer.IsActive == dto.IsActive)
                {
                    return Mutation.Audited(
                        Ok(new { offer.Id, offer.IsActive, offer.ConcurrencyToken }),
                        AdminAuditActions.SupplierOfferStatusChanged,
                        AdminAuditAggregateTypes.SupplierOffer,
                        offer.Id,
                        AdminAuditOutcomes.Replayed);
                }

                offer.IsActive = dto.IsActive;
                offer.ConcurrencyToken = Guid.NewGuid();
                await _context.SaveChangesAsync(token);
                return Mutation.Audited(
                    Ok(new { offer.Id, offer.IsActive, offer.ConcurrencyToken }),
                    AdminAuditActions.SupplierOfferStatusChanged,
                    AdminAuditAggregateTypes.SupplierOffer,
                    offer.Id,
                    AdminAuditOutcomes.Succeeded);
            },
            cancellationToken);

    [HttpPost("sourcing/select")]
    [Authorize(Policy = AdminPolicyNames.Warehouse)]
    public async Task<IActionResult> SelectSupplierSource(
        SelectSupplierSourceDto dto,
        CancellationToken cancellationToken)
    {
        var result = await _sourcingService.SelectAsync(
            new SupplierSourcingRequest(
                dto.ProductId,
                dto.Quantity,
                "TRY",
                dto.AllowSplit,
                dto.RequireDropship,
                dto.OemNumber),
            cancellationToken);
        return result.Outcome switch
        {
            SupplierSourcingOutcome.Selected => Ok(result),
            SupplierSourcingOutcome.InsufficientSupply => Conflict(result),
            SupplierSourcingOutcome.InvalidRequest => BadRequest(result),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private async Task<IActionResult> ExecuteAuditedAsync(
        Func<CancellationToken, Task<Mutation>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
            var mutation = await operation(cancellationToken);
            if (!mutation.ShouldAudit)
            {
                return mutation.Result;
            }

            var stage = StageAudit(mutation);
            if (stage.Outcome != AdminAuditIntentStageOutcome.Staged)
            {
                throw new InvalidOperationException($"Admin audit intent staging failed: {stage.ErrorCode}");
            }

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            await DispatchAuditBestEffortAsync(cancellationToken);
            return mutation.Result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "The record changed; reload and retry." });
        }
    }

    private AdminAuditIntentStageResult StageAudit(Mutation mutation)
    {
        var actorClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorRole = User.FindFirstValue(ClaimTypes.Role);
        if (!int.TryParse(actorClaim, out var actorUserId) || string.IsNullOrWhiteSpace(actorRole))
        {
            throw new InvalidOperationException("Authenticated admin audit identity is missing.");
        }

        return _auditIntentService.Stage(new AdminAuditIntentStageRequest(
            Guid.NewGuid(),
            actorUserId,
            actorRole,
            mutation.Action!,
            mutation.AggregateType!,
            mutation.AggregateId,
            HttpContext.TraceIdentifier,
            mutation.Outcome!));
    }

    private async Task DispatchAuditBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _auditIntentService.DispatchBatchAsync(
                _auditService,
                _auditOptions,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "B2B admin audit dispatch deferred. ExceptionType: {ExceptionType}",
                exception.GetType().Name);
        }
    }

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

    private sealed record Mutation(
        IActionResult Result,
        bool ShouldAudit,
        string? Action = null,
        string? AggregateType = null,
        long AggregateId = 0,
        string? Outcome = null)
    {
        public static Mutation NoAudit(IActionResult result) => new(result, false);

        public static Mutation Audited(
            IActionResult result,
            string action,
            string aggregateType,
            long aggregateId,
            string outcome) =>
            new(result, true, action, aggregateType, aggregateId, outcome);
    }
}

public sealed class ReviewDealerApplicationDto
{
    [Required, StringLength(20)]
    public string Decision { get; set; } = string.Empty;
    public long? CustomerGroupId { get; set; }
}

public sealed class CreateCustomerGroupDto
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class UpdateCustomerGroupDto
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid ConcurrencyToken { get; set; }
}

public sealed class CreatePriceListDto
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    [Range(1, long.MaxValue)]
    public long CustomerGroupId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset? ValidToUtc { get; set; }
}

public sealed class UpdatePriceListDto
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    [Range(1, long.MaxValue)]
    public long CustomerGroupId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset? ValidToUtc { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

public class CreatePriceRuleDto
{
    [Range(1, long.MaxValue)]
    public long PriceListId { get; set; }
    public int? ProductId { get; set; }
    public int? BrandId { get; set; }
    public int? CategoryId { get; set; }
    [Range(1, 100_000)]
    public int MinimumQuantity { get; set; } = 1;
    [Range(0, double.MaxValue)]
    public decimal MinimumPeriodRevenue { get; set; }
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
    [Range(0.01, 99.99)]
    public decimal? DiscountPercentage { get; set; }
    [Range(0.01, double.MaxValue)]
    public decimal? FixedUnitPrice { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset? ValidToUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class UpdatePriceRuleDto : CreatePriceRuleDto
{
    public Guid ConcurrencyToken { get; set; }
}

public sealed class PrepareBulkQuoteDto
{
    [Required, MinLength(1), MaxLength(BulkQuoteService.MaxLines)]
    public List<PrepareBulkQuoteLineDto> Lines { get; set; } = new();
    public DateTimeOffset ValidUntilUtc { get; set; }
}

public sealed class PrepareBulkQuoteLineDto
{
    [Range(1, long.MaxValue)]
    public long LineId { get; set; }
    [Range(0.01, double.MaxValue)]
    public decimal? UnitPrice { get; set; }
    [Range(0, int.MaxValue)]
    public int AvailableQuantity { get; set; }
    [Range(0, 3650)]
    public int LeadTimeDays { get; set; }
}

public sealed class CreateSupplierDto
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = string.Empty;
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    [Required, StringLength(20)]
    public string HealthStatus { get; set; } = SupplierHealthStatuses.Healthy;
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
}

public sealed class UpdateSupplierDto
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    [Required, StringLength(20)]
    public string HealthStatus { get; set; } = SupplierHealthStatuses.Healthy;
    [Range(0, int.MaxValue)]
    public int Priority { get; set; }
    public Guid ConcurrencyToken { get; set; }
}

public sealed class RegisterSupplierOfferDto
{
    [Range(1, long.MaxValue)]
    public long SupplierId { get; set; }
    [Required, StringLength(100, MinimumLength = 1)]
    public string ExternalOfferId { get; set; } = string.Empty;
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }
    [Required, StringLength(80, MinimumLength = 1)]
    public string OemNumber { get; set; } = string.Empty;
    [Range(0, double.MaxValue)]
    public decimal UnitCost { get; set; }
    [Range(0, double.MaxValue)]
    public decimal ShippingCost { get; set; }
    [Range(0, int.MaxValue)]
    public int AvailableQuantity { get; set; }
    [Range(0, 3650)]
    public int LeadTimeDays { get; set; }
    [Range(1, int.MaxValue)]
    public int MinimumOrderQuantity { get; set; } = 1;
    public DateTimeOffset ValidUntilUtc { get; set; }
    public bool CanDropship { get; set; }
    public bool CanSupplyWarehouse { get; set; }

    public override string ToString() => $"{nameof(RegisterSupplierOfferDto)} {{ Sensitive = true }}";
}

public sealed class SelectSupplierSourceDto
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }
    [Range(1, 100_000)]
    public int Quantity { get; set; }
    public bool AllowSplit { get; set; }
    public bool RequireDropship { get; set; }
    [StringLength(80)]
    public string? OemNumber { get; set; }
}

public sealed class SetSupplierOfferActiveDto
{
    public bool IsActive { get; set; }
    public Guid ConcurrencyToken { get; set; }
}
