using Microsoft.EntityFrameworkCore;
using Mrm.Infrastructure;

namespace Mrm.Tests.Helpers;

public static class DbExtensions
{
    /// <summary>Ensures DB schema is up-to-date before each test.</summary>
    public static MrmDbContext MigrateAndGet(this MrmWebFactory factory)
    {
        var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        return db;
    }
}
