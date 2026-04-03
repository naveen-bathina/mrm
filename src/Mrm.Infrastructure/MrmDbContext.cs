using Microsoft.EntityFrameworkCore;
using Mrm.Infrastructure.Configurations;
using Mrm.Infrastructure.Entities;

namespace Mrm.Infrastructure;

public class MrmDbContext(DbContextOptions<MrmDbContext> options) : DbContext(options)
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new MovieConfiguration());
        modelBuilder.ApplyConfiguration(new UserConfiguration());
    }
}
