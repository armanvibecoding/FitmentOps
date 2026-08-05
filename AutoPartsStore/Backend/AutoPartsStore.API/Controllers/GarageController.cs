using System.Security.Claims;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AutoPartsStore.API.Controllers;

[ApiController]
[Route("api/garage")]
[Authorize]
public sealed class GarageController : ControllerBase
{
    private readonly MaintenanceJournalService _journal;
    private readonly TimeProvider _timeProvider;

    public GarageController(
        MaintenanceJournalService journal,
        TimeProvider timeProvider)
    {
        _journal = journal;
        _timeProvider = timeProvider;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserVehicleDto>>> GetGarage(
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var vehicles = await _journal.GetGarageAsync(userId, cancellationToken);
        return Ok(vehicles.Select(ToVehicleDto).ToList());
    }

    [HttpPost]
    [EnableRateLimiting("garage-write")]
    public async Task<IActionResult> CreateVehicle(
        CreateUserVehicleRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!IsValidIdempotencyKey(idempotencyKey)) return InvalidIdempotencyKey();
        var result = await _journal.CreateVehicleAsync(
            userId,
            idempotencyKey!,
            request,
            cancellationToken);
        return MapVehicleWrite(result);
    }

    [HttpPut("{userVehicleId:int}")]
    [EnableRateLimiting("garage-write")]
    public async Task<IActionResult> UpdateVehicle(
        int userVehicleId,
        UpdateUserVehicleRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _journal.UpdateVehicleAsync(
            userId,
            userVehicleId,
            request,
            cancellationToken);
        return MapVehicleWrite(result);
    }

    [HttpGet("{userVehicleId:int}/maintenance")]
    public async Task<IActionResult> GetMaintenance(
        int userVehicleId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var records = await _journal.GetRecordsAsync(userId, userVehicleId, cancellationToken);
        if (records == null) return NotFound();
        return Ok(records.Select(ToRecordDto).ToList());
    }

    [HttpPost("{userVehicleId:int}/maintenance")]
    [EnableRateLimiting("garage-write")]
    public async Task<IActionResult> AddMaintenance(
        int userVehicleId,
        CreateMaintenanceRecordRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!IsValidIdempotencyKey(idempotencyKey)) return InvalidIdempotencyKey();
        var result = await _journal.AddRecordAsync(
            userId,
            userVehicleId,
            idempotencyKey!,
            request,
            cancellationToken);
        return result.Outcome switch
        {
            MaintenanceWriteOutcome.Created =>
                StatusCode(StatusCodes.Status201Created, ToRecordDto(result.Value!)),
            MaintenanceWriteOutcome.Replayed => Ok(ToRecordDto(result.Value!)),
            MaintenanceWriteOutcome.NotFound => NotFound(),
            MaintenanceWriteOutcome.Conflict => Conflict(new { message = result.Message }),
            MaintenanceWriteOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("{userVehicleId:int}/reminders")]
    public async Task<IActionResult> GetReminders(
        int userVehicleId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var reminders = await _journal.GetRemindersAsync(userId, userVehicleId, cancellationToken);
        if (reminders == null) return NotFound();
        var currentOdometer = (await _journal.GetGarageAsync(userId, cancellationToken))
            .Single(vehicle => vehicle.Id == userVehicleId)
            .CurrentOdometerKm;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return Ok(reminders.Select(reminder => ToReminderDto(reminder, currentOdometer, now)).ToList());
    }

