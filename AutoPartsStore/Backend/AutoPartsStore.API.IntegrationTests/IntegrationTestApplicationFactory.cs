using AutoPartsStore.API.Data;
using AutoPartsStore.API.Models;
using AutoPartsStore.API.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoPartsStore.API.IntegrationTests;

public sealed class IntegrationTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IntegrationTestApplicationFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTesting");
        builder.UseSetting("Jwt:Key", "integration-test-only-signing-key-with-more-than-32-characters");
        builder.UseSetting("Jwt:Issuer", "AutoPartsStore.IntegrationTests");
        builder.UseSetting("Jwt:Audience", "AutoPartsStore.IntegrationTests.Users");
        builder.UseSetting("Cors:AllowedOrigins:0", "https://frontend.integration.test");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "integration-test-only-signing-key-with-more-than-32-characters",
                ["Jwt:Issuer"] = "AutoPartsStore.IntegrationTests",
                ["Jwt:Audience"] = "AutoPartsStore.IntegrationTests.Users",
                ["ConnectionStrings:DefaultConnection"] = "unused-by-integration-test",
                ["Cors:AllowedOrigins:0"] = "https://frontend.integration.test",
                ["PublicSite:Origin"] = "https://shop.integration.test",
                ["OutboxWorker:Enabled"] = "false",
                ["InventoryReservationExpiry:Enabled"] = "false",
                ["AdminAuditIntent:Enabled"] = "false"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AutoPartsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AutoPartsDbContext>>();
            services.RemoveAll<AutoPartsDbContext>();
            services.RemoveAll<IDatabaseInitializer>();
            services.AddSingleton(_connection);
            services.AddDbContext<AutoPartsDbContext>(options => options.UseSqlite(_connection));
            services.AddScoped<IDatabaseInitializer, EnsureCreatedDatabaseInitializer>();
            services.AddControllers().AddApplicationPart(typeof(IntegrationFailureController).Assembly);
        });
    }

    public HttpClient CreateHttpsClient(bool allowAutoRedirect = false) =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = allowAutoRedirect
        });

    public async Task SeedAsync(Func<AutoPartsDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        await seed(scope.ServiceProvider.GetRequiredService<AutoPartsDbContext>());
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AutoPartsDbContext>();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task<string> CreateTokenAsync(string role)
    {
        string token = string.Empty;
        await SeedAsync(async context =>
        {
            var user = new User
            {
                FullName = $"{role} Integration User",
                Email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@integration.test",
                Password = BCrypt.Net.BCrypt.HashPassword("integration-test-only-password"),
                Role = role,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            using var scope = Services.CreateScope();
            token = scope.ServiceProvider.GetRequiredService<JwtService>().GenerateToken(user);
        });
        return token;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }

    private sealed class EnsureCreatedDatabaseInitializer : IDatabaseInitializer
    {
        private readonly AutoPartsDbContext _context;

        public EnsureCreatedDatabaseInitializer(AutoPartsDbContext context)
        {
            _context = context;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            _context.Database.EnsureCreatedAsync(cancellationToken);
    }
}
