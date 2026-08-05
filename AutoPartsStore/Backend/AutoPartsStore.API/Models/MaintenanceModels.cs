using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models;

public static class MaintenanceServiceTypes
{
    public const string PeriodicMaintenance = "PeriodicMaintenance";
    public const string OilChange = "OilChange";
    public const string FilterChange = "FilterChange";
    public const string BrakeService = "BrakeService";
    public const string Repair = "Repair";
    public const string Inspection = "Inspection";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [PeriodicMaintenance, OilChange, FilterChange, BrakeService, Repair, Inspection],
        StringComparer.Ordinal);
}

public sealed class UserVehicle
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int VehicleId { get; set; }

    [Required, MaxLength(80)]
    public string Nickname { get; set; } = string.Empty;

    public int? CurrentOdometerKm { get; set; }
    public bool IsActive { get; set; } = true;

    [Required, MaxLength(100), JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public User User { get; set; } = null!;
    public Vehicle Vehicle { get; set; } = null!;
    public ICollection<MaintenanceRecord> MaintenanceRecords { get; set; } =
        new List<MaintenanceRecord>();
    public ICollection<MaintenanceReminder> Reminders { get; set; } =
        new List<MaintenanceReminder>();
}

public sealed class MaintenanceRecord
{
    public long Id { get; set; }
    public int UserVehicleId { get; set; }

    [Required, MaxLength(100), JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime ServiceDateUtc { get; set; }
    public int OdometerKm { get; set; }

    [MaxLength(120)]
    public string? ServiceProvider { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public UserVehicle UserVehicle { get; set; } = null!;
    public ICollection<MaintenanceRecordItem> Items { get; set; } =
        new List<MaintenanceRecordItem>();
}

public sealed class MaintenanceRecordItem
{
    public long Id { get; set; }
    public long MaintenanceRecordId { get; set; }
    public int? ProductId { get; set; }

    [Required, MaxLength(80)]
    public string ServiceType { get; set; } = string.Empty;

    [Required, MaxLength(250)]
    public string Description { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public decimal? UnitCost { get; set; }

    public MaintenanceRecord MaintenanceRecord { get; set; } = null!;
    public Product? Product { get; set; }
}

public sealed class MaintenanceReminder
{
    public long Id { get; set; }
    public int UserVehicleId { get; set; }

    [Required, MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    public DateTime? DueDateUtc { get; set; }
    public int? DueOdometerKm { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    [Required, MaxLength(100), JsonIgnore]
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [ConcurrencyCheck, JsonIgnore]
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public UserVehicle UserVehicle { get; set; } = null!;
}

public sealed record CreateUserVehicleRequest(
    int VehicleId,
    string Nickname,
    int? CurrentOdometerKm);

public sealed record UpdateUserVehicleRequest(
    string Nickname,
    int? CurrentOdometerKm,
    bool IsActive,
    Guid ConcurrencyToken);

public sealed record CreateMaintenanceRecordRequest(
    DateTime ServiceDateUtc,
    int OdometerKm,
    string? ServiceProvider,
    string? Notes,
    IReadOnlyList<CreateMaintenanceRecordItemRequest> Items);

public sealed record CreateMaintenanceRecordItemRequest(
    int? ProductId,
    string ServiceType,
    string Description,
    int Quantity,
    decimal? UnitCost);

public sealed record CreateMaintenanceReminderRequest(
    string Title,
    DateTime? DueDateUtc,
    int? DueOdometerKm);

public sealed record CompleteMaintenanceReminderRequest(Guid ConcurrencyToken);

public enum MaintenanceWriteOutcome
{
    Created,
    Updated,
    Replayed,
    NotFound,
    Conflict,
    InvalidRequest
}

public sealed record MaintenanceWriteResult<T>(
    MaintenanceWriteOutcome Outcome,
    T? Value,
    string? Message = null);
