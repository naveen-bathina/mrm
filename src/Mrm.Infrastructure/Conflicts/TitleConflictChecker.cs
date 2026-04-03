using Microsoft.EntityFrameworkCore;

namespace Mrm.Infrastructure.Conflicts;

public class TitleConflictChecker(MrmDbContext db)
{
    /// <summary>
    /// Checks whether a normalized title + release year already exists.
    /// Pass <paramref name="excludeMovieId"/> when editing an existing movie
    /// so the movie doesn't conflict with itself.
    /// </summary>
    public async Task<ConflictEnvelope> CheckAsync(
        string normalizedTitle,
        int releaseYear,
        Guid? excludeMovieId = null,
        CancellationToken ct = default)
    {
        var query = db.Movies.AsNoTracking()
            .Where(m => m.NormalizedTitle == normalizedTitle && m.ReleaseYear == releaseYear);

        if (excludeMovieId.HasValue)
            query = query.Where(m => m.Id != excludeMovieId.Value);

        var conflict = await query
            .Select(m => new { m.OriginalTitle, m.ReleaseYear })
            .FirstOrDefaultAsync(ct);

        if (conflict is null)
            return ConflictEnvelope.None();

        return ConflictEnvelope.Hard(new ConflictItem(
            Type: "TitleConflict",
            Severity: "hard",
            Detail: $"'{conflict.OriginalTitle}' ({conflict.ReleaseYear}) has the same normalized title."
        ));
    }
}
