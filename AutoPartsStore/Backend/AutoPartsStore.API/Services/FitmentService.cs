using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public sealed class FitmentService
{
    public const int MaxReadLimit = 100;
    public const int MaxReadOffset = 10_000;

    private readonly AutoPartsDbContext _context;
    private readonly TimeProvider _timeProvider;

    public FitmentService(
        AutoPartsDbContext context,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<VehicleTreeWriteResult> UpsertVehicleTreeAsync(
        VehicleTreeUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateVehicleTree(request);
        if (validationError != null)
        {
            return new VehicleTreeWriteResult(
                FitmentWriteOutcome.InvalidRequest,
                Message: validationError);
        }

        var makeKey = NormalizeKey(request.MakeKey);
        var modelKey = NormalizeKey(request.ModelKey);
        var generationKey = NormalizeKey(request.GenerationKey);
        var engineKey = NormalizeKey(request.EngineKey);
        var vehicleKey = NormalizeKey(request.VehicleKey);

        var make = await _context.Set<VehicleMake>()
            .SingleOrDefaultAsync(item => item.CanonicalKey == makeKey, cancellationToken);
        if (make != null && !SameText(make.Name, request.MakeName))
        {
            return Conflict("Araç markası anahtarı farklı bir adla kayıtlı.");
        }

        make ??= new VehicleMake
        {
            CanonicalKey = makeKey,
            Name = request.MakeName.Trim()
        };

        VehicleModel? model = null;
        if (make.Id != 0)
        {
            model = await _context.Set<VehicleModel>()
                .SingleOrDefaultAsync(
                    item => item.MakeId == make.Id && item.CanonicalKey == modelKey,
                    cancellationToken);
        }

        if (model != null && !SameText(model.Name, request.ModelName))
        {
            return Conflict("Araç modeli anahtarı farklı bir adla kayıtlı.");
        }

        model ??= new VehicleModel
        {
            Make = make,
            CanonicalKey = modelKey,
            Name = request.ModelName.Trim()
        };

        VehicleGeneration? generation = null;
        if (model.Id != 0)
        {
            generation = await _context.Set<VehicleGeneration>()
                .SingleOrDefaultAsync(
                    item => item.ModelId == model.Id && item.CanonicalKey == generationKey,
                    cancellationToken);
        }

        if (generation != null &&
            (!SameText(generation.Name, request.GenerationName) ||
             generation.ProductionStartYear != request.GenerationStartYear ||
             generation.ProductionEndYear != request.GenerationEndYear))
        {
            return Conflict("Araç nesli anahtarı farklı ayrıntılarla kayıtlı.");
        }

        generation ??= new VehicleGeneration
        {
            Model = model,
            CanonicalKey = generationKey,
            Name = request.GenerationName.Trim(),
            ProductionStartYear = request.GenerationStartYear,
            ProductionEndYear = request.GenerationEndYear
        };

        VehicleEngine? engine = null;
        if (generation.Id != 0)
        {
            engine = await _context.Set<VehicleEngine>()
                .SingleOrDefaultAsync(
                    item => item.GenerationId == generation.Id && item.CanonicalKey == engineKey,
                    cancellationToken);
        }

        if (engine != null && !EngineMatches(engine, request))
        {
            return Conflict("Motor anahtarı farklı ayrıntılarla kayıtlı.");
        }

        engine ??= new VehicleEngine
        {
            Generation = generation,
            CanonicalKey = engineKey,
            Name = request.EngineName.Trim(),
            EngineCode = CleanOptional(request.EngineCode),
            FuelType = CleanOptional(request.FuelType),
            DisplacementCc = request.DisplacementCc,
            PowerKw = request.PowerKw
        };

        Vehicle? vehicle = null;
        if (engine.Id != 0)
        {
            vehicle = await _context.Set<Vehicle>()
                .SingleOrDefaultAsync(
                    item => item.EngineId == engine.Id && item.CanonicalKey == vehicleKey,
                    cancellationToken);
        }

        if (vehicle != null)
        {
            return VehicleMatches(vehicle, request)
                ? new VehicleTreeWriteResult(FitmentWriteOutcome.Replayed, vehicle)
                : Conflict("Araç anahtarı farklı ayrıntılarla kayıtlı.");
        }

        vehicle = new Vehicle
        {
            Engine = engine,
            CanonicalKey = vehicleKey,
            DisplayName = request.VehicleName.Trim(),
            BodyStyle = CleanOptional(request.BodyStyle),
            Transmission = CleanOptional(request.Transmission),
            DriveType = CleanOptional(request.DriveType),
            Market = CleanOptional(request.Market),
            ProductionStartYear = request.VehicleStartYear,
            ProductionEndYear = request.VehicleEndYear
        };

        _context.Set<Vehicle>().Add(vehicle);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new VehicleTreeWriteResult(FitmentWriteOutcome.Created, vehicle);
        }
        catch (DbUpdateException)
        {
            DetachAddedGraph(vehicle);

            var concurrentVehicle = await FindVehicleAsync(
                makeKey,
                modelKey,
                generationKey,
                engineKey,
                vehicleKey,
                cancellationToken);
            if (concurrentVehicle != null &&
                SameText(concurrentVehicle.Engine.Generation.Model.Make.Name, request.MakeName) &&
                SameText(concurrentVehicle.Engine.Generation.Model.Name, request.ModelName) &&
                GenerationMatches(concurrentVehicle.Engine.Generation, request) &&
                EngineMatches(concurrentVehicle.Engine, request) &&
                VehicleMatches(concurrentVehicle, request))
            {
                return new VehicleTreeWriteResult(
                    FitmentWriteOutcome.Replayed,
                    concurrentVehicle);
            }

            return Conflict("Araç ağacının benzersiz anahtarlarından biri başka bir kayıtla çakıştı.");
        }
    }

    public async Task<ProductFitmentWriteResult> UpsertProductFitmentAsync(
        ProductFitmentUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateFitment(request);
        if (validationError != null)
        {
            return new ProductFitmentWriteResult(
                FitmentWriteOutcome.InvalidRequest,
                Message: validationError);
        }

        var normalized = Normalize(request);
        var byIdempotencyKey = await _context.Set<ProductFitment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.IdempotencyKey == normalized.IdempotencyKey,
                cancellationToken);
        if (byIdempotencyKey != null)
        {
            return ResolveExisting(byIdempotencyKey, normalized, "İdempotensi anahtarı farklı bir fitment beyanıyla kayıtlı.");
        }

        var byPair = await _context.Set<ProductFitment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProductId == normalized.ProductId &&
                        item.VehicleId == normalized.VehicleId,
                cancellationToken);
        if (byPair != null)
        {
            return ResolveExisting(byPair, normalized, "Ürün/araç çifti farklı bir fitment beyanıyla kayıtlı.");
        }

        var bySourceRecord = await _context.Set<ProductFitment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.SourceName == normalized.SourceName &&
                        item.SourceRecordId == normalized.SourceRecordId,
                cancellationToken);
        if (bySourceRecord != null)
        {
            return ResolveExisting(bySourceRecord, normalized, "Kaynak kayıt kimliği farklı bir fitment beyanıyla kayıtlı.");
        }

        if (!await _context.Set<Product>().AnyAsync(
                item => item.Id == normalized.ProductId,
                cancellationToken))
        {
            return new ProductFitmentWriteResult(
                FitmentWriteOutcome.NotFound,
                Message: "Ürün bulunamadı.");
        }

        if (!await _context.Set<Vehicle>().AnyAsync(
                item => item.Id == normalized.VehicleId,
                cancellationToken))
        {
            return new ProductFitmentWriteResult(
                FitmentWriteOutcome.NotFound,
                Message: "Araç bulunamadı.");
        }

        var fitment = new ProductFitment
        {
            ProductId = normalized.ProductId,
            VehicleId = normalized.VehicleId,
            AssertionKind = normalized.AssertionKind,
            Confidence = normalized.Confidence,
            IsVerified = normalized.IsVerified,
            SourceKind = normalized.SourceKind,
            SourceName = normalized.SourceName,
            SourceRecordId = normalized.SourceRecordId,
            Provenance = normalized.Provenance,
            IdempotencyKey = normalized.IdempotencyKey,
            ValidFromUtc = normalized.ValidFromUtc,
            ValidToUtc = normalized.ValidToUtc,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };

        _context.Set<ProductFitment>().Add(fitment);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new ProductFitmentWriteResult(FitmentWriteOutcome.Created, fitment);
        }
        catch (DbUpdateException)
        {
            _context.Entry(fitment).State = EntityState.Detached;

            var concurrentFitment = await _context.Set<ProductFitment>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.IdempotencyKey == normalized.IdempotencyKey ||
                            (item.ProductId == normalized.ProductId &&
                             item.VehicleId == normalized.VehicleId) ||
                            (item.SourceName == normalized.SourceName &&
                             item.SourceRecordId == normalized.SourceRecordId),
                    cancellationToken);
            if (concurrentFitment != null)
            {
                return ResolveExisting(
                    concurrentFitment,
                    normalized,
                    "Eşzamanlı kayıt farklı bir fitment beyanı oluşturdu.");
            }

            return new ProductFitmentWriteResult(
                FitmentWriteOutcome.Conflict,
                Message: "Fitment kaydı benzersiz ürün/araç, kaynak veya idempotensi anahtarıyla çakıştı.");
        }
    }

    public async Task<FitmentCheckResult> CheckAsync(
        FitmentCheckQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.ProductId <= 0 || query.VehicleId <= 0)
        {
            return Unknown("Ürün ve araç kimlikleri pozitif olmalıdır.");
        }

        if (query.EffectiveAtUtc.Kind != DateTimeKind.Utc)
        {
            return Unknown("Eşleşme tarihi UTC olmalıdır.");
        }

        var fitment = await _context.Set<ProductFitment>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProductId == query.ProductId &&
                        item.VehicleId == query.VehicleId &&
                        item.ValidFromUtc <= query.EffectiveAtUtc &&
                        (item.ValidToUtc == null || query.EffectiveAtUtc < item.ValidToUtc),
                cancellationToken);

        if (fitment == null)
        {
            return Unknown("Bu ürün/araç çifti için geçerli bir fitment kanıtı yok.");
        }

        if (!fitment.IsVerified || fitment.SourceKind == FitmentSourceKind.UnverifiedImport)
        {
            return new FitmentCheckResult(
                FitmentMatchKind.Unknown,
                false,
                fitment.Confidence,
                fitment.SourceName,
                fitment.SourceRecordId,
                fitment.Provenance,
                fitment.ValidFromUtc,
                fitment.ValidToUtc,
                "Fitment beyanı doğrulanmadığı için uyumluluk iddiası üretilmedi.");
        }

        var minimumConfidence = FitmentConfidencePolicy.MinimumFor(fitment.AssertionKind);
        if (fitment.Confidence < minimumConfidence)
        {
            return new FitmentCheckResult(
                FitmentMatchKind.Unknown,
                true,
                fitment.Confidence,
                fitment.SourceName,
                fitment.SourceRecordId,
                fitment.Provenance,
                fitment.ValidFromUtc,
                fitment.ValidToUtc,
                "Doğrulanmış kayıt güven eşiğinin altında olduğu için uyumluluk iddiası üretilmedi.");
        }

        var match = fitment.AssertionKind == FitmentAssertionKind.Exact
            ? FitmentMatchKind.Exact
            : FitmentMatchKind.Compatible;

        return new FitmentCheckResult(
            match,
            true,
            fitment.Confidence,
            fitment.SourceName,
            fitment.SourceRecordId,
            fitment.Provenance,
            fitment.ValidFromUtc,
            fitment.ValidToUtc);
    }

    public async Task<ProductIdentifierWriteResult> UpsertProductIdentifierAsync(
        ProductIdentifierUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateIdentifier(request);
        if (validationError != null)
        {
            return new ProductIdentifierWriteResult(
                FitmentWriteOutcome.InvalidRequest,
                Message: validationError);
        }

        var schemeAuthority = request.SchemeAuthority.Trim().ToUpperInvariant();
        var value = request.Value.Trim();
        var normalizedValue = NormalizePartIdentifier(value);
        var sourceName = request.SourceName.Trim().ToUpperInvariant();
        var sourceRecordId = request.SourceRecordId.Trim();
        var provenance = request.Provenance.Trim();

        var existing = await _context.Set<ProductIdentifier>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item =>
                    (item.ProductId == request.ProductId &&
                     item.Kind == request.Kind &&
                     item.SchemeAuthority == schemeAuthority &&
                     item.NormalizedValue == normalizedValue) ||
                    (item.SourceName == sourceName &&
                     item.SourceRecordId == sourceRecordId),
                cancellationToken);
        if (existing != null)
        {
            return IdentifierMatches(existing, request, schemeAuthority, normalizedValue, sourceName, sourceRecordId, provenance)
                ? new ProductIdentifierWriteResult(FitmentWriteOutcome.Replayed, existing)
                : new ProductIdentifierWriteResult(
                    FitmentWriteOutcome.Conflict,
                    existing,
                    "Identifier natural key or source record is linked to different data.");
        }

        if (!await _context.Products.AsNoTracking().AnyAsync(
                product => product.Id == request.ProductId,
                cancellationToken))
        {
            return new ProductIdentifierWriteResult(
                FitmentWriteOutcome.NotFound,
                Message: "Product not found.");
        }

        var identifier = new ProductIdentifier
        {
            ProductId = request.ProductId,
            Kind = request.Kind,
            SchemeAuthority = schemeAuthority,
            Value = value,
            NormalizedValue = normalizedValue,
            IsVerified = request.IsVerified,
            SourceKind = request.SourceKind,
            SourceName = sourceName,
            SourceRecordId = sourceRecordId,
            Provenance = provenance,
            ValidFromUtc = request.ValidFromUtc,
            ValidToUtc = request.ValidToUtc
        };
        _context.Set<ProductIdentifier>().Add(identifier);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return new ProductIdentifierWriteResult(FitmentWriteOutcome.Created, identifier);
        }
        catch (DbUpdateException)
        {
            _context.Entry(identifier).State = EntityState.Detached;
            existing = await _context.Set<ProductIdentifier>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item =>
                        (item.ProductId == request.ProductId &&
                         item.Kind == request.Kind &&
                         item.SchemeAuthority == schemeAuthority &&
                         item.NormalizedValue == normalizedValue) ||
                        (item.SourceName == sourceName &&
                         item.SourceRecordId == sourceRecordId),
                    cancellationToken);
            if (existing != null && IdentifierMatches(
                    existing,
                    request,
                    schemeAuthority,
                    normalizedValue,
                    sourceName,
                    sourceRecordId,
                    provenance))
            {
                return new ProductIdentifierWriteResult(FitmentWriteOutcome.Replayed, existing);
            }

            return new ProductIdentifierWriteResult(
                FitmentWriteOutcome.Conflict,
                existing,
                "Identifier uniqueness conflict.");
        }
    }

    public async Task<FitmentReadPage> QueryAsync(
        FitmentReadQuery query,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateReadQuery(query);
        if (validationError != null)
        {
            return new FitmentReadPage(
                Array.Empty<FitmentReadItem>(),
                query.Offset,
                query.Limit,
                false,
                validationError);
        }

        var source = _context.Set<ProductFitment>()
            .AsNoTracking()
            .Where(item =>
                item.ValidFromUtc <= query.EffectiveAtUtc &&
                (item.ValidToUtc == null || query.EffectiveAtUtc < item.ValidToUtc));

        if (query.ProductId.HasValue)
        {
            source = source.Where(item => item.ProductId == query.ProductId.Value);
        }

        if (query.VehicleId.HasValue)
        {
            source = source.Where(item => item.VehicleId == query.VehicleId.Value);
        }

        if (query.VerifiedOnly)
        {
            source = source.Where(item =>
                item.IsVerified && item.SourceKind != FitmentSourceKind.UnverifiedImport);
        }

        var candidates = await source
            .OrderBy(item => item.Id)
            .Skip(query.Offset)
            .Take(query.Limit + 1)
            .Select(item => new FitmentReadItem(
                item.Id,
                item.ProductId,
                item.Product.Name,
                item.VehicleId,
                item.Vehicle.DisplayName,
                item.Vehicle.Engine.Generation.Model.Make.Name,
                item.Vehicle.Engine.Generation.Model.Name,
                item.Vehicle.Engine.Generation.Name,
                item.Vehicle.Engine.Name,
                item.AssertionKind,
                item.Confidence,
                item.IsVerified,
                item.SourceKind,
                item.SourceName,
                item.SourceRecordId,
                item.Provenance,
                item.ValidFromUtc,
                item.ValidToUtc))
            .ToListAsync(cancellationToken);

        var hasMore = candidates.Count > query.Limit;
        if (hasMore)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        return new FitmentReadPage(candidates, query.Offset, query.Limit, hasMore);
    }

    private static VehicleTreeWriteResult Conflict(string message)
    {
        return new VehicleTreeWriteResult(FitmentWriteOutcome.Conflict, Message: message);
    }

    private static FitmentCheckResult Unknown(string message)
    {
        return new FitmentCheckResult(FitmentMatchKind.Unknown, false, Message: message);
    }

    private static ProductFitmentWriteResult ResolveExisting(
        ProductFitment existing,
        ProductFitmentUpsertRequest request,
        string conflictMessage)
    {
        return FitmentMatches(existing, request)
            ? new ProductFitmentWriteResult(FitmentWriteOutcome.Replayed, existing)
            : new ProductFitmentWriteResult(
                FitmentWriteOutcome.Conflict,
                existing,
                conflictMessage);
    }

    private static bool FitmentMatches(
        ProductFitment existing,
        ProductFitmentUpsertRequest request)
    {
        return existing.ProductId == request.ProductId &&
               existing.VehicleId == request.VehicleId &&
               existing.AssertionKind == request.AssertionKind &&
               existing.Confidence == request.Confidence &&
               existing.IsVerified == request.IsVerified &&
               existing.SourceKind == request.SourceKind &&
               SameText(existing.SourceName, request.SourceName) &&
               SameText(existing.SourceRecordId, request.SourceRecordId) &&
               string.Equals(existing.Provenance, request.Provenance, StringComparison.Ordinal) &&
               existing.ValidFromUtc == request.ValidFromUtc &&
               existing.ValidToUtc == request.ValidToUtc;
    }

    private static bool IdentifierMatches(
        ProductIdentifier existing,
        ProductIdentifierUpsertRequest request,
        string schemeAuthority,
        string normalizedValue,
        string sourceName,
        string sourceRecordId,
        string provenance)
    {
        return existing.ProductId == request.ProductId &&
               existing.Kind == request.Kind &&
               existing.SchemeAuthority == schemeAuthority &&
               existing.NormalizedValue == normalizedValue &&
               existing.IsVerified == request.IsVerified &&
               existing.SourceKind == request.SourceKind &&
               existing.SourceName == sourceName &&
               existing.SourceRecordId == sourceRecordId &&
               existing.Provenance == provenance &&
               existing.ValidFromUtc == request.ValidFromUtc &&
               existing.ValidToUtc == request.ValidToUtc;
    }

    private void DetachAddedGraph(Vehicle vehicle)
    {
        object[] graph =
        [
            vehicle,
            vehicle.Engine,
            vehicle.Engine.Generation,
            vehicle.Engine.Generation.Model,
            vehicle.Engine.Generation.Model.Make
        ];

        foreach (var entity in graph)
        {
            var entry = _context.Entry(entity);
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static bool EngineMatches(
        VehicleEngine engine,
        VehicleTreeUpsertRequest request)
    {
        return SameText(engine.Name, request.EngineName) &&
               SameOptional(engine.EngineCode, request.EngineCode) &&
               SameOptional(engine.FuelType, request.FuelType) &&
               engine.DisplacementCc == request.DisplacementCc &&
               engine.PowerKw == request.PowerKw;
    }

    private static bool GenerationMatches(
        VehicleGeneration generation,
        VehicleTreeUpsertRequest request)
    {
        return SameText(generation.Name, request.GenerationName) &&
               generation.ProductionStartYear == request.GenerationStartYear &&
               generation.ProductionEndYear == request.GenerationEndYear;
    }

    private static bool VehicleMatches(
        Vehicle vehicle,
        VehicleTreeUpsertRequest request)
    {
        return SameText(vehicle.DisplayName, request.VehicleName) &&
               SameOptional(vehicle.BodyStyle, request.BodyStyle) &&
               SameOptional(vehicle.Transmission, request.Transmission) &&
               SameOptional(vehicle.DriveType, request.DriveType) &&
               SameOptional(vehicle.Market, request.Market) &&
               vehicle.ProductionStartYear == request.VehicleStartYear &&
               vehicle.ProductionEndYear == request.VehicleEndYear;
    }

    private static ProductFitmentUpsertRequest Normalize(ProductFitmentUpsertRequest request)
    {
        return request with
        {
            SourceName = request.SourceName.Trim().ToUpperInvariant(),
            SourceRecordId = request.SourceRecordId.Trim(),
            Provenance = request.Provenance.Trim(),
            IdempotencyKey = request.IdempotencyKey.Trim()
        };
    }

    private Task<Vehicle?> FindVehicleAsync(
        string makeKey,
        string modelKey,
        string generationKey,
        string engineKey,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        return _context.Set<Vehicle>()
            .AsNoTracking()
            .Include(item => item.Engine)
                .ThenInclude(item => item.Generation)
                .ThenInclude(item => item.Model)
                .ThenInclude(item => item.Make)
            .SingleOrDefaultAsync(
                item => item.CanonicalKey == vehicleKey &&
                        item.Engine.CanonicalKey == engineKey &&
                        item.Engine.Generation.CanonicalKey == generationKey &&
                        item.Engine.Generation.Model.CanonicalKey == modelKey &&
                        item.Engine.Generation.Model.Make.CanonicalKey == makeKey,
                cancellationToken);
    }

    private static string? ValidateVehicleTree(VehicleTreeUpsertRequest request)
    {
        if (request == null)
        {
            return "Araç ağacı isteği gereklidir.";
        }

        var requiredFields = new (string? Value, int Limit, string Label)[]
        {
            (request.MakeKey, 80, "Marka anahtarı"),
            (request.MakeName, 200, "Marka adı"),
            (request.ModelKey, 80, "Model anahtarı"),
            (request.ModelName, 200, "Model adı"),
            (request.GenerationKey, 80, "Nesil anahtarı"),
            (request.GenerationName, 200, "Nesil adı"),
            (request.EngineKey, 80, "Motor anahtarı"),
            (request.EngineName, 200, "Motor adı"),
            (request.VehicleKey, 120, "Araç anahtarı"),
            (request.VehicleName, 300, "Araç adı")
        };

        foreach (var field in requiredFields)
        {
            if (string.IsNullOrWhiteSpace(field.Value) || field.Value.Trim().Length > field.Limit)
            {
                return $"{field.Label} 1 ile {field.Limit} karakter arasında olmalıdır.";
            }
        }

        var optionalFields = new (string? Value, int Limit, string Label)[]
        {
            (request.EngineCode, 80, "Motor kodu"),
            (request.FuelType, 40, "Yakıt türü"),
            (request.BodyStyle, 80, "Kasa türü"),
            (request.Transmission, 80, "Şanzıman"),
            (request.DriveType, 40, "Çekiş türü"),
            (request.Market, 40, "Pazar")
        };

        foreach (var field in optionalFields)
        {
            if (field.Value?.Trim().Length > field.Limit)
            {
                return $"{field.Label} en fazla {field.Limit} karakter olabilir.";
            }
        }

        if (!ValidYearRange(request.GenerationStartYear, request.GenerationEndYear) ||
            !ValidYearRange(request.VehicleStartYear, request.VehicleEndYear))
        {
            return "Üretim yılları 1886 ile 2200 arasında ve başlangıç bitişten önce olmalıdır.";
        }

        if (request.DisplacementCc is <= 0 or > 20_000)
        {
            return "Motor hacmi 1 ile 20000 cc arasında olmalıdır.";
        }

        if (request.PowerKw is <= 0 or > 2_000)
        {
            return "Motor gücü 0 ile 2000 kW arasında olmalıdır.";
        }

        return null;
    }

    private static string? ValidateFitment(ProductFitmentUpsertRequest request)
    {
        if (request.ProductId <= 0 || request.VehicleId <= 0)
        {
            return "Ürün ve araç kimlikleri pozitif olmalıdır.";
        }

        if (!Enum.IsDefined(request.AssertionKind))
        {
            return "Fitment beyan türü geçersiz.";
        }

        if (request.Confidence is < 0 or > 1)
        {
            return "Güven değeri 0 ile 1 arasında olmalıdır.";
        }

        if (!Enum.IsDefined(request.SourceKind))
        {
            return "Fitment kaynak türü geçersiz.";
        }

        if (request.IsVerified && request.SourceKind == FitmentSourceKind.UnverifiedImport)
        {
            return "Doğrulanmamış içe aktarma kaynağı verified olarak işaretlenemez.";
        }

        if (!RequiredWithin(request.SourceName, 120) ||
            !RequiredWithin(request.SourceRecordId, 200) ||
            !RequiredWithin(request.Provenance, 1000) ||
            !RequiredWithin(request.IdempotencyKey, 100))
        {
            return "Kaynak adı, kaynak kayıt kimliği, provenance ve idempotensi anahtarı zorunlu ve sınırları içinde olmalıdır.";
        }

        if (request.ValidFromUtc.Kind != DateTimeKind.Utc ||
            request.ValidToUtc is { Kind: not DateTimeKind.Utc })
        {
            return "Fitment geçerlilik tarihleri UTC olmalıdır.";
        }

        if (request.ValidToUtc.HasValue && request.ValidToUtc <= request.ValidFromUtc)
        {
            return "Fitment geçerlilik bitişi başlangıçtan sonra olmalıdır.";
        }

        return null;
    }

    private static string? ValidateReadQuery(FitmentReadQuery query)
    {
        if (!query.ProductId.HasValue && !query.VehicleId.HasValue)
        {
            return "Sınırlı sorgu için ürün veya araç filtresi gereklidir.";
        }

        if (query.ProductId is <= 0 || query.VehicleId is <= 0)
        {
            return "Ürün ve araç filtreleri pozitif olmalıdır.";
        }

        if (query.EffectiveAtUtc.Kind != DateTimeKind.Utc)
        {
            return "Sorgu tarihi UTC olmalıdır.";
        }

        if (query.Offset is < 0 or > MaxReadOffset)
        {
            return $"Offset 0 ile {MaxReadOffset} arasında olmalıdır.";
        }

        if (query.Limit is < 1 or > MaxReadLimit)
        {
            return $"Limit 1 ile {MaxReadLimit} arasında olmalıdır.";
        }

        return null;
    }

    private static string? ValidateIdentifier(ProductIdentifierUpsertRequest request)
    {
        if (request.ProductId <= 0 || !Enum.IsDefined(request.Kind))
        {
            return "Product and identifier kind are required.";
        }

        if (!Enum.IsDefined(request.SourceKind) ||
            request.IsVerified && request.SourceKind == FitmentSourceKind.UnverifiedImport)
        {
            return "Verified identifiers require a trusted source kind.";
        }

        if (!RequiredWithin(request.SchemeAuthority, 120) ||
            !RequiredWithin(request.Value, 160) ||
            !RequiredWithin(request.SourceName, 120) ||
            !RequiredWithin(request.SourceRecordId, 200) ||
            !RequiredWithin(request.Provenance, 1000) ||
            NormalizePartIdentifier(request.Value).Length is < 1 or > 160)
        {
            return "Identifier and source fields are required and must stay within their bounds.";
        }

        if (request.ValidFromUtc.Kind != DateTimeKind.Utc ||
            request.ValidToUtc is { Kind: not DateTimeKind.Utc } ||
            request.ValidToUtc.HasValue && request.ValidToUtc <= request.ValidFromUtc)
        {
            return "Identifier validity timestamps must be a valid UTC range.";
        }

        return null;
    }

    public static string NormalizePartIdentifier(string value) =>
        new(value
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray());

    private static bool RequiredWithin(string? value, int limit)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= limit;
    }

    private static bool ValidYearRange(int? start, int? end)
    {
        if (start is < 1886 or > 2200 || end is < 1886 or > 2200)
        {
            return false;
        }

        return !start.HasValue || !end.HasValue || start <= end;
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static string? CleanOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool SameText(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameOptional(string? left, string? right)
    {
        return SameText(CleanOptional(left), CleanOptional(right));
    }
}
