using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mrm.Infrastructure.Entities;

namespace Mrm.Infrastructure.Configurations;

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.OriginalTitle).IsRequired().HasMaxLength(500);
        builder.Property(m => m.NormalizedTitle).IsRequired().HasMaxLength(500);
        builder.Property(m => m.Status).HasConversion<string>();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();

        // Unique constraint enforced at DB level to back the title conflict checker
        builder.HasIndex(m => new { m.NormalizedTitle, m.ReleaseYear })
               .IsUnique()
               .HasDatabaseName("ix_movies_normalized_title_year");
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).IsRequired().HasMaxLength(320);
        builder.Property(u => u.Role).HasConversion<string>();
    }
}

public class TerritoryConfiguration : IEntityTypeConfiguration<Territory>
{
    public void Configure(EntityTypeBuilder<Territory> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Code).IsRequired().HasMaxLength(10);
        builder.HasIndex(t => t.Code).IsUnique().HasDatabaseName("ix_territories_code");
    }
}

public class MovieReleaseConfiguration : IEntityTypeConfiguration<MovieRelease>
{
    public void Configure(EntityTypeBuilder<MovieRelease> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasOne(r => r.Movie).WithMany().HasForeignKey(r => r.MovieId);
        builder.HasOne(r => r.Territory).WithMany().HasForeignKey(r => r.TerritoryId);
        // One release date per movie per territory
        builder.HasIndex(r => new { r.MovieId, r.TerritoryId })
               .IsUnique()
               .HasDatabaseName("ix_movie_releases_movie_territory");
    }
}
