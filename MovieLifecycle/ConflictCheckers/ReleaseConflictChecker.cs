// ============================================================================
// SECTION 1 — Concrete Implementation: ReleaseConflictChecker
// ============================================================================

using Microsoft.EntityFrameworkCore;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Types;

namespace MovieLifecycle.ConflictCheckers;

// WHY: Release date conflicts are soft warnings — they don't block the transition
// but inform the studio of scheduling overlap in the same territory on the same date.
// Cross-studio query via IgnoreQueryFilters ensures one studio sees conflicts with
// another studio's releases without accessing their full data.
public sealed class ReleaseConflictChecker : IConflictChecker
{
    private readonly MovieDbContext _dbContext;

    public ReleaseConflictChecker(MovieDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ConflictResult?> CheckAsync(TransitionContext context, CancellationToken ct)
    {
        var movieId = context.Command.MovieId;

        // WHY: Check if any of this movie's territory releases overlap with
        // another movie's releases in the same territory on the same date.
        var hasOverlap = await _dbContext.TerritoryReleases
            .IgnoreQueryFilters()
            .Where(tr => tr.MovieId == movieId)
            .AnyAsync(tr => _dbContext.TerritoryReleases
                .IgnoreQueryFilters()
                .Any(other =>
                    other.MovieId != movieId &&
                    other.TerritoryId == tr.TerritoryId &&
                    other.ReleaseDate == tr.ReleaseDate),
                ct);

        return hasOverlap
            ? new ConflictResult(
                nameof(ReleaseConflictChecker),
                ConflictSeverity.Warning,
                "One or more release dates overlap with another movie in the same territory.")
            : null;
    }
}
