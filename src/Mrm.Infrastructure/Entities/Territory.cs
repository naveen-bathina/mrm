namespace Mrm.Infrastructure.Entities;

public class Territory
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty; // e.g. "US", "UK", "IN"
    public bool IsActive { get; set; } = true;
}

public class MovieRelease
{
    public Guid Id { get; set; }
    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
    public Guid TerritoryId { get; set; }
    public Territory Territory { get; set; } = null!;
    public DateOnly ReleaseDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
