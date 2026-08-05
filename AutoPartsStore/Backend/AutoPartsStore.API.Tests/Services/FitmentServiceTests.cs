using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class FitmentServiceTests
{
    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EffectiveAt = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ValidTo = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task VehicleTreeUpsert_CreatesCompleteHierarchyAndReplays()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildVehicleRequest();

        var created = await service.UpsertVehicleTreeAsync(request);
        var replayed = await service.UpsertVehicleTreeAsync(request);

        Assert.Equal(FitmentWriteOutcome.Created, created.Outcome);
        Assert.Equal(FitmentWriteOutcome.Replayed, replayed.Outcome);
        Assert.Equal(created.Vehicle!.Id, replayed.Vehicle!.Id);
        Assert.Equal("VW", created.Vehicle.Engine.Generation.Model.Make.CanonicalKey);
        Assert.Equal(1, await database.Context.Set<VehicleMake>().CountAsync());
        Assert.Equal(1, await database.Context.Set<VehicleModel>().CountAsync());
        Assert.Equal(1, await database.Context.Set<VehicleGeneration>().CountAsync());
        Assert.Equal(1, await database.Context.Set<VehicleEngine>().CountAsync());
        Assert.Equal(1, await database.Context.Set<Vehicle>().CountAsync());
    }

    [Fact]
    public async Task VehicleTreeUpsert_SameCanonicalKeyWithDifferentMeaningConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildVehicleRequest();
        await service.UpsertVehicleTreeAsync(request);

        var conflict = await service.UpsertVehicleTreeAsync(
            request with { GenerationName = "Unrelated generation" });

        Assert.Equal(FitmentWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<VehicleGeneration>().CountAsync());
        Assert.Equal(1, await database.Context.Set<Vehicle>().CountAsync());
    }

    [Fact]
    public async Task VehicleTreeUpsert_RejectsInvalidYearRangeBeforeWriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = BuildVehicleRequest() with
        {
            GenerationStartYear = 2025,
            GenerationEndYear = 2020
        };

        var result = await new FitmentService(database.Context).UpsertVehicleTreeAsync(request);

        Assert.Equal(FitmentWriteOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(await database.Context.Set<Vehicle>().ToListAsync());
    }

    [Fact]
    public async Task FitmentUpsert_SamePayloadIsReplayAndChangedPayloadConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        var request = BuildFitmentRequest(1, vehicle.Id);

        var created = await service.UpsertProductFitmentAsync(request);
        var replayed = await service.UpsertProductFitmentAsync(request);
        var conflict = await service.UpsertProductFitmentAsync(
            request with { Confidence = 0.8000m });

        Assert.Equal(FitmentWriteOutcome.Created, created.Outcome);
        Assert.Equal(FitmentWriteOutcome.Replayed, replayed.Outcome);
        Assert.Equal(created.Fitment!.Id, replayed.Fitment!.Id);
        Assert.Equal(FitmentWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<ProductFitment>().CountAsync());
    }

    [Fact]
    public async Task FitmentUpsert_SamePairWithDifferentIdempotencyKeyStillReplaysCanonicalFact()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        var request = BuildFitmentRequest(1, vehicle.Id);
        var first = await service.UpsertProductFitmentAsync(request);

        var replay = await service.UpsertProductFitmentAsync(
            request with { IdempotencyKey = "fitment-new-request-key" });

        Assert.Equal(FitmentWriteOutcome.Created, first.Outcome);
        Assert.Equal(FitmentWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Fitment!.Id, replay.Fitment!.Id);
        Assert.Equal("fitment-1", replay.Fitment.IdempotencyKey);
    }

    [Fact]
    public async Task FitmentUpsert_SameIdempotencyKeyForDifferentPairConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var firstVehicle = await database.AddVehicleAsync("first");
        var secondVehicle = await database.AddVehicleAsync("second");
        var service = new FitmentService(database.Context);
        await service.UpsertProductFitmentAsync(BuildFitmentRequest(1, firstVehicle.Id));

        var conflict = await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, secondVehicle.Id) with
            {
                IdempotencyKey = "fitment-1",
                SourceRecordId = "source-second"
            });

        Assert.Equal(FitmentWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<ProductFitment>().CountAsync());
    }

    [Fact]
    public async Task FitmentUpsert_RejectsVerifiedUntrustedImportAndMissingEntities()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);

        var untrusted = await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, vehicle.Id) with
            {
                SourceKind = FitmentSourceKind.UnverifiedImport,
                IsVerified = true
            });
        var missingVehicle = await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, 999_999) with
            {
                IdempotencyKey = "missing-vehicle",
                SourceRecordId = "missing-vehicle"
            });

        Assert.Equal(FitmentWriteOutcome.InvalidRequest, untrusted.Outcome);
        Assert.Equal(FitmentWriteOutcome.NotFound, missingVehicle.Outcome);
        Assert.Empty(await database.Context.Set<ProductFitment>().ToListAsync());
    }

    [Theory]
    [InlineData(FitmentAssertionKind.Exact, FitmentMatchKind.Exact)]
    [InlineData(FitmentAssertionKind.Compatible, FitmentMatchKind.Compatible)]
    public async Task Check_ReturnsOnlyTheExplicitVerifiedAssertion(
        FitmentAssertionKind assertion,
        FitmentMatchKind expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, vehicle.Id) with { AssertionKind = assertion });

        var result = await service.CheckAsync(new FitmentCheckQuery(1, vehicle.Id, EffectiveAt));

        Assert.Equal(expected, result.Match);
        Assert.True(result.IsVerified);
        Assert.Equal("OEM CATALOG", result.SourceName);
        Assert.Equal("catalog row 42", result.Provenance);
    }

    [Fact]
    public async Task Check_UnverifiedAssertionIsUnknownToAvoidFalsePositive()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, vehicle.Id) with
            {
                IsVerified = false,
                SourceKind = FitmentSourceKind.UnverifiedImport
            });

        var result = await service.CheckAsync(new FitmentCheckQuery(1, vehicle.Id, EffectiveAt));

        Assert.Equal(FitmentMatchKind.Unknown, result.Match);
        Assert.False(result.IsVerified);
        Assert.Contains("doğrulanmadığı", result.Message);
    }

    [Theory]
    [InlineData(FitmentAssertionKind.Exact, 0.8999)]
    [InlineData(FitmentAssertionKind.Compatible, 0.7999)]
    public async Task Check_VerifiedButLowConfidenceAssertionRemainsUnknown(
        FitmentAssertionKind assertionKind,
        decimal confidence)
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        await service.UpsertProductFitmentAsync(
            BuildFitmentRequest(1, vehicle.Id) with
            {
                AssertionKind = assertionKind,
                Confidence = confidence
            });

        var result = await service.CheckAsync(new FitmentCheckQuery(1, vehicle.Id, EffectiveAt));

        Assert.Equal(FitmentMatchKind.Unknown, result.Match);
        Assert.True(result.IsVerified);
        Assert.Equal(confidence, result.Confidence);
        Assert.Contains("güven eşiğinin altında", result.Message);
    }

    [Fact]
    public async Task Check_UsesHalfOpenValidityWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        await service.UpsertProductFitmentAsync(BuildFitmentRequest(1, vehicle.Id));

        var atStart = await service.CheckAsync(new FitmentCheckQuery(1, vehicle.Id, ValidFrom));
        var atEnd = await service.CheckAsync(new FitmentCheckQuery(1, vehicle.Id, ValidTo));

        Assert.Equal(FitmentMatchKind.Exact, atStart.Match);
        Assert.Equal(FitmentMatchKind.Unknown, atEnd.Match);
    }

    [Fact]
    public async Task Query_IsBoundedProjectedAndReportsHasMore()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        var service = new FitmentService(database.Context);
        for (var productId = 1; productId <= 3; productId++)
        {
            await service.UpsertProductFitmentAsync(
                BuildFitmentRequest(productId, vehicle.Id) with
                {
                    IdempotencyKey = $"bounded-{productId}",
                    SourceRecordId = $"bounded-source-{productId}"
                });
        }

        var firstPage = await service.QueryAsync(
            new FitmentReadQuery(null, vehicle.Id, EffectiveAt, Limit: 2));
        var secondPage = await service.QueryAsync(
            new FitmentReadQuery(null, vehicle.Id, EffectiveAt, Offset: 2, Limit: 2));

        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Single(secondPage.Items);
        Assert.False(secondPage.HasMore);
        Assert.All(firstPage.Items, item =>
        {
            Assert.Equal("Volkswagen", item.MakeName);
            Assert.Equal("Golf", item.ModelName);
            Assert.Equal("Mk7", item.GenerationName);
            Assert.Equal("1.4 TSI", item.EngineName);
            Assert.True(item.IsVerified);
        });
    }

    [Fact]
    public async Task Query_RejectsUnfilteredOrOversizedReads()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);

        var unfiltered = await service.QueryAsync(
            new FitmentReadQuery(null, null, EffectiveAt));
        var oversized = await service.QueryAsync(
            new FitmentReadQuery(1, null, EffectiveAt, Limit: FitmentService.MaxReadLimit + 1));

        Assert.Empty(unfiltered.Items);
        Assert.NotNull(unfiltered.ValidationError);
        Assert.Empty(oversized.Items);
        Assert.Contains("Limit", oversized.ValidationError);
    }

    [Fact]
    public async Task ProductIdentifier_UniqueNaturalKeyIsEnforcedRelationally()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Set<ProductIdentifier>().Add(BuildIdentifier("first-source"));
        await database.Context.SaveChangesAsync();
        database.Context.Set<ProductIdentifier>().Add(BuildIdentifier("second-source"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_CreatesNormalizedRecordAndReplays()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildIdentifierUpsertRequest();

        var created = await service.UpsertProductIdentifierAsync(request);
        var replayed = await service.UpsertProductIdentifierAsync(request);

        Assert.Equal(FitmentWriteOutcome.Created, created.Outcome);
        Assert.Equal(FitmentWriteOutcome.Replayed, replayed.Outcome);
        Assert.Equal(created.Identifier!.Id, replayed.Identifier!.Id);
        Assert.Equal("VOLKSWAGEN", created.Identifier.SchemeAuthority);
        Assert.Equal("OEM CATALOG", created.Identifier.SourceName);
        Assert.Equal("04E115561H", created.Identifier.NormalizedValue);
        Assert.Equal(1, await database.Context.Set<ProductIdentifier>().CountAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_NormalizedEquivalentValueReplays()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildIdentifierUpsertRequest();
        await service.UpsertProductIdentifierAsync(request);

        var result = await service.UpsertProductIdentifierAsync(
            request with { Value = " 04e-115.561/h " });

        Assert.Equal(FitmentWriteOutcome.Replayed, result.Outcome);
        Assert.Equal(1, await database.Context.Set<ProductIdentifier>().CountAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_SameNaturalKeyWithDifferentPayloadConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildIdentifierUpsertRequest();
        var first = await service.UpsertProductIdentifierAsync(request);

        var conflict = await service.UpsertProductIdentifierAsync(
            request with { Provenance = "different catalog evidence" });

        Assert.Equal(FitmentWriteOutcome.Created, first.Outcome);
        Assert.Equal(FitmentWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(first.Identifier!.Id, conflict.Identifier!.Id);
        Assert.Equal(1, await database.Context.Set<ProductIdentifier>().CountAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_ReusedSourceRecordForDifferentNaturalKeyConflicts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildIdentifierUpsertRequest();
        await service.UpsertProductIdentifierAsync(request);

        var conflict = await service.UpsertProductIdentifierAsync(
            request with
            {
                Kind = PartIdentifierKind.Interchange,
                Value = "HU 719/7 X"
            });

        Assert.Equal(FitmentWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.Set<ProductIdentifier>().CountAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_RejectsVerifiedUnverifiedImportBeforeWriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = BuildIdentifierUpsertRequest() with
        {
            IsVerified = true,
            SourceKind = FitmentSourceKind.UnverifiedImport
        };

        var result = await new FitmentService(database.Context)
            .UpsertProductIdentifierAsync(request);

        Assert.Equal(FitmentWriteOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(await database.Context.Set<ProductIdentifier>().ToListAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_RejectsInvalidIdentifiersAndDateRanges()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new FitmentService(database.Context);
        var request = BuildIdentifierUpsertRequest();
        ProductIdentifierUpsertRequest[] invalidRequests =
        [
            request with { ProductId = 0 },
            request with { Kind = (PartIdentifierKind)999 },
            request with { Value = " -- / . " },
            request with { SchemeAuthority = " " },
            request with { SourceName = " " },
            request with { SourceRecordId = " " },
            request with { Provenance = " " },
            request with { ValidFromUtc = DateTime.SpecifyKind(ValidFrom, DateTimeKind.Unspecified) },
            request with { ValidToUtc = ValidFrom }
        ];

        foreach (var invalidRequest in invalidRequests)
        {
            var result = await service.UpsertProductIdentifierAsync(invalidRequest);
            Assert.Equal(FitmentWriteOutcome.InvalidRequest, result.Outcome);
        }

        Assert.Empty(await database.Context.Set<ProductIdentifier>().ToListAsync());
    }

    [Fact]
    public async Task ProductIdentifierUpsert_MissingProductDoesNotWrite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = BuildIdentifierUpsertRequest() with { ProductId = 999_999 };

        var result = await new FitmentService(database.Context)
            .UpsertProductIdentifierAsync(request);

        Assert.Equal(FitmentWriteOutcome.NotFound, result.Outcome);
        Assert.Empty(await database.Context.Set<ProductIdentifier>().ToListAsync());
    }

    [Fact]
    public async Task ProductFitment_VerifiedUntrustedSourceIsRejectedRelationally()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vehicle = await database.AddVehicleAsync();
        database.Context.Set<ProductFitment>().Add(new ProductFitment
        {
            ProductId = 1,
            VehicleId = vehicle.Id,
            AssertionKind = FitmentAssertionKind.Exact,
            Confidence = 0.9m,
            IsVerified = true,
            SourceKind = FitmentSourceKind.UnverifiedImport,
            SourceName = "IMPORT",
            SourceRecordId = "unsafe-source",
            Provenance = "unreviewed import row",
            IdempotencyKey = "unsafe-fitment",
            ValidFromUtc = ValidFrom,
            CreatedAtUtc = EffectiveAt
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Model_DeclaresUniqueFitmentDuplicateGuards()
    {
        await using var database = await TestDatabase.CreateAsync();
        var entity = database.Context.Model.FindEntityType(typeof(ProductFitment));
        Assert.NotNull(entity);
        var uniquePropertySets = entity!.GetIndexes()
            .Where(index => index.IsUnique)
            .Select(index => string.Join(",", index.Properties.Select(property => property.Name)))
            .ToArray();

        Assert.Contains("ProductId,VehicleId", uniquePropertySets);
        Assert.Contains("IdempotencyKey", uniquePropertySets);
        Assert.Contains("SourceName,SourceRecordId", uniquePropertySets);
    }

    private static VehicleTreeUpsertRequest BuildVehicleRequest(string suffix = "")
    {
        var keySuffix = string.IsNullOrEmpty(suffix) ? string.Empty : $"-{suffix}";
        return new VehicleTreeUpsertRequest
        {
            MakeKey = $"vw{keySuffix}",
            MakeName = "Volkswagen",
            ModelKey = $"golf{keySuffix}",
            ModelName = "Golf",
            GenerationKey = $"mk7{keySuffix}",
            GenerationName = "Mk7",
            GenerationStartYear = 2012,
            GenerationEndYear = 2020,
            EngineKey = $"ea211-czda{keySuffix}",
            EngineName = "1.4 TSI",
            EngineCode = "CZDA",
            FuelType = "Petrol",
            DisplacementCc = 1395,
            PowerKw = 110m,
            VehicleKey = $"golf-mk7-czda-dsg-eu{keySuffix}",
            VehicleName = "Volkswagen Golf Mk7 1.4 TSI DSG",
            BodyStyle = "Hatchback",
            Transmission = "DSG",
            DriveType = "FWD",
            Market = "EU",
            VehicleStartYear = 2014,
            VehicleEndYear = 2017
        };
    }

    private static ProductFitmentUpsertRequest BuildFitmentRequest(int productId, int vehicleId)
    {
        return new ProductFitmentUpsertRequest(
            productId,
            vehicleId,
            FitmentAssertionKind.Exact,
            0.9500m,
            true,
            FitmentSourceKind.Manufacturer,
            " OEM Catalog ",
            $"source-{productId}-{vehicleId}",
            "catalog row 42",
            $"fitment-{productId}",
            ValidFrom,
            ValidTo);
    }

    private static ProductIdentifier BuildIdentifier(string sourceRecordId)
    {
        return new ProductIdentifier
        {
            ProductId = 1,
            Kind = PartIdentifierKind.Oem,
            SchemeAuthority = "Volkswagen",
            Value = "04E 115 561 H",
            NormalizedValue = "04E115561H",
            IsVerified = true,
            SourceKind = FitmentSourceKind.Manufacturer,
            SourceName = "OEM Catalog",
            SourceRecordId = sourceRecordId,
            Provenance = "catalog row 99",
            ValidFromUtc = ValidFrom
        };
    }

    private static ProductIdentifierUpsertRequest BuildIdentifierUpsertRequest()
    {
        return new ProductIdentifierUpsertRequest(
            1,
            PartIdentifierKind.Oem,
            " Volkswagen ",
            " 04E 115 561 H ",
            true,
            FitmentSourceKind.Manufacturer,
            " OEM Catalog ",
            " oem-row-99 ",
            " catalog row 99 ",
            ValidFrom,
            ValidTo);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(FitmentTestDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public FitmentTestDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new FitmentTestDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async Task<Vehicle> AddVehicleAsync(string suffix = "")
        {
            var result = await new FitmentService(Context)
                .UpsertVehicleTreeAsync(BuildVehicleRequest(suffix));
            Assert.Equal(FitmentWriteOutcome.Created, result.Outcome);
            return result.Vehicle!;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FitmentTestDbContext : AutoPartsDbContext
    {
        public FitmentTestDbContext(DbContextOptions<AutoPartsDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ConfigureFitmentModel();
        }
    }
}
