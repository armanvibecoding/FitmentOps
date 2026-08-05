using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public sealed class MaintenanceJournalService
{
    private const int MaximumOdometerKm = 10_000_000;
    private const int MaximumItemsPerRecord = 50;
    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public MaintenanceJournalService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<List<UserVehicle>> GetGarageAsync(
        int userId,
        CancellationToken cancellationToken = default) =>
        _context.UserVehicles
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .Include(candidate => candidate.Vehicle)
                .ThenInclude(vehicle => vehicle.Engine)
                    .ThenInclude(engine => engine.Generation)
                        .ThenInclude(generation => generation.Model)
                            .ThenInclude(model => model.Make)
            .OrderByDescending(candidate => candidate.IsActive)
            .ThenBy(candidate => candidate.Nickname)
            .ToListAsync(cancellationToken);

    public async Task<MaintenanceWriteResult<UserVehicle>> CreateVehicleAsync(
        int userId,
        string idempotencyKey,
        CreateUserVehicleRequest request,
        CancellationToken cancellationToken = default)
    {
        var nickname = request.Nickname?.Trim() ?? string.Empty;
        if (userId <= 0 || request.VehicleId <= 0 || nickname is { Length: < 1 or > 80 } ||
            !IsValidOdometer(request.CurrentOdometerKm))
        {
            return Invalid<UserVehicle>("Araç bilgileri geçersiz.");
        }

        var existing = await _context.UserVehicles.SingleOrDefaultAsync(
            candidate => candidate.UserId == userId && candidate.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing != null)
        {
            return existing.VehicleId == request.VehicleId &&
                   existing.Nickname == nickname &&
                   existing.CurrentOdometerKm == request.CurrentOdometerKm
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Idempotency-Key farklı bir araç isteğinde kullanılmış.");
        }

        if (!await _context.Vehicles.AnyAsync(
                candidate => candidate.Id == request.VehicleId,
                cancellationToken))
        {
            return new(MaintenanceWriteOutcome.NotFound, null, "Araç kataloğu kaydı bulunamadı.");
        }

        var now = UtcNow();
        var userVehicle = new UserVehicle
        {
            UserId = userId,
            VehicleId = request.VehicleId,
            Nickname = nickname,
            CurrentOdometerKm = request.CurrentOdometerKm,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _context.UserVehicles.Add(userVehicle);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new(MaintenanceWriteOutcome.Created, userVehicle);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            existing = await _context.UserVehicles.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.IdempotencyKey == idempotencyKey,
                cancellationToken);
            return existing != null &&
                   existing.VehicleId == request.VehicleId &&
                   existing.Nickname == nickname &&
                   existing.CurrentOdometerKm == request.CurrentOdometerKm
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Araç kaydı eşzamanlı olarak değişti.");
        }
    }

    public async Task<MaintenanceWriteResult<UserVehicle>> UpdateVehicleAsync(
        int userId,
        int userVehicleId,
        UpdateUserVehicleRequest request,
        CancellationToken cancellationToken = default)
    {
        var nickname = request.Nickname?.Trim() ?? string.Empty;
        if (nickname is { Length: < 1 or > 80 } || !IsValidOdometer(request.CurrentOdometerKm))
        {
            return Invalid<UserVehicle>("Araç bilgileri geçersiz.");
        }

        var vehicle = await _context.UserVehicles.SingleOrDefaultAsync(
            candidate => candidate.Id == userVehicleId && candidate.UserId == userId,
            cancellationToken);
        if (vehicle == null)
        {
            return new(MaintenanceWriteOutcome.NotFound, null);
        }

        if (vehicle.ConcurrencyToken != request.ConcurrencyToken)
        {
            return new(MaintenanceWriteOutcome.Conflict, null, "Araç kaydı başka bir işlem tarafından güncellendi.");
        }

        if (request.CurrentOdometerKm < vehicle.CurrentOdometerKm)
        {
            return Invalid<UserVehicle>("Güncel kilometre önceki değerden düşük olamaz.");
        }

        vehicle.Nickname = nickname;
        vehicle.CurrentOdometerKm = request.CurrentOdometerKm;
        vehicle.IsActive = request.IsActive;
        vehicle.UpdatedAtUtc = UtcNow();
        vehicle.ConcurrencyToken = Guid.NewGuid();
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new(MaintenanceWriteOutcome.Updated, vehicle);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.ChangeTracker.Clear();
            return new(MaintenanceWriteOutcome.Conflict, null, "Araç kaydı eşzamanlı olarak değişti.");
        }
    }

    public async Task<List<MaintenanceRecord>?> GetRecordsAsync(
        int userId,
        int userVehicleId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsVehicleAsync(userId, userVehicleId, cancellationToken)) return null;
        return await _context.MaintenanceRecords
            .AsNoTracking()
            .Where(candidate => candidate.UserVehicleId == userVehicleId)
            .Include(candidate => candidate.Items)
                .ThenInclude(item => item.Product)
            .OrderByDescending(candidate => candidate.ServiceDateUtc)
            .ThenByDescending(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceWriteResult<MaintenanceRecord>> AddRecordAsync(
        int userId,
        int userVehicleId,
        string idempotencyKey,
        CreateMaintenanceRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedItems = NormalizeItems(request.Items);
        var serviceDate = NormalizeUtc(request.ServiceDateUtc);
        var serviceProvider = NormalizeOptional(request.ServiceProvider);
        var notes = NormalizeOptional(request.Notes);
        if (serviceDate < new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc) ||
            serviceDate > UtcNow().AddDays(1) ||
            !IsValidOdometer(request.OdometerKm) ||
            normalizedItems == null ||
            serviceProvider?.Length > 120 ||
            notes?.Length > 1000)
        {
            return Invalid<MaintenanceRecord>("Bakım kaydı bilgileri geçersiz.");
        }

        var ownerVehicle = await _context.UserVehicles.SingleOrDefaultAsync(
            candidate => candidate.Id == userVehicleId && candidate.UserId == userId,
            cancellationToken);
        if (ownerVehicle == null)
        {
            return new(MaintenanceWriteOutcome.NotFound, null);
        }

        var existing = await _context.MaintenanceRecords
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(
                candidate => candidate.UserVehicleId == userVehicleId &&
                             candidate.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existing != null)
        {
            return RecordMatches(existing, serviceDate, request.OdometerKm, serviceProvider, notes, normalizedItems)
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Idempotency-Key farklı bir bakım kaydında kullanılmış.");
        }

        var productIds = normalizedItems
            .Where(item => item.ProductId.HasValue)
            .Select(item => item.ProductId!.Value)
            .Distinct()
            .ToArray();
        if (productIds.Length > 0)
        {
            var foundProducts = await _context.Products.CountAsync(
                candidate => productIds.Contains(candidate.Id),
                cancellationToken);
            if (foundProducts != productIds.Length)
            {
                return Invalid<MaintenanceRecord>("Bakım kalemlerinden biri bilinmeyen bir ürüne bağlı.");
            }
        }

        var now = UtcNow();
        var record = new MaintenanceRecord
        {
            UserVehicleId = userVehicleId,
            IdempotencyKey = idempotencyKey,
            ServiceDateUtc = serviceDate,
            OdometerKm = request.OdometerKm,
            ServiceProvider = serviceProvider,
            Notes = notes,
            CreatedAtUtc = now,
            Items = normalizedItems.Select(item => new MaintenanceRecordItem
            {
                ProductId = item.ProductId,
                ServiceType = item.ServiceType,
                Description = item.Description,
                Quantity = item.Quantity,
                UnitCost = item.UnitCost
            }).ToList()
        };

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        _context.MaintenanceRecords.Add(record);
        if (!ownerVehicle.CurrentOdometerKm.HasValue || request.OdometerKm > ownerVehicle.CurrentOdometerKm)
        {
            ownerVehicle.CurrentOdometerKm = request.OdometerKm;
            ownerVehicle.UpdatedAtUtc = now;
            ownerVehicle.ConcurrencyToken = Guid.NewGuid();
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(MaintenanceWriteOutcome.Created, record);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            existing = await _context.MaintenanceRecords
                .AsNoTracking()
                .Include(candidate => candidate.Items)
                .SingleOrDefaultAsync(
                    candidate => candidate.UserVehicleId == userVehicleId &&
                                 candidate.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            return existing != null &&
                   RecordMatches(existing, serviceDate, request.OdometerKm, serviceProvider, notes, normalizedItems)
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Bakım kaydı eşzamanlı olarak değişti.");
        }
    }

    public async Task<List<MaintenanceReminder>?> GetRemindersAsync(
        int userId,
        int userVehicleId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsVehicleAsync(userId, userVehicleId, cancellationToken)) return null;
        return await _context.MaintenanceReminders
            .AsNoTracking()
            .Where(candidate => candidate.UserVehicleId == userVehicleId)
            .OrderBy(candidate => candidate.IsCompleted)
            .ThenBy(candidate => candidate.DueDateUtc)
            .ThenBy(candidate => candidate.DueOdometerKm)
            .ToListAsync(cancellationToken);
    }

    public async Task<MaintenanceWriteResult<MaintenanceReminder>> AddReminderAsync(
        int userId,
        int userVehicleId,
        string idempotencyKey,
        CreateMaintenanceReminderRequest request,
        CancellationToken cancellationToken = default)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        DateTime? dueDate = request.DueDateUtc.HasValue
            ? NormalizeUtc(request.DueDateUtc.Value)
            : null;
        if (title is { Length: < 1 or > 120 } ||
            (!dueDate.HasValue && !request.DueOdometerKm.HasValue) ||
            dueDate < new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc) ||
            !IsValidOdometer(request.DueOdometerKm))
        {
            return Invalid<MaintenanceReminder>("Hatırlatıcı için tarih veya kilometre hedefi gereklidir.");
        }

        if (!await OwnsVehicleAsync(userId, userVehicleId, cancellationToken))
        {
            return new(MaintenanceWriteOutcome.NotFound, null);
        }

        var existing = await _context.MaintenanceReminders.SingleOrDefaultAsync(
            candidate => candidate.UserVehicleId == userVehicleId &&
                         candidate.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing != null)
        {
            return existing.Title == title &&
                   existing.DueDateUtc == dueDate &&
                   existing.DueOdometerKm == request.DueOdometerKm
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Idempotency-Key farklı bir hatırlatıcıda kullanılmış.");
        }

        var now = UtcNow();
        var reminder = new MaintenanceReminder
        {
            UserVehicleId = userVehicleId,
            Title = title,
            DueDateUtc = dueDate,
            DueOdometerKm = request.DueOdometerKm,
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _context.MaintenanceReminders.Add(reminder);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new(MaintenanceWriteOutcome.Created, reminder);
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            existing = await _context.MaintenanceReminders.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.UserVehicleId == userVehicleId &&
                             candidate.IdempotencyKey == idempotencyKey,
                cancellationToken);
            return existing != null &&
                   existing.Title == title &&
                   existing.DueDateUtc == dueDate &&
                   existing.DueOdometerKm == request.DueOdometerKm
                ? new(MaintenanceWriteOutcome.Replayed, existing)
                : new(MaintenanceWriteOutcome.Conflict, null, "Hatırlatıcı eşzamanlı olarak değişti.");
        }
    }

    public async Task<MaintenanceWriteResult<MaintenanceReminder>> CompleteReminderAsync(
        int userId,
        long reminderId,
        CompleteMaintenanceReminderRequest request,
        CancellationToken cancellationToken = default)
    {
        var reminder = await _context.MaintenanceReminders
            .Include(candidate => candidate.UserVehicle)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == reminderId && candidate.UserVehicle.UserId == userId,
                cancellationToken);
        if (reminder == null) return new(MaintenanceWriteOutcome.NotFound, null);
        if (reminder.IsCompleted) return new(MaintenanceWriteOutcome.Replayed, reminder);
        if (reminder.ConcurrencyToken != request.ConcurrencyToken)
        {
            return new(MaintenanceWriteOutcome.Conflict, null, "Hatırlatıcı başka bir işlem tarafından güncellendi.");
        }

        reminder.IsCompleted = true;
        reminder.CompletedAtUtc = UtcNow();
        reminder.UpdatedAtUtc = reminder.CompletedAtUtc.Value;
        reminder.ConcurrencyToken = Guid.NewGuid();
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new(MaintenanceWriteOutcome.Updated, reminder);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.ChangeTracker.Clear();
            return new(MaintenanceWriteOutcome.Conflict, null, "Hatırlatıcı eşzamanlı olarak değişti.");
        }
    }

    private Task<bool> OwnsVehicleAsync(
        int userId,
        int userVehicleId,
        CancellationToken cancellationToken) =>
        _context.UserVehicles.AnyAsync(
            candidate => candidate.Id == userVehicleId && candidate.UserId == userId,
            cancellationToken);

    private static List<NormalizedMaintenanceItem>? NormalizeItems(
        IReadOnlyList<CreateMaintenanceRecordItemRequest>? items)
    {
        if (items is not { Count: > 0 } || items.Count > MaximumItemsPerRecord) return null;
        var normalized = new List<NormalizedMaintenanceItem>(items.Count);
        foreach (var item in items)
        {
            var serviceType = item.ServiceType?.Trim() ?? string.Empty;
            var description = item.Description?.Trim() ?? string.Empty;
            if (serviceType is { Length: < 1 or > 80 } ||
                !MaintenanceServiceTypes.All.Contains(serviceType) ||
                description is { Length: < 1 or > 250 } ||
                item.Quantity is < 1 or > 1000 ||
                item.UnitCost is < 0 or > 1_000_000_000 ||
                item.ProductId <= 0)
            {
                return null;
            }

            normalized.Add(new(
                item.ProductId,
                serviceType,
                description,
                item.Quantity,
                item.UnitCost));
        }

        return normalized;
    }

    private static bool RecordMatches(
        MaintenanceRecord existing,
        DateTime serviceDate,
        int odometerKm,
        string? serviceProvider,
        string? notes,
        IReadOnlyList<NormalizedMaintenanceItem> items) =>
        existing.ServiceDateUtc == serviceDate &&
        existing.OdometerKm == odometerKm &&
        existing.ServiceProvider == serviceProvider &&
        existing.Notes == notes &&
        existing.Items.Count == items.Count &&
        existing.Items.OrderBy(item => item.Id).Zip(items).All(pair =>
            pair.First.ProductId == pair.Second.ProductId &&
            pair.First.ServiceType == pair.Second.ServiceType &&
            pair.First.Description == pair.Second.Description &&
            pair.First.Quantity == pair.Second.Quantity &&
            pair.First.UnitCost == pair.Second.UnitCost);

    private static MaintenanceWriteResult<T> Invalid<T>(string message) =>
        new(MaintenanceWriteOutcome.InvalidRequest, default, message);

    private static bool IsValidOdometer(int? value) =>
        !value.HasValue || value.Value is >= 0 and <= MaximumOdometerKm;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed record NormalizedMaintenanceItem(
        int? ProductId,
        string ServiceType,
        string Description,
        int Quantity,
        decimal? UnitCost);
}
