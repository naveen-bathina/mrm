using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mrm.Api.Auth;
using Mrm.Infrastructure;
using Mrm.Infrastructure.Conflicts;
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

        group.MapPut("/{id:guid}", UpdateMovie)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        group.MapPost("/validate", ValidateTitle)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        group.MapPost("/{id:guid}/transition", TransitionMovie)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        return app;
    }

    // ── POST /movies ──────────────────────────────────────────────────────────

    private static async Task<IResult> CreateMovie(
        [FromBody] CreateMovieRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        CancellationToken ct)
    {
        var studioId = GetStudioId(user);
        if (studioId is null) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            StudioId = studioId.Value,
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

    // ── GET /movies ───────────────────────────────────────────────────────────

    private static async Task<IResult> GetMovies(
        ClaimsPrincipal user,
        MrmDbContext db,
        CancellationToken ct)
    {
        var roleClaim = user.FindFirstValue(ClaimTypes.Role);
        IQueryable<Movie> query = db.Movies.AsNoTracking();

        if (roleClaim != nameof(UserRole.SystemAdmin))
        {
            var studioId = GetStudioId(user);
            if (studioId is null) return Results.Forbid();
            query = query.Where(m => m.StudioId == studioId.Value);
        }

        var movies = await query
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ToResponse())
            .ToListAsync(ct);

        return Results.Ok(movies);
    }

    // ── PUT /movies/{id} ──────────────────────────────────────────────────────

    private static async Task<IResult> UpdateMovie(
        Guid id,
        [FromBody] UpdateMovieRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        TitleConflictChecker checker,
        CancellationToken ct)
    {
        var studioId = GetStudioId(user);
        if (studioId is null) return Results.Forbid();

        var movie = await db.Movies.FirstOrDefaultAsync(
            m => m.Id == id && m.StudioId == studioId.Value, ct);
        if (movie is null) return Results.NotFound();

        var newNormalized = TitleNormalizer.Normalize(request.OriginalTitle);
        var titleChanged = newNormalized != movie.NormalizedTitle
                        || request.ReleaseYear != movie.ReleaseYear;

        if (titleChanged)
        {
            var envelope = await checker.CheckAsync(
                newNormalized, request.ReleaseYear, excludeMovieId: id, ct);
            if (envelope.Blocked)
                return Results.Conflict(envelope);
        }

        movie.OriginalTitle = request.OriginalTitle.Trim();
        movie.NormalizedTitle = newNormalized;
        movie.ReleaseYear = request.ReleaseYear;
        movie.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(movie.ToResponse());
    }

    // ── POST /movies/validate ─────────────────────────────────────────────────

    private static async Task<IResult> ValidateTitle(
        [FromBody] ValidateMovieTitleRequest request,
        TitleConflictChecker checker,
        CancellationToken ct)
    {
        var normalized = TitleNormalizer.Normalize(request.OriginalTitle);
        var envelope = await checker.CheckAsync(
            normalized, request.ReleaseYear, request.ExcludeMovieId, ct);
        return Results.Ok(envelope);
    }

    // ── POST /movies/{id}/transition ──────────────────────────────────────────

    private static async Task<IResult> TransitionMovie(
        Guid id,
        [FromBody] TransitionMovieRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        TitleConflictChecker checker,
        CancellationToken ct)
    {
        var studioId = GetStudioId(user);
        if (studioId is null) return Results.Forbid();

        var movie = await db.Movies.FirstOrDefaultAsync(
            m => m.Id == id && m.StudioId == studioId.Value, ct);
        if (movie is null) return Results.NotFound();

        if (!Enum.TryParse<MovieStatus>(request.TargetStatus, out var target))
            return Results.BadRequest($"Unknown status '{request.TargetStatus}'.");

        // Validate transition is legal
        var validationError = ValidateTransition(movie.Status, target);
        if (validationError is not null)
            return Results.UnprocessableEntity(new { error = validationError });

        // Draft → Registered: run title conflict check (hard block)
        if (movie.Status == MovieStatus.Draft && target == MovieStatus.Registered)
        {
            var envelope = await checker.CheckAsync(
                movie.NormalizedTitle, movie.ReleaseYear, excludeMovieId: id, ct);
            if (envelope.Blocked)
                return Results.Conflict(envelope);
        }

        movie.Status = target;
        movie.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(movie.ToResponse());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Guid? GetStudioId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue(ClaimNames.StudioId);
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static string? ValidateTransition(MovieStatus current, MovieStatus target)
    {
        // Only forward sequential transitions are allowed
        var allowed = current switch
        {
            MovieStatus.Draft          => MovieStatus.Registered,
            MovieStatus.Registered     => MovieStatus.InProduction,
            MovieStatus.InProduction   => MovieStatus.PostProduction,
            MovieStatus.PostProduction => MovieStatus.Released,
            _                          => (MovieStatus?)null,
        };

        if (allowed is null)
            return $"Movie is already in terminal status '{current}'.";
        if (target != allowed)
            return $"Cannot transition from '{current}' to '{target}'. Expected '{allowed}'.";
        return null;
    }
}