    [HttpPost("{userVehicleId:int}/reminders")]
    [EnableRateLimiting("garage-write")]
    public async Task<IActionResult> AddReminder(
        int userVehicleId,
        CreateMaintenanceReminderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!IsValidIdempotencyKey(idempotencyKey)) return InvalidIdempotencyKey();
        var result = await _journal.AddReminderAsync(
            userId,
            userVehicleId,
            idempotencyKey!,
            request,
            cancellationToken);
        var currentOdometer = result.Value == null
            ? null
            : (await _journal.GetGarageAsync(userId, cancellationToken))
                .Single(vehicle => vehicle.Id == userVehicleId)
                .CurrentOdometerKm;
        return MapReminderWrite(result, currentOdometer);
    }

    [HttpPost("reminders/{reminderId:long}/complete")]
    [EnableRateLimiting("garage-write")]
    public async Task<IActionResult> CompleteReminder(
        long reminderId,
        CompleteMaintenanceReminderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _journal.CompleteReminderAsync(
            userId,
            reminderId,
            request,
            cancellationToken);
        return MapReminderWrite(result, null);
    }

    private IActionResult MapVehicleWrite(MaintenanceWriteResult<UserVehicle> result) =>
        result.Outcome switch
        {
            MaintenanceWriteOutcome.Created =>
                StatusCode(StatusCodes.Status201Created, ToVehicleDto(result.Value!)),
            MaintenanceWriteOutcome.Updated or MaintenanceWriteOutcome.Replayed =>
                Ok(ToVehicleDto(result.Value!)),
            MaintenanceWriteOutcome.NotFound => NotFound(),
            MaintenanceWriteOutcome.Conflict => Conflict(new { message = result.Message }),
            MaintenanceWriteOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };

    private IActionResult MapReminderWrite(
        MaintenanceWriteResult<MaintenanceReminder> result,
        int? currentOdometer) =>
        result.Outcome switch
        {
            MaintenanceWriteOutcome.Created => StatusCode(
                StatusCodes.Status201Created,
                ToReminderDto(result.Value!, currentOdometer, _timeProvider.GetUtcNow().UtcDateTime)),
            MaintenanceWriteOutcome.Updated or MaintenanceWriteOutcome.Replayed => Ok(
                ToReminderDto(result.Value!, currentOdometer, _timeProvider.GetUtcNow().UtcDateTime)),
            MaintenanceWriteOutcome.NotFound => NotFound(),
            MaintenanceWriteOutcome.Conflict => Conflict(new { message = result.Message }),
            MaintenanceWriteOutcome.InvalidRequest => BadRequest(new { message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };

    private static UserVehicleDto ToVehicleDto(UserVehicle userVehicle)
    {
        var vehicle = userVehicle.Vehicle;
        var engine = vehicle?.Engine;
        var generation = engine?.Generation;
        var model = generation?.Model;
        return new UserVehicleDto(
            userVehicle.Id,
            userVehicle.VehicleId,
            userVehicle.Nickname,
            userVehicle.CurrentOdometerKm,
            userVehicle.IsActive,
            userVehicle.ConcurrencyToken,
            vehicle?.DisplayName,
            model?.Make?.Name,
            model?.Name,
            generation?.Name,
            engine?.Name,
            userVehicle.CreatedAtUtc,
            userVehicle.UpdatedAtUtc);
    }

    private static MaintenanceRecordDto ToRecordDto(MaintenanceRecord record) => new(
        record.Id,
        record.ServiceDateUtc,
        record.OdometerKm,
        record.ServiceProvider,
        record.Notes,
        record.Items.Select(item => new MaintenanceRecordItemDto(
            item.Id,
            item.ProductId,
            item.Product?.Name,
            item.ServiceType,
            item.Description,
            item.Quantity,
            item.UnitCost)).ToList(),
        record.CreatedAtUtc);

    private static MaintenanceReminderDto ToReminderDto(
        MaintenanceReminder reminder,
        int? currentOdometer,
        DateTime now)
    {
        var status = reminder.IsCompleted
            ? "Completed"
            : (reminder.DueDateUtc <= now ||
               (reminder.DueOdometerKm.HasValue &&
                currentOdometer.HasValue &&
                reminder.DueOdometerKm <= currentOdometer))
                ? "Due"
                : "Upcoming";
        return new(
            reminder.Id,
            reminder.Title,
            reminder.DueDateUtc,
            reminder.DueOdometerKm,
            status,
            reminder.CompletedAtUtc,
            reminder.ConcurrencyToken,
            reminder.CreatedAtUtc,
            reminder.UpdatedAtUtc);
    }

    private bool TryGetUserId(out int userId) =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId > 0;

    private static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: >= 16 and <= 100 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private BadRequestObjectResult InvalidIdempotencyKey() => BadRequest(new
    {
        message = "Idempotency-Key 16-100 harf, rakam, tire veya alt çizgi içermelidir."
    });
}

public sealed record UserVehicleDto(
    int Id,
    int VehicleId,
    string Nickname,
    int? CurrentOdometerKm,
    bool IsActive,
    Guid ConcurrencyToken,
    string? VehicleName,
    string? MakeName,
    string? ModelName,
    string? GenerationName,
    string? EngineName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record MaintenanceRecordItemDto(
    long Id,
    int? ProductId,
    string? ProductName,
    string ServiceType,
    string Description,
    int Quantity,
    decimal? UnitCost);

public sealed record MaintenanceRecordDto(
    long Id,
    DateTime ServiceDateUtc,
    int OdometerKm,
    string? ServiceProvider,
    string? Notes,
    IReadOnlyList<MaintenanceRecordItemDto> Items,
    DateTime CreatedAtUtc);

public sealed record MaintenanceReminderDto(
    long Id,
    string Title,
    DateTime? DueDateUtc,
    int? DueOdometerKm,
    string Status,
    DateTime? CompletedAtUtc,
    Guid ConcurrencyToken,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
