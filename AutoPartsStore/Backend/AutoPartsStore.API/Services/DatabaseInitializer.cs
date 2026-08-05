using AutoPartsStore.API.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsStore.API.Services;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class EfCoreDatabaseInitializer : IDatabaseInitializer
{
    private readonly AutoPartsDbContext _context;

    public EfCoreDatabaseInitializer(AutoPartsDbContext context)
    {
        _context = context;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _context.Database.MigrateAsync(cancellationToken);
}
