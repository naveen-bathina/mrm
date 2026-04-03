using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mrm.Api.Auth;
using Mrm.Infrastructure;
using Mrm.Infrastructure.Entities;

namespace Mrm.Api.Movies;

public static class MovieEndpoints
{
    public static IEndpointRouteBuilder MapMovieEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/movies").RequireAuthorization();

        group.MapPost("/", CreateMovie)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        group.MapGet("/", GetMovies)
             .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> CreateMovie(
        [FromBody] CreateMovieRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        CancellationToken ct)
    {
        var studioIdClaim = user.FindFirstValue(ClaimNames.StudioId);
        if (!Guid.TryParse(studioIdClaim, out var studioId))
            return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            StudioId = studioId,
            OriginalTitle = request.OriginalTitle.Trim(),
            NormalizedTitle = TitleNormalizer.Normalize(request.OriginalTitle),
            ReleaseYear = request.ReleaseYear,
            Status = MovieStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Movies.Add(movie);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/movies/{movie.Id}", movie.ToResponse());
    }

    private static async Task<IResult> GetMovies(
        ClaimsPrincipal user,
        MrmDbContext db,
        CancellationToken ct)
    {
        var roleClaim = user.FindFirstValue(ClaimTypes.Role);
        var studioIdClaim = user.FindFirstValue(ClaimNames.StudioId);

        IQueryable<Movie> query = db.Movies.AsNoTracking();

        // System admins see all; studio users see only their studio's movies
        if (roleClaim != nameof(UserRole.SystemAdmin))
        {
            if (!Guid.TryParse(studioIdClaim, out var studioId))
                return Results.Forbid();
            query = query.Where(m => m.StudioId == studioId);
        }

        var movies = await query
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ToResponse())
            .ToListAsync(ct);

        return Results.Ok(movies);
    }
}
