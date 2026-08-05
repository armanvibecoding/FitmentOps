using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AutoPartsStore.API.Models;

public static class LegalDocumentTypes
{
    public const string PreliminaryInformation = "PreliminaryInformation";
    public const string DistanceSalesAgreement = "DistanceSalesAgreement";
    public const string PrivacyNotice = "PrivacyNotice";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        PreliminaryInformation,
        DistanceSalesAgreement,
        PrivacyNotice
    ]);
}

public static class LegalDocumentStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Retired = "Retired";
}

public sealed class LegalDocumentVersion
{
    private LegalDocumentVersion()
    {
    }

    public long Id { get; private set; }

    [Required, StringLength(50)]
    public string DocumentType { get; private set; } = string.Empty;

    [Required, StringLength(40)]
    public string Version { get; private set; } = string.Empty;

    [Required, StringLength(200)]
    public string Title { get; private set; } = string.Empty;

    [Required, StringLength(100_000)]
    public string Content { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    public string ContentSha256 { get; private set; } = string.Empty;

    [Required, StringLength(20)]
    public string Status { get; private set; } = LegalDocumentStatuses.Draft;

    public int CreatedByUserId { get; private set; }
    public int? PublishedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public DateTime? RetiredAtUtc { get; private set; }

    [ConcurrencyCheck]
    public Guid ConcurrencyToken { get; private set; }

    [JsonIgnore]
    public ICollection<LegalAcceptance> Acceptances { get; private set; } = new List<LegalAcceptance>();

    public static LegalDocumentVersion CreateDraft(
        string documentType,
        string version,
        string title,
        string content,
        int actorUserId,
        DateTime nowUtc)
    {
        var normalizedType = LegalDocumentTypes.All.SingleOrDefault(candidate =>
            string.Equals(candidate, documentType?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException("Unsupported legal document type.", nameof(documentType));
        var normalizedVersion = version?.Trim() ?? string.Empty;
        var normalizedTitle = title?.Trim() ?? string.Empty;
        var normalizedContent = CanonicalizeContent(content);
        if (normalizedVersion is { Length: < 1 or > 40 } ||
            normalizedTitle is { Length: < 1 or > 200 } ||
            normalizedContent is { Length: < 1 or > 100_000 } ||
            actorUserId <= 0)
        {
            throw new ArgumentException("Legal document draft is invalid.");
        }

        return new LegalDocumentVersion
        {
            DocumentType = normalizedType,
            Version = normalizedVersion,
            Title = normalizedTitle,
            Content = normalizedContent,
            ContentSha256 = ComputeContentHash(normalizedContent),
            Status = LegalDocumentStatuses.Draft,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            ConcurrencyToken = Guid.NewGuid()
        };
    }

    public void Publish(int actorUserId, DateTime nowUtc)
    {
        if (actorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actorUserId));
        }

        if (Status == LegalDocumentStatuses.Retired)
        {
            throw new InvalidOperationException("A retired legal document cannot be republished.");
        }

        Status = LegalDocumentStatuses.Published;
        PublishedByUserId = actorUserId;
        PublishedAtUtc ??= DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        RetiredAtUtc = null;
        ConcurrencyToken = Guid.NewGuid();
    }

    public void Retire(DateTime nowUtc)
    {
        if (Status == LegalDocumentStatuses.Retired) return;
        Status = LegalDocumentStatuses.Retired;
        RetiredAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        ConcurrencyToken = Guid.NewGuid();
    }

    public static string CanonicalizeContent(string? content) =>
        (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    public static string ComputeContentHash(string canonicalContent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalContent)))
            .ToLowerInvariant();
}

public sealed class LegalAcceptance
{
    private LegalAcceptance()
    {
    }

    public long Id { get; private set; }
    public int OrderId { get; private set; }

    [JsonIgnore]
    public Order Order { get; private set; } = null!;

    public long LegalDocumentVersionId { get; private set; }

    [JsonIgnore]
    public LegalDocumentVersion LegalDocumentVersion { get; private set; } = null!;

    public int? UserId { get; private set; }
    public DateTime AcceptedAtUtc { get; private set; }

    [Required, StringLength(50)]
    public string DocumentTypeSnapshot { get; private set; } = string.Empty;

    [Required, StringLength(40)]
    public string VersionSnapshot { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    public string ContentSha256Snapshot { get; private set; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 64)]
    [JsonIgnore]
    public string CheckoutReferenceSha256 { get; private set; } = string.Empty;

    public static LegalAcceptance Create(
        LegalDocumentVersion document,
        int? userId,
        string checkoutIdempotencyKey,
        DateTime acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        var evidence = string.Join(
            '|',
            checkoutIdempotencyKey,
            document.Id,
            document.DocumentType,
            document.Version,
            document.ContentSha256);
        return new LegalAcceptance
        {
            LegalDocumentVersionId = document.Id,
            UserId = userId,
            AcceptedAtUtc = DateTime.SpecifyKind(acceptedAtUtc, DateTimeKind.Utc),
            DocumentTypeSnapshot = document.DocumentType,
            VersionSnapshot = document.Version,
            ContentSha256Snapshot = document.ContentSha256,
            CheckoutReferenceSha256 = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(evidence)))
                .ToLowerInvariant()
        };
    }
}
