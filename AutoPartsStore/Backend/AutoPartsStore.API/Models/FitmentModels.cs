using System.ComponentModel.DataAnnotations;

namespace AutoPartsStore.API.Models;

public enum FitmentAssertionKind
{
    Exact = 1,
    Compatible = 2
}

public enum FitmentMatchKind
{
    Unknown = 0,
    Compatible = 1,
    Exact = 2
}

public enum FitmentSourceKind
{
    UnverifiedImport = 0,
    Manufacturer = 1,
    AuthorizedSupplier = 2,
    LicensedCatalog = 3,
    ManualExpertReview = 4
}

public static class FitmentConfidencePolicy
{
    public const decimal MinimumCompatible = 0.80m;
    public const decimal MinimumExact = 0.90m;

    public static decimal MinimumFor(FitmentAssertionKind assertionKind) =>
        assertionKind == FitmentAssertionKind.Exact
            ? MinimumExact
            : MinimumCompatible;

    public static string Band(decimal? confidence) => confidence switch
    {
        >= 0.95m => "VeryHigh",
        >= MinimumExact => "High",
        >= MinimumCompatible => "Medium",
        _ => "Low"
    };
}

public enum PartIdentifierKind
{
    Oem = 1,
    Interchange = 2,
    ManufacturerPartNumber = 3,
    SupplierSku = 4
}

public enum FitmentWriteOutcome
{
    Created,
    Replayed,
    Conflict,
    InvalidRequest,
    NotFound
}

public sealed class VehicleMake
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string CanonicalKey { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public ICollection<VehicleModel> Models { get; set; } = new List<VehicleModel>();
}

public sealed class VehicleModel
{
    public int Id { get; set; }
    public int MakeId { get; set; }

    [Required, MaxLength(80)]
    public string CanonicalKey { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public VehicleMake Make { get; set; } = null!;
    public ICollection<VehicleGeneration> Generations { get; set; } = new List<VehicleGeneration>();
}

public sealed class VehicleGeneration
{
    public int Id { get; set; }
    public int ModelId { get; set; }

    [Required, MaxLength(80)]
    public string CanonicalKey { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public int? ProductionStartYear { get; set; }
    public int? ProductionEndYear { get; set; }

    public VehicleModel Model { get; set; } = null!;
    public ICollection<VehicleEngine> Engines { get; set; } = new List<VehicleEngine>();
}

public sealed class VehicleEngine
{
    public int Id { get; set; }
    public int GenerationId { get; set; }

    [Required, MaxLength(80)]
    public string CanonicalKey { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? EngineCode { get; set; }

    [MaxLength(40)]
    public string? FuelType { get; set; }

    public int? DisplacementCc { get; set; }
    public decimal? PowerKw { get; set; }

    public VehicleGeneration Generation { get; set; } = null!;
    public ICollection<Vehicle> Vehicles { get; set; } = new List<Vehicle>();
}

public sealed class Vehicle
{
    public int Id { get; set; }
    public int EngineId { get; set; }

    // This is an application-owned canonical key, not a VIN or a licensed catalog id.
    [Required, MaxLength(120)]
    public string CanonicalKey { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? BodyStyle { get; set; }

    [MaxLength(80)]
    public string? Transmission { get; set; }

    [MaxLength(40)]
    public string? DriveType { get; set; }

    [MaxLength(40)]
    public string? Market { get; set; }

    public int? ProductionStartYear { get; set; }
    public int? ProductionEndYear { get; set; }

    public VehicleEngine Engine { get; set; } = null!;
    public ICollection<ProductFitment> ProductFitments { get; set; } = new List<ProductFitment>();
}

public sealed class ProductFitment
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int VehicleId { get; set; }
    public FitmentAssertionKind AssertionKind { get; set; }
    public decimal Confidence { get; set; }
    public bool IsVerified { get; set; }
    public FitmentSourceKind SourceKind { get; set; }

