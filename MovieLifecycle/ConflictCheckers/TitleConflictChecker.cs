// ============================================================================
// SECTION 1 — Concrete Implementation: TitleConflictChecker
// ============================================================================

using Microsoft.EntityFrameworkCore;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Types;

namespace MovieLifecycle.ConflictCheckers;

// WHY: Constructor-injected DbContext ensures the checker participates in the
// request's unit of work. IgnoreQueryFilters() bypasses multi-tenant studio
// scoping because title conflicts are cross-studio — "The Batman" registered
// by Studio A blocks Studio B from registering the same title in the same year.
public sealed class TitleConflictChecker : IConflictChecker
{
    private readonly MovieDbContext _dbContext;

    public TitleConflictChecker(MovieDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ConflictResult?> CheckAsync(TransitionContext context, CancellationToken ct)
    {
        var movie = await _dbContext.Movies
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == context.Command.MovieId, ct);

        if (movie is null) return null;

        // WHY: IgnoreQueryFilters bypasses the global studio-scoping filter.
        // Title uniqueness must be enforced across ALL studios, not just the
        // requesting studio's movies. This is a cross-studio business rule.
        var conflict = await _dbContext.Movies
            .IgnoreQueryFilters()
            .AnyAsync(m =>
                m.Id != movie.Id &&
                m.NormalizedTitle == movie.NormalizedTitle &&
                m.ReleaseYear == movie.ReleaseYear &&
                m.Status != MovieStatus.Archived,
                ct);

        return conflict
            ? new ConflictResult(
                nameof(TitleConflictChecker),
                ConflictSeverity.Hard,
                $"Title '{movie.Title}' conflicts with an existing registration in {movie.ReleaseYear}.")
            : null;
    }
}
