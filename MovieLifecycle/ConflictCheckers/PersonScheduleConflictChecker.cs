// ============================================================================
// SECTION 1 — Concrete Implementation: PersonScheduleConflictChecker
// ============================================================================

using Microsoft.EntityFrameworkCore;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Types;

namespace MovieLifecycle.ConflictCheckers;

// WHY: Schedule overlap is a hard conflict — an actor/crew member physically cannot
// be in two productions simultaneously. Uses PostgreSQL date range overlap operator
// (&&) via raw SQL because EF Core doesn't natively support range operators.
// This is the most expensive checker (joins across schedule_blocks, production_roles,
// and persons), so it runs only on the Registered → InProduction edge.
public sealed class PersonScheduleConflictChecker : IConflictChecker
{
    private readonly MovieDbContext _dbContext;

    public PersonScheduleConflictChecker(MovieDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ConflictResult?> CheckAsync(TransitionContext context, CancellationToken ct)
    {
        var movieId = context.Command.MovieId;

        // WHY: Raw SQL for PostgreSQL daterange overlap operator (&&).
        // EF Core's LINQ provider cannot translate range intersection. Using
        // SqlQueryRaw keeps the query in the database (no client-side evaluation)
        // and leverages PostgreSQL's GiST index on daterange columns.
        var conflicts = await _dbContext.Database
            .SqlQueryRaw<ScheduleConflictDto>(
                @"SELECT p.full_name AS ""PersonName"",
                         sb2.movie_id AS ""ConflictingMovieId""
                  FROM schedule_blocks sb1
                  JOIN production_roles pr1 ON pr1.id = sb1.production_role_id
                  JOIN schedule_blocks sb2 ON sb2.id <> sb1.id
                  JOIN production_roles pr2 ON pr2.id = sb2.production_role_id
                  JOIN persons p ON p.id = pr1.person_id
                  WHERE pr1.movie_id = {0}
                    AND pr1.person_id = pr2.person_id
                    AND daterange(sb1.start_date, sb1.end_date) &&
                        daterange(sb2.start_date, sb2.end_date)",
                movieId)
            .ToListAsync(ct);

        return conflicts.Count > 0
            ? new ConflictResult(
                nameof(PersonScheduleConflictChecker),
                ConflictSeverity.Hard,
                $"Schedule overlap detected for {conflicts.Count} person(s): " +
                $"{string.Join(", ", conflicts.Select(c => c.PersonName))}.")
            : null;
    }
}
