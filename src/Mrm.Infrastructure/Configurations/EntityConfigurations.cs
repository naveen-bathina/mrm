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
