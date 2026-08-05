using System.Security.Claims;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminFitmentControllerTests
{
    private static readonly DateTime ValidFrom =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidTo =
        new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VehicleUpsert_CommitsBusinessIntentAndExactlyOneImmediateEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = CreateController(database.Context);

        var response = await controller.UpsertVehicle(
            BuildVehicleRequest(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(response).StatusCode);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(1, await database.Context.Vehicles.CountAsync());
        var intent = await database.Context.AdminAuditIntents.AsNoTracking().SingleAsync();
        var auditEvent = await database.Context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(AdminAuditActions.VehicleUpserted, intent.Action);
        Assert.Equal(AdminAuditActions.VehicleUpserted, auditEvent.Action);
        Assert.Equal(intent.AggregateId, auditEvent.AggregateId);
    }

    [Fact]
    public async Task ProductFitmentUpsert_CommitsBusinessIntentAndExactlyOneImmediateEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fitmentService = new FitmentService(database.Context, new FixedTimeProvider(Now));
        var vehicle = await fitmentService.UpsertVehicleTreeAsync(BuildVehicleRequest());
        Assert.Equal(FitmentWriteOutcome.Created, vehicle.Outcome);
        var controller = CreateController(database.Context);

        var response = await controller.UpsertProductFitment(
            BuildFitmentRequest(vehicle.Vehicle!.Id),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(response).StatusCode);
        database.Context.ChangeTracker.Clear();
        var link = await database.Context.ProductFitments.AsNoTracking().SingleAsync();
        var intent = await database.Context.AdminAuditIntents.AsNoTracking().SingleAsync();
        var auditEvent = await database.Context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(AdminAuditActions.ProductFitmentUpserted, auditEvent.Action);
        Assert.Equal(link.Id, intent.AggregateId);
        Assert.Equal(link.Id, auditEvent.AggregateId);
    }

    [Fact]
    public async Task ProductIdentifierUpsert_CommitsBusinessIntentAndExactlyOneImmediateEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = CreateController(database.Context);

        var response = await controller.UpsertProductIdentifier(
            BuildIdentifierRequest(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(response).StatusCode);
        database.Context.ChangeTracker.Clear();
        var identifier = await database.Context.ProductIdentifiers.AsNoTracking().SingleAsync();
        var intent = await database.Context.AdminAuditIntents.AsNoTracking().SingleAsync();
        var auditEvent = await database.Context.AdminAuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Succeeded, intent.Status);
        Assert.Equal(AdminAuditActions.ProductIdentifierUpserted, auditEvent.Action);
        Assert.Equal(identifier.Id, intent.AggregateId);
        Assert.Equal(identifier.Id, auditEvent.AggregateId);
    }

    [Fact]
    public async Task GetQuality_ReportsCoverageConfidenceAndSourceGaps()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seededProductCount = await database.Context.Products.CountAsync();
        var category = new Category { Name = "Filters", Slug = "filters" };
        var brand = new Brand { Name = "Volkswagen", Slug = "volkswagen" };
        var partBrand = new PartBrand { Name = "Mann", Slug = "mann" };
        database.Context.AddRange(category, brand, partBrand);
        await database.Context.SaveChangesAsync();
        var products = new[]
        {
            new Product
            {
                Name = "Oil filter",
                PartNumber = "OF-1",
                Price = 100,
                Stock = 10,
                CategoryId = category.Id,
                BrandId = brand.Id,
                PartBrandId = partBrand.Id
            },
            new Product
            {
                Name = "Air filter",
                PartNumber = "AF-1",
                Price = 200,
                Stock = 5,
                CategoryId = category.Id,
                BrandId = brand.Id,
                PartBrandId = partBrand.Id
            }
        };
        database.Context.Products.AddRange(products);
        await database.Context.SaveChangesAsync();

        var fitmentService = new FitmentService(database.Context, new FixedTimeProvider(Now));
        var vehicle = (await fitmentService.UpsertVehicleTreeAsync(BuildVehicleRequest())).Vehicle!;
        await fitmentService.UpsertProductFitmentAsync(new ProductFitmentUpsertRequest(
            products[0].Id,
            vehicle.Id,
            FitmentAssertionKind.Exact,
            0.89m,
            true,
            FitmentSourceKind.Manufacturer,
            "OEM Catalog",
            "quality-verified",
            "catalog row 100",
            "quality-verified-idempotency",
            Now.UtcDateTime.AddDays(-1),
            Now.UtcDateTime.AddDays(10)));
        await fitmentService.UpsertProductFitmentAsync(new ProductFitmentUpsertRequest(
            products[1].Id,
            vehicle.Id,
            FitmentAssertionKind.Compatible,
            0.99m,
            false,
            FitmentSourceKind.UnverifiedImport,
            "Supplier Import",
            "quality-unverified",
            "unverified row 1",
            "quality-unverified-idempotency",
            Now.UtcDateTime.AddDays(-1),
            null));
        await fitmentService.UpsertProductIdentifierAsync(new ProductIdentifierUpsertRequest(
            products[0].Id,
            PartIdentifierKind.Oem,
            "Volkswagen",
            "04E 115 561 H",
            true,
            FitmentSourceKind.Manufacturer,
            "OEM Catalog",
            "quality-oem",
            "catalog row 101",
            Now.UtcDateTime.AddDays(-1),
            null));

        database.Context.ChangeTracker.Clear();
        var response = await CreateController(database.Context).GetQuality(CancellationToken.None);

        var dto = Assert.IsType<FitmentQualityDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(seededProductCount + 2, dto.TotalProducts);
        Assert.Equal(1, dto.ActiveVerifiedFitments);
        Assert.Equal(1, dto.ActiveUnverifiedFitments);
        Assert.Equal(1, dto.BelowConfidenceThreshold);
        Assert.Equal(seededProductCount + 1, dto.ProductsWithoutVerifiedFitment);
        Assert.Equal(seededProductCount + 1, dto.ProductsWithoutVerifiedOem);
        Assert.Equal(1, dto.ExpiringWithin30Days);
        Assert.Collection(dto.Sources, source =>
        {
            Assert.Equal(nameof(FitmentSourceKind.Manufacturer), source.SourceKind);
            Assert.Equal(1, source.Count);
        });
        Assert.Equal(Now.UtcDateTime, dto.ObservedAtUtc);
    }

    [Fact]
    public async Task InvalidAuditIntent_RollsBackVehicleMutationAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var controller = CreateController(database.Context, actorRole: "not-an-admin-role");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.UpsertVehicle(BuildVehicleRequest(), CancellationToken.None));

        Assert.Contains(AdminAuditIntentErrorCodes.InvalidActorRole, exception.Message);
        Assert.Equal(0, await database.Context.VehicleMakes.CountAsync());
        Assert.Equal(0, await database.Context.Vehicles.CountAsync());
        Assert.Equal(0, await database.Context.AdminAuditIntents.CountAsync());
        Assert.Equal(0, await database.Context.AdminAuditEvents.CountAsync());
    }

    [Fact]
    public async Task ImmediateDispatchFailure_KeepsCommittedBusinessAndDurablePendingIntent()
    {
        await using var database = await TestDatabase.CreateAsync(failAuditEventWrites: true);
        var controller = CreateController(database.Context);

        var response = await controller.UpsertVehicle(
            BuildVehicleRequest(),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(response).StatusCode);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(1, await database.Context.Vehicles.CountAsync());
        var intent = await database.Context.AdminAuditIntents.AsNoTracking().SingleAsync();
        Assert.Equal(AdminAuditIntentStatuses.Pending, intent.Status);
        Assert.Equal(1, intent.AttemptCount);
        Assert.Equal(AdminAuditIntentErrorCodes.DispatchException, intent.LastErrorCode);
        Assert.Equal(0, await database.Context.AdminAuditEvents.CountAsync());
    }

    private static AdminFitmentController CreateController(
        AuditTestDbContext context,
        string actorRole = AdminAuditRoles.Catalog)
    {
        var clock = new FixedTimeProvider(Now);
        var options = new AdminAuditIntentOptions
        {
            MaxBatchSize = 10,
            MaxAttempts = 3,
            LeaseDuration = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(100)
        };
        var controller = new AdminFitmentController(
            context,
            new FitmentService(context, clock),
            new AdminAuditIntentService(context, clock),
            new AdminAuditService(context, clock),
            options,
            clock);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                TraceIdentifier = "fitment-audit-correlation"
            }
        };
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "42"),
                new Claim(ClaimTypes.Role, actorRole)
            ],
            "test"));
        return controller;
    }

    private static VehicleTreeUpsertRequest BuildVehicleRequest() => new()
    {
        MakeKey = "vw",
        MakeName = "Volkswagen",
        ModelKey = "golf",
        ModelName = "Golf",
        GenerationKey = "mk7",
        GenerationName = "Mk7",
        GenerationStartYear = 2012,
        GenerationEndYear = 2020,
        EngineKey = "ea211-czda",
        EngineName = "1.4 TSI",
        EngineCode = "CZDA",
        FuelType = "Petrol",
        DisplacementCc = 1395,
        PowerKw = 110m,
        VehicleKey = "golf-mk7-czda-dsg-eu",
        VehicleName = "Volkswagen Golf Mk7 1.4 TSI DSG",
        BodyStyle = "Hatchback",
        Transmission = "DSG",
        DriveType = "FWD",
        Market = "EU",
        VehicleStartYear = 2014,
        VehicleEndYear = 2017
    };

    private static ProductFitmentUpsertRequest BuildFitmentRequest(int vehicleId) =>
        new(
            1,
            vehicleId,
            FitmentAssertionKind.Exact,
            0.95m,
            true,
            FitmentSourceKind.Manufacturer,
            "OEM Catalog",
            "controller-fitment-source",
            "catalog row 42",
            "controller-fitment-idempotency",
            ValidFrom,
            ValidTo);

    private static ProductIdentifierUpsertRequest BuildIdentifierRequest() =>
        new(
            1,
            PartIdentifierKind.Oem,
            "Volkswagen",
            "04E 115 561 H",
            true,
            FitmentSourceKind.Manufacturer,
            "OEM Catalog",
            "controller-identifier-source",
            "catalog row 99",
            ValidFrom,
            ValidTo);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(AuditTestDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AuditTestDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync(bool failAuditEventWrites = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AuditTestDbContext(options)
            {
                FailAuditEventWrites = failAuditEventWrites
            };
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class AuditTestDbContext : AutoPartsDbContext
    {
        public AuditTestDbContext(DbContextOptions<AutoPartsDbContext> options)
            : base(options)
        {
        }

        public bool FailAuditEventWrites { get; init; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailAuditEventWrites &&
                ChangeTracker.Entries<AdminAuditEvent>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                throw new DbUpdateException("Injected audit event persistence failure.");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
