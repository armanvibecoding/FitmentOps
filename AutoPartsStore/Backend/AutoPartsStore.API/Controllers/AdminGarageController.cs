using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/Admin/garage")]
[Authorize(Policy = AdminPolicyNames.Support)]
public sealed class AdminGarageController : ControllerBase
{
    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public AdminGarageController(AutoPartsDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AdminGarageSummaryDto>> GetSummary(
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var recentSince = now.AddDays(-30);
        var activeVehicles = await _context.UserVehicles.AsNoTracking()
            .CountAsync(candidate => candidate.IsActive, cancellationToken);
        var usersWithVehicles = await _context.UserVehicles.AsNoTracking()
            .Select(candidate => candidate.UserId)
            .Distinct()
            .CountAsync(cancellationToken);
        var openReminders = await _context.MaintenanceReminders.AsNoTracking()
            .CountAsync(candidate => !candidate.IsCompleted, cancellationToken);
        var dueReminders = await _context.MaintenanceReminders.AsNoTracking()
            .CountAsync(
                candidate => !candidate.IsCompleted &&
                    ((candidate.DueDateUtc.HasValue && candidate.DueDateUtc <= now) ||
                     (candidate.DueOdometerKm.HasValue &&
                      candidate.UserVehicle.CurrentOdometerKm.HasValue &&
                      candidate.DueOdometerKm <= candidate.UserVehicle.CurrentOdometerKm)),
                cancellationToken);
        var recentRecords = await _context.MaintenanceRecords.AsNoTracking()
            .CountAsync(candidate => candidate.ServiceDateUtc >= recentSince, cancellationToken);

        return Ok(new AdminGarageSummaryDto(
            activeVehicles,
            usersWithVehicles,
            openReminders,
            dueReminders,
            recentRecords,
            now));
    }

    [HttpGet("users/{userId:int}")]
    public async Task<ActionResult<IReadOnlyList<AdminUserVehicleDto>>> GetUserGarage(
        int userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0) return BadRequest(new { message = "Geçerli bir kullanıcı ID girin." });
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var vehicles = await _context.UserVehicles
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId)
            .OrderByDescending(candidate => candidate.IsActive)
            .ThenBy(candidate => candidate.Nickname)
            .Select(candidate => new AdminUserVehicleDto(
                candidate.Id,
                candidate.UserId,
                candidate.Nickname,
                candidate.Vehicle.DisplayName,
                candidate.Vehicle.Engine.Generation.Model.Make.Name,
                candidate.Vehicle.Engine.Generation.Model.Name,
                candidate.Vehicle.Engine.Generation.Name,
                candidate.Vehicle.Engine.Name,
                candidate.CurrentOdometerKm,
                candidate.IsActive,
                candidate.MaintenanceRecords.Count,
                candidate.MaintenanceRecords
                    .Select(record => (DateTime?)record.ServiceDateUtc)
                    .Max(),
                candidate.Reminders.Count(reminder => !reminder.IsCompleted),
                candidate.Reminders.Count(reminder =>
                    !reminder.IsCompleted &&
                    ((reminder.DueDateUtc.HasValue && reminder.DueDateUtc <= now) ||
                     (reminder.DueOdometerKm.HasValue &&
                      candidate.CurrentOdometerKm.HasValue &&
                      reminder.DueOdometerKm <= candidate.CurrentOdometerKm))),
                candidate.CreatedAtUtc,
                candidate.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
        return Ok(vehicles);
    }
}

public sealed record AdminGarageSummaryDto(
    int ActiveVehicles,
    int UsersWithVehicles,
    int OpenReminders,
    int DueReminders,
    int MaintenanceRecordsInLastThirtyDays,
    DateTime ObservedAtUtc);

public sealed record AdminUserVehicleDto(
    int Id,
    int UserId,
    string Nickname,
    string VehicleName,
    string MakeName,
    string ModelName,
    string GenerationName,
    string EngineName,
    int? CurrentOdometerKm,
    bool IsActive,
    int MaintenanceRecordCount,
    DateTime? LastServiceDateUtc,
    int OpenReminderCount,
    int DueReminderCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
