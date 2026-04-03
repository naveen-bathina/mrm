namespace Mrm.Infrastructure.Entities;

public enum MovieStatus
{
    Draft,
    Registered,
    InProduction,
    PostProduction,
    Released,
}

public class Movie
{
    public Guid Id { get; set; }
    public Guid StudioId { get; set; }
    public string OriginalTitle { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public MovieStatus Status { get; set; } = MovieStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
