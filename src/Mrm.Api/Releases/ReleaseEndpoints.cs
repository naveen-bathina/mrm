using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mrm.Api.Auth;
using Mrm.Infrastructure;
using Mrm.Infrastructure.Conflicts;
using Mrm.Infrastructure.Entities;

namespace Mrm.Api.Releases;

public record ScheduleReleaseRequest(Guid TerritoryId, DateOnly ReleaseDate);

public record ReleaseResponse(
    Guid Id,
    Guid MovieId,
    Guid TerritoryId,
    string TerritoryName,
    DateOnly ReleaseDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record ReleaseWithWarningsResponse(
    ReleaseResponse Release,
    bool HasWarnings,
    IReadOnlyList<object> Warnings
);

public static class ReleaseEndpoints
{
    public static IEndpointRouteBuilder MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/movies/{movieId:guid}").RequireAuthorization();

        group.MapPost("/releases", ScheduleRelease)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        group.MapPut("/releases/{releaseId:guid}", UpdateRelease)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        group.MapPost("/releases/validate", ValidateRelease)
             .RequireAuthorization(AuthPolicies.StudioAdmin);

        return app;
    }

    private static async Task<IResult> ScheduleRelease(
        Guid movieId,
        [FromBody] ScheduleReleaseRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        ReleaseConflictChecker checker,
        CancellationToken ct)
    {
        var studioId = GetStudioId(user);
        if (studioId is null) return Results.Forbid();

        var movie = await db.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == movieId && m.StudioId == studioId, ct);
        if (movie is null) return Results.NotFound();

        var territory = await db.Territories.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TerritoryId && t.IsActive, ct);
        if (territory is null) return Results.NotFound("Territory not found or inactive.");

        // Check for existing release for this movie+territory
        var existing = await db.MovieReleases
            .FirstOrDefaultAsync(r => r.MovieId == movieId && r.TerritoryId == request.TerritoryId, ct);
        if (existing is not null)
            return Results.Conflict("A release date for this territory already exists. Use PUT to update.");

        var envelope = await checker.CheckAsync(request.TerritoryId, request.ReleaseDate, movieId, ct);

        var now = DateTimeOffset.UtcNow;
        var release = new MovieRelease
        {
            Id = Guid.NewGuid(),
            MovieId = movieId,
            TerritoryId = request.TerritoryId,
            ReleaseDate = request.ReleaseDate,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.MovieReleases.Add(release);
        await db.SaveChangesAsync(ct);

        var response = ToResponse(release, territory.Name);
        return Results.Ok(new { release = response, envelope });
    }

    private static async Task<IResult> UpdateRelease(
        Guid movieId,
        Guid releaseId,
        [FromBody] ScheduleReleaseRequest request,
        ClaimsPrincipal user,
        MrmDbContext db,
        ReleaseConflictChecker checker,
        CancellationToken ct)
    {
        var studioId = GetStudioId(user);
        if (studioId is null) return Results.Forbid();

        var movie = await db.Movies.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == movieId && m.StudioId == studioId, ct);
        if (movie is null) return Results.NotFound();

        var release = await db.MovieReleases
            .Include(r => r.Territory)
            .FirstOrDefaultAsync(r => r.Id == releaseId && r.MovieId == movieId, ct);
        if (release is null) return Results.NotFound();

        var territory = await db.Territories.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TerritoryId && t.IsActive, ct);
        if (territory is null) return Results.NotFound("Territory not found or inactive.");

        var envelope = await checker.CheckAsync(request.TerritoryId, request.ReleaseDate, movieId, ct);

        release.TerritoryId = request.TerritoryId;
        release.ReleaseDate = request.ReleaseDate;
        release.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var response = ToResponse(release, territory.Name);
        return Results.Ok(new { release = response, envelope });
    }

    private static async Task<IResult> ValidateRelease(
        Guid movieId,
        [FromBody] ScheduleReleaseRequest request,
        MrmDbContext db,
        ReleaseConflictChecker checker,
        CancellationToken ct)
    {
        var territory = await db.Territories.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TerritoryId && t.IsActive, ct);
        if (territory is null) return Results.NotFound("Territory not found or inactive.");

        var envelope = await checker.CheckAsync(request.TerritoryId, request.ReleaseDate, movieId, ct);
        return Results.Ok(envelope);
    }

    private static Guid? GetStudioId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue("studioId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static ReleaseResponse ToResponse(MovieRelease r, string territoryName) => new(
        r.Id, r.MovieId, r.TerritoryId, territoryName, r.ReleaseDate, r.CreatedAt, r.UpdatedAt);
}
