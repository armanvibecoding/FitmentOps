using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Contracts;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using AutoPartsStore.API.Payments;
using AutoPartsStore.API.Invoicing;
using AutoPartsStore.API.Observability;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add DbContext
builder.Services.AddDbContext<AutoPartsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IDatabaseInitializer, EfCoreDatabaseInitializer>();

// Add Services
builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<OrderLifecycleService>();
builder.Services.AddScoped<PaymentEventService>();
builder.Services.AddScoped<PaymentStateService>();
builder.Services.AddScoped<PaymentCallbackReconciliationService>();
builder.Services.AddScoped<RefundService>();
builder.Services.AddScoped<OutboxService>();
builder.Services.AddScoped<FulfillmentService>();
builder.Services.AddScoped<ReturnService>();
builder.Services.AddScoped<InventoryReservationService>();
var hostedCheckoutOptions = builder.Configuration
    .GetSection("HostedCheckout")
    .Get<HostedCheckoutOptions>() ?? new HostedCheckoutOptions();
builder.Services.AddHostedCheckout(hostedCheckoutOptions);
builder.Services.AddSingleton(
    builder.Configuration
        .GetSection("HostedCheckoutEndpoint")
        .Get<HostedCheckoutEndpointOptions>() ?? new HostedCheckoutEndpointOptions());
builder.Services.AddScoped<FitmentService>();
builder.Services.AddScoped<SupplierSourcingService>();
builder.Services.AddScoped<DealerApplicationService>();
builder.Services.AddScoped<B2bPricingService>();
builder.Services.AddScoped<BulkQuoteService>();
builder.Services.AddSingleton<ISalesChannelAdapterRegistry, DisabledSalesChannelAdapterRegistry>();
builder.Services.AddScoped<SalesChannelService>();
builder.Services.AddScoped<MaintenanceJournalService>();
var configuredLegalDocumentTypes = builder.Configuration
    .GetSection("LegalCheckout:RequiredDocumentTypes")
    .Get<string[]>();
var legalCheckoutOptions = configuredLegalDocumentTypes is { Length: > 0 }
    ? new LegalCheckoutOptions { RequiredDocumentTypes = configuredLegalDocumentTypes }
    : new LegalCheckoutOptions();
legalCheckoutOptions.Validate();
builder.Services.AddSingleton(legalCheckoutOptions);
builder.Services.AddScoped<LegalConsentService>();
builder.Services.AddSingleton(
    builder.Configuration.GetSection("PublicSite").Get<PublicSiteOptions>() ?? new PublicSiteOptions());
builder.Services.AddScoped(provider => new AdminAuditIntentService(
    provider.GetRequiredService<AutoPartsDbContext>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped(provider => new AdminAuditService(
    provider.GetRequiredService<AutoPartsDbContext>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(TimeProvider.System);
var adminAuditIntentOptions = builder.Configuration
    .GetSection("AdminAuditIntent")
    .Get<AdminAuditIntentOptions>() ?? new AdminAuditIntentOptions();
adminAuditIntentOptions.Validate();
builder.Services.AddSingleton(adminAuditIntentOptions);
builder.Services.AddHostedService<AdminAuditIntentWorker>();
var inventoryReservationExpiryOptions = builder.Configuration
    .GetSection(InventoryReservationExpiryOptions.ConfigurationSectionName)
    .Get<InventoryReservationExpiryOptions>() ?? new InventoryReservationExpiryOptions();
inventoryReservationExpiryOptions.Validate();
builder.Services.AddSingleton(inventoryReservationExpiryOptions);
builder.Services.AddScoped<InventoryReservationExpiryProcessor>();
builder.Services.AddHostedService<InventoryReservationExpiryWorker>();
builder.Services.AddSingleton(
    builder.Configuration
        .GetSection("OutboxDispatch")
        .Get<OutboxDispatchOptions>() ?? new OutboxDispatchOptions());
builder.Services.AddSingleton<IPaymentGateway, DisabledPaymentGateway>();
builder.Services.AddSingleton<IInvoiceGateway, DisabledInvoiceGateway>();
var operationalReadinessOptions = builder.Configuration
    .GetSection("OperationalReadiness")
    .Get<OperationalReadinessOptions>() ?? new OperationalReadinessOptions();
builder.Services.AddOperationalObservability(operationalReadinessOptions);
var outboxWorkerOptions = builder.Configuration
    .GetSection("OutboxWorker")
    .Get<OutboxWorkerOptions>() ?? new OutboxWorkerOptions();
builder.Services.AddOutboxDispatchWorker(outboxWorkerOptions);

// Add Authentication
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must be configured with at least 32 characters. Use the Jwt__Key environment variable.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "FitmentOps.API",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "FitmentOps.Web",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        AdminPolicyNames.AdminAccess,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Finance,
            AdminAuditRoles.Warehouse,
            AdminAuditRoles.Catalog,
            AdminAuditRoles.Support,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.OperationsRead,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Warehouse,
            AdminAuditRoles.Support,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.Returns,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Warehouse,
            AdminAuditRoles.Support,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.Finance,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Finance,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.Warehouse,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Warehouse,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.Catalog,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Catalog,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.Support,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.Support,
            AdminAuditRoles.SuperAdmin));
    options.AddPolicy(
        AdminPolicyNames.SuperAdmin,
        policy => policy.RequireRole(
            AdminAuditRoles.LegacyAdmin,
            AdminAuditRoles.SuperAdmin));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("order-tracking", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("authentication", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("checkout-initialization", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("payment-callback", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("payment-webhook", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("b2b-write", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("garage-write", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var corsSettings = builder.Configuration.GetSection("Cors").Get<CorsSettings>() ?? new CorsSettings();
var allowedCorsOrigins = corsSettings.GetValidatedOrigins();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy
            .WithOrigins(allowedCorsOrigins.ToArray())
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();
var openApiEnabled = app.Environment.IsDevelopment() ||
                     builder.Configuration.GetValue<bool>("OpenApi:Enabled");

app.UseRequestCorrelation();

// Configure the HTTP request pipeline.
if (openApiEnabled)
{
    app.UseSwagger();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=()";
        if (!app.Environment.IsDevelopment())
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'";
        }

        return Task.CompletedTask;
    });
    await next();
});

// Global Exception Handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;

        if (exception != null)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            if (app.Environment.IsDevelopment())
            {
                logger.LogError(exception, "Unhandled exception. TraceId: {TraceId}", context.TraceIdentifier);
            }
            else
            {
                logger.LogError(
                    "Unhandled exception of type {ExceptionType}. TraceId: {TraceId}",
                    exception.GetType().Name,
                    context.TraceIdentifier);
            }

            var response = new
            {
                error = "An error occurred while processing your request.",
                message = app.Environment.IsDevelopment() ? exception.Message : "Internal server error",
                details = app.Environment.IsDevelopment() ? exception.StackTrace : null
            };

            await context.Response.WriteAsJsonAsync(response);
        }
    });
});

app.UseHttpsRedirection();

app.UseCors("AllowReactApp");

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapOperationalHealthEndpoints();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var databaseInitializer = services.GetRequiredService<IDatabaseInitializer>();
        await databaseInitializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(ex, "Database migration failed. Application startup is aborted.");
        throw;
    }
}

app.Run();

public partial class Program
{
}