    [Required, MaxLength(120)]
    public string SourceName { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string SourceRecordId { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Provenance { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Product Product { get; set; } = null!;
    public Vehicle Vehicle { get; set; } = null!;
}

public sealed class ProductIdentifier
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public PartIdentifierKind Kind { get; set; }

    // Examples are an OEM/manufacturer name or another explicit identifier namespace.
    [Required, MaxLength(120)]
    public string SchemeAuthority { get; set; } = string.Empty;

    [Required, MaxLength(160)]
    public string Value { get; set; } = string.Empty;

    [Required, MaxLength(160)]
    public string NormalizedValue { get; set; } = string.Empty;

    public bool IsVerified { get; set; }
    public FitmentSourceKind SourceKind { get; set; }

    [Required, MaxLength(120)]
    public string SourceName { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string SourceRecordId { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Provenance { get; set; } = string.Empty;

    public DateTime ValidFromUtc { get; set; }
    public DateTime? ValidToUtc { get; set; }

    public Product Product { get; set; } = null!;
}

public sealed record VehicleTreeUpsertRequest
{
    public required string MakeKey { get; init; }
    public required string MakeName { get; init; }
    public required string ModelKey { get; init; }
    public required string ModelName { get; init; }
    public required string GenerationKey { get; init; }
    public required string GenerationName { get; init; }
    public int? GenerationStartYear { get; init; }
    public int? GenerationEndYear { get; init; }
    public required string EngineKey { get; init; }
    public required string EngineName { get; init; }
    public string? EngineCode { get; init; }
    public string? FuelType { get; init; }
    public int? DisplacementCc { get; init; }
    public decimal? PowerKw { get; init; }
    public required string VehicleKey { get; init; }
    public required string VehicleName { get; init; }
    public string? BodyStyle { get; init; }
    public string? Transmission { get; init; }
    public string? DriveType { get; init; }
    public string? Market { get; init; }
    public int? VehicleStartYear { get; init; }
    public int? VehicleEndYear { get; init; }
}

public sealed record ProductFitmentUpsertRequest(
    int ProductId,
    int VehicleId,
    FitmentAssertionKind AssertionKind,
    decimal Confidence,
    bool IsVerified,
    FitmentSourceKind SourceKind,
    string SourceName,
    string SourceRecordId,
    string Provenance,
    string IdempotencyKey,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc);

public sealed record ProductIdentifierUpsertRequest(
    int ProductId,
    PartIdentifierKind Kind,
    string SchemeAuthority,
    string Value,
    bool IsVerified,
    FitmentSourceKind SourceKind,
    string SourceName,
    string SourceRecordId,
    string Provenance,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc);

public sealed record FitmentCheckQuery(
    int ProductId,
    int VehicleId,
    DateTime EffectiveAtUtc);

public sealed record FitmentReadQuery(
    int? ProductId,
    int? VehicleId,
    DateTime EffectiveAtUtc,
    int Offset = 0,
    int Limit = 50,
    bool VerifiedOnly = true);

public sealed record VehicleTreeWriteResult(
    FitmentWriteOutcome Outcome,
    Vehicle? Vehicle = null,
    string? Message = null);

public sealed record ProductFitmentWriteResult(
    FitmentWriteOutcome Outcome,
    ProductFitment? Fitment = null,
    string? Message = null);

public sealed record ProductIdentifierWriteResult(
    FitmentWriteOutcome Outcome,
    ProductIdentifier? Identifier = null,
    string? Message = null);

public sealed record FitmentCheckResult(
    FitmentMatchKind Match,
    bool IsVerified,
    decimal? Confidence = null,
    string? SourceName = null,
    string? SourceRecordId = null,
    string? Provenance = null,
    DateTime? ValidFromUtc = null,
    DateTime? ValidToUtc = null,
    string? Message = null);

public sealed record FitmentReadItem(
    long FitmentId,
    int ProductId,
    string ProductName,
    int VehicleId,
    string VehicleName,
    string MakeName,
    string ModelName,
    string GenerationName,
    string EngineName,
    FitmentAssertionKind AssertionKind,
    decimal Confidence,
    bool IsVerified,
    FitmentSourceKind SourceKind,
    string SourceName,
    string SourceRecordId,
    string Provenance,
    DateTime ValidFromUtc,
    DateTime? ValidToUtc);

public sealed record FitmentReadPage(
    IReadOnlyList<FitmentReadItem> Items,
    int Offset,
    int Limit,
    bool HasMore,
    string? ValidationError = null);
