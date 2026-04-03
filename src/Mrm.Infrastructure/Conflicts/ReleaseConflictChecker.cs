using Microsoft.EntityFrameworkCore;

namespace Mrm.Infrastructure.Conflicts;

public class ReleaseConflictChecker(MrmDbContext db)
{
    /// <summary>
    /// Checks for same-territory/same-date clashes across ALL studios (global scope).
    /// Returns a SOFT warning — callers may still save.
    /// Pass <paramref name="excludeMovieId"/> when editing to avoid self-conflict.
    /// </summary>
    public async Task<ConflictEnvelope> CheckAsync(
        Guid territoryId,
        DateOnly releaseDate,
        Guid? excludeMovieId = null,
        CancellationToken ct = default)
    {
        var query = db.MovieReleases
            .AsNoTracking()
            .Include(r => r.Movie)
            .Include(r => r.Territory)
            .Where(r => r.TerritoryId == territoryId
                     && r.ReleaseDate == releaseDate
                     && r.Territory.IsActive);

        if (excludeMovieId.HasValue)
            query = query.Where(r => r.MovieId != excludeMovieId.Value);

        var clashes = await query
            .Select(r => new { r.Movie.OriginalTitle, r.Territory.Name })
            .ToListAsync(ct);

        if (clashes.Count == 0)
            return ConflictEnvelope.None();

        var items = clashes.Select(c => new ConflictItem(
            Type: "ReleaseDateConflict",
            Severity: "soft",
            Detail: $"Another movie is already scheduled in '{c.Name}' on {releaseDate:yyyy-MM-dd}."
        )).ToArray();

        return ConflictEnvelope.Soft(items);
    }
}
