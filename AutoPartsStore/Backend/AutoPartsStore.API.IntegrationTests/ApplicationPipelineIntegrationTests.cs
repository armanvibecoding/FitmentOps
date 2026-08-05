using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AutoPartsStore.API.IntegrationTests;

public sealed class ApplicationPipelineIntegrationTests : IClassFixture<IntegrationTestApplicationFactory>
{
    private readonly IntegrationTestApplicationFactory _factory;

    public ApplicationPipelineIntegrationTests(IntegrationTestApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LiveHealth_ReturnsSecurityHeadersAndCorrelationId()
    {
        using var client = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-ID", "integration-correlation-123");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("integration-correlation-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Readiness_FailsClosedWithoutRequiredLegalDocumentsAndProviders()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousAdminRequest_IsUnauthorized()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/Admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SupportRole_CannotUseSuperAdminEndpoint()
    {
        using var client = _factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _factory.CreateTokenAsync(AdminAuditRoles.Support));

        using var response = await client.GetAsync("/api/Admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuperAdminRole_CanUseSuperAdminEndpoint()
    {
        using var client = _factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _factory.CreateTokenAsync(AdminAuditRoles.SuperAdmin));

        using var response = await client.GetAsync("/api/Admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UserRole_CanReadOnlyOwnGarageSurface()
    {
        using var client = _factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await _factory.CreateTokenAsync("User"));

        using var response = await client.GetAsync("/api/garage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        Assert.Equal(0, payload.GetArrayLength());
    }

    [Fact]
    public async Task LegalDocuments_StayUnavailableUntilCompletePublishedSetExists()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateHttpsClient();
        using var before = await client.GetAsync("/api/legal/checkout-documents");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        await _factory.SeedAsync(async context =>
        {
            var now = DateTime.UtcNow;
            var actor = new User
            {
                FullName = "Legal Integration Admin",
                Email = $"legal-{Guid.NewGuid():N}@integration.test",
                Password = "integration-test-only-password-hash",
                Role = AdminAuditRoles.SuperAdmin,
                IsActive = true,
                CreatedAt = now
            };
            context.Users.Add(actor);
            await context.SaveChangesAsync();
            foreach (var documentType in new[]
                     {
                         LegalDocumentTypes.PreliminaryInformation,
                         LegalDocumentTypes.DistanceSalesAgreement
                     })
            {
                var document = LegalDocumentVersion.CreateDraft(
                    documentType,
                    "integration-v1",
                    $"{documentType} Integration Title",
                    $"{documentType} integration content",
                    actor.Id,
                    now);
                document.Publish(actor.Id, now);
                context.LegalDocumentVersions.Add(document);
            }
            await context.SaveChangesAsync();
        });

        using var after = await client.GetAsync("/api/legal/checkout-documents");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var documents = await after.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, documents.GetArrayLength());
    }

    [Fact]
    public async Task Cors_AllowsConfiguredOriginAndRejectsHostileOrigin()
    {
        using var client = _factory.CreateHttpsClient();
        using var allowedRequest = CreatePreflight("https://frontend.integration.test");
        using var allowedResponse = await client.SendAsync(allowedRequest);
        Assert.Equal(
            "https://frontend.integration.test",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var hostileRequest = CreatePreflight("https://hostile.example");
        using var hostileResponse = await client.SendAsync(hostileRequest);
        Assert.False(hostileResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ProductionExceptionResponse_DoesNotLeakExceptionDetailsOrStack()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/integration-test/throw");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("integration-sensitive-exception-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Internal server error", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationRateLimit_RejectsEleventhAttempt()
    {
        using var isolatedFactory = new IntegrationTestApplicationFactory();
        using var client = isolatedFactory.CreateHttpsClient();
        var payload = new { email = "missing@integration.test", password = "not-a-secret" };

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/Auth/login", payload);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/Auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static HttpRequestMessage CreatePreflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/Products");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }
}
