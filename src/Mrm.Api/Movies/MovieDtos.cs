using Mrm.Infrastructure.Entities;

namespace Mrm.Api.Movies;

public record CreateMovieRequest(
    string OriginalTitle,
    int ReleaseYear
);

public record UpdateMovieRequest(
    string OriginalTitle,
    int ReleaseYear
);

public record ValidateMovieTitleRequest(
    string OriginalTitle,
    int ReleaseYear,
    Guid? ExcludeMovieId = null
);

public record TransitionMovieRequest(
    string TargetStatus
);

public record MovieResponse(
    Guid Id,
    Guid StudioId,
    string OriginalTitle,
    string NormalizedTitle,
    int ReleaseYear,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public static class MovieMappings
{
    public static MovieResponse ToResponse(this Movie m) => new(
        m.Id, m.StudioId, m.OriginalTitle, m.NormalizedTitle,
        m.ReleaseYear, m.Status.ToString(), m.CreatedAt, m.UpdatedAt
    );
}
