// ============================================================================
// Infrastructure Stubs — EF Core entities and DbContext
// In production, these live in the shared data access layer.
// ============================================================================

using Microsoft.EntityFrameworkCore;
using MovieLifecycle.Types;

namespace MovieLifecycle.Infrastructure;

// WHY: Stub DbContext for the lifecycle module. In production, this would be the
// shared MovieDbContext from the main application with full configuration.
// Constructor-injected DbContextOptions enables Testcontainers for integration tests.
public class MovieDbContext : DbContext
{
    public MovieDbContext(DbContextOptions<MovieDbContext> options) : base(options) { }

    public DbSet<MovieEntity> Movies => Set<MovieEntity>();
    public DbSet<TerritoryRelease> TerritoryReleases => Set<TerritoryRelease>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
}

public class MovieEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public MovieStatus Status { get; set; }
    public Guid StudioId { get; set; }
}

public class TerritoryRelease
{
    public Guid Id { get; set; }
    public Guid MovieId { get; set; }
    public Guid TerritoryId { get; set; }
    public DateOnly ReleaseDate { get; set; }
}

public class AuditLogEntry
{
    public Guid Id { get; set; }
    public Guid MovieId { get; set; }
    public string FromStatus { get; set; } = string.Empty;
    public string ToStatus { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public int ConflictCount { get; set; }
    public string? OverrideReason { get; set; }
    public string? OverrideBy { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

// WHY: DTO for raw SQL query result in PersonScheduleConflictChecker.
// EF Core requires a concrete type to materialize SqlQueryRaw results.
public class ScheduleConflictDto
{
    public string PersonName { get; set; } = string.Empty;
    public Guid ConflictingMovieId { get; set; }
}
