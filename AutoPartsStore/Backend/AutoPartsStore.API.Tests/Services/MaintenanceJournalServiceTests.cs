using AutoPartsStore.API.Data;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AutoPartsStore.API.Tests.Services;

public sealed class MaintenanceJournalServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateVehicle_IsIdempotentAndRejectsChangedReplay()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await SeedOwnerAndVehicleAsync(database.Context);
        var service = CreateService(database.Context);
        var request = new CreateUserVehicleRequest(fixture.VehicleId, "Aile aracı", 42_000);

        var created = await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-001",
            request);
        var replayed = await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-001",
            request);
        var conflict = await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-001",
            request with { Nickname = "Değiştirilmiş" });

        Assert.Equal(MaintenanceWriteOutcome.Created, created.Outcome);
        Assert.Equal(MaintenanceWriteOutcome.Replayed, replayed.Outcome);
        Assert.Equal(created.Value!.Id, replayed.Value!.Id);
        Assert.Equal(MaintenanceWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, await database.Context.UserVehicles.CountAsync());
    }

    [Fact]
    public async Task AddRecord_AtomicallyRaisesOdometerAndReplaysExactlyOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await SeedOwnerAndVehicleAsync(database.Context);
        var service = CreateService(database.Context);
        var garageVehicle = (await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-002",
            new(fixture.VehicleId, "Servis aracı", 40_000))).Value!;
        var request = new CreateMaintenanceRecordRequest(
            Now.UtcDateTime.AddDays(-2),
            45_000,
            "Yetkili servis",
            "Periyodik bakım",
            [new(1, "OilChange", "Yağ ve filtre değişimi", 1, 1200m)]);

        var created = await service.AddRecordAsync(
            fixture.UserId,
            garageVehicle.Id,
            "maintenance-key-0001",
            request);
        var replayed = await service.AddRecordAsync(
            fixture.UserId,
            garageVehicle.Id,
            "maintenance-key-0001",
            request);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(MaintenanceWriteOutcome.Created, created.Outcome);
        Assert.Equal(MaintenanceWriteOutcome.Replayed, replayed.Outcome);
        Assert.Equal(1, await database.Context.MaintenanceRecords.CountAsync());
        Assert.Equal(1, await database.Context.MaintenanceRecordItems.CountAsync());
        Assert.Equal(
            45_000,
            await database.Context.UserVehicles
                .Where(candidate => candidate.Id == garageVehicle.Id)
                .Select(candidate => candidate.CurrentOdometerKm)
                .SingleAsync());
    }

    [Fact]
    public async Task OwnershipBoundary_HidesOtherUsersJournal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await SeedOwnerAndVehicleAsync(database.Context);
        var service = CreateService(database.Context);
        var garageVehicle = (await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-003",
            new(fixture.VehicleId, "Özel araç", null))).Value!;
        var otherUser = new User
        {
            Email = "other@example.test",
            Password = "not-a-real-password-hash",
            FullName = "Other User"
        };
        database.Context.Users.Add(otherUser);
        await database.Context.SaveChangesAsync();

        var records = await service.GetRecordsAsync(otherUser.Id, garageVehicle.Id);
        var reminders = await service.GetRemindersAsync(otherUser.Id, garageVehicle.Id);
        var write = await service.AddReminderAsync(
            otherUser.Id,
            garageVehicle.Id,
            "reminder-key-other1",
            new("Yağ bakımı", Now.UtcDateTime.AddDays(30), null));

        Assert.Null(records);
        Assert.Null(reminders);
        Assert.Equal(MaintenanceWriteOutcome.NotFound, write.Outcome);
        Assert.Equal(0, await database.Context.MaintenanceReminders.CountAsync());
    }

    [Fact]
    public async Task Reminder_RequiresTargetAndUsesOptimisticConcurrencyOnCompletion()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await SeedOwnerAndVehicleAsync(database.Context);
        var service = CreateService(database.Context);
        var garageVehicle = (await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-004",
            new(fixture.VehicleId, "Hatırlatma aracı", 50_000))).Value!;

        var invalid = await service.AddReminderAsync(
            fixture.UserId,
            garageVehicle.Id,
            "reminder-key-invalid",
            new("Hedefsiz", null, null));
        var created = await service.AddReminderAsync(
            fixture.UserId,
            garageVehicle.Id,
            "reminder-key-valid01",
            new("Polen filtresi", null, 55_000));
        var stale = await service.CompleteReminderAsync(
            fixture.UserId,
            created.Value!.Id,
            new(Guid.NewGuid()));
        var completed = await service.CompleteReminderAsync(
            fixture.UserId,
            created.Value.Id,
            new(created.Value.ConcurrencyToken));
        var replayed = await service.CompleteReminderAsync(
            fixture.UserId,
            created.Value.Id,
            new(Guid.NewGuid()));

        Assert.Equal(MaintenanceWriteOutcome.InvalidRequest, invalid.Outcome);
        Assert.Equal(MaintenanceWriteOutcome.Conflict, stale.Outcome);
        Assert.Equal(MaintenanceWriteOutcome.Updated, completed.Outcome);
        Assert.Equal(MaintenanceWriteOutcome.Replayed, replayed.Outcome);
        Assert.NotNull(completed.Value!.CompletedAtUtc);
    }

    [Fact]
    public async Task AdminSummaryAndUserView_TranslateAndExposeOnlyOperationalMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await SeedOwnerAndVehicleAsync(database.Context);
        var service = CreateService(database.Context);
        var garageVehicle = (await service.CreateVehicleAsync(
            fixture.UserId,
            "garage-create-key-005",
            new(fixture.VehicleId, "Destek aracı", 60_000))).Value!;
        await service.AddRecordAsync(
            fixture.UserId,
            garageVehicle.Id,
            "maintenance-key-0002",
            new(
                Now.UtcDateTime.AddDays(-5),
                59_000,
                "Servis",
                "Private customer note",
                [new(null, MaintenanceServiceTypes.Inspection, "Genel kontrol", 1, null)]));
        await service.AddReminderAsync(
            fixture.UserId,
            garageVehicle.Id,
            "reminder-key-valid02",
            new("Fren kontrolü", null, 59_500));
        database.Context.ChangeTracker.Clear();
        var controller = new AdminGarageController(
            database.Context,
            new FixedTimeProvider(Now));

        var summaryResult = await controller.GetSummary(CancellationToken.None);
        var summary = Assert.IsType<AdminGarageSummaryDto>(
            Assert.IsType<OkObjectResult>(summaryResult.Result).Value);
        var userResult = await controller.GetUserGarage(fixture.UserId, CancellationToken.None);
        var vehicles = Assert.IsAssignableFrom<IReadOnlyList<AdminUserVehicleDto>>(
            Assert.IsType<OkObjectResult>(userResult.Result).Value);

        Assert.Equal(1, summary.ActiveVehicles);
        Assert.Equal(1, summary.DueReminders);
        var vehicle = Assert.Single(vehicles);
        Assert.Equal(1, vehicle.MaintenanceRecordCount);
        Assert.Equal(1, vehicle.DueReminderCount);
        Assert.DoesNotContain("Private customer note", System.Text.Json.JsonSerializer.Serialize(vehicle));
    }

    private static MaintenanceJournalService CreateService(AutoPartsDbContext context) =>
        new(context, new FixedTimeProvider(Now));

    private static async Task<(int UserId, int VehicleId)> SeedOwnerAndVehicleAsync(
        AutoPartsDbContext context)
    {
        var user = new User
        {
            Email = $"owner-{Guid.NewGuid():N}@example.test",
            Password = "not-a-real-password-hash",
            FullName = "Garage Owner"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var vehicle = await new FitmentService(context, new FixedTimeProvider(Now))
            .UpsertVehicleTreeAsync(new VehicleTreeUpsertRequest
            {
                MakeKey = "toyota",
                MakeName = "Toyota",
                ModelKey = "corolla",
                ModelName = "Corolla",
                GenerationKey = "e210",
                GenerationName = "E210",
                GenerationStartYear = 2018,
                EngineKey = "m20a-fks",
                EngineName = "2.0",
                EngineCode = "M20A-FKS",
                FuelType = "Petrol",
                VehicleKey = "corolla-e210-m20a-cvt-tr",
                VehicleName = "Toyota Corolla E210 2.0 CVT",
                Transmission = "CVT",
                DriveType = "FWD",
                Market = "TR",
                VehicleStartYear = 2019
            });
        return (user.Id, vehicle.Vehicle!.Id);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(AutoPartsDbContext context, SqliteConnection connection)
        {
            Context = context;
            _connection = connection;
        }

        public AutoPartsDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AutoPartsDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AutoPartsDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
