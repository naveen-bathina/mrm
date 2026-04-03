using Microsoft.EntityFrameworkCore;
using Mrm.Infrastructure.Configurations;
using Mrm.Infrastructure.Entities;

namespace Mrm.Infrastructure;

public class MrmDbContext(DbContextOptions<MrmDbContext> options) : DbContext(options)
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Territory> Territories => Set<Territory>();
    public DbSet<MovieRelease> MovieReleases => Set<MovieRelease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new MovieConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new TerritoryConfiguration());
        modelBuilder.ApplyConfiguration(new MovieReleaseConfiguration());
    }
}
