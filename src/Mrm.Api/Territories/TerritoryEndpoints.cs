using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mrm.Api.Auth;
using Mrm.Infrastructure;
using Mrm.Infrastructure.Entities;

namespace Mrm.Api.Territories;

public record CreateTerritoryRequest(string Name, string Code);
public record UpdateTerritoryRequest(string Name, string Code);
public record TerritoryResponse(Guid Id, string Name, string Code, bool IsActive);

public static class TerritoryEndpoints
{
    public static IEndpointRouteBuilder MapTerritoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/territories").RequireAuthorization();

        group.MapGet("/", GetTerritories);

        group.MapPost("/", CreateTerritory)
             .RequireAuthorization(AuthPolicies.SystemAdmin);

        group.MapPut("/{id:guid}", UpdateTerritory)
             .RequireAuthorization(AuthPolicies.SystemAdmin);

        group.MapDelete("/{id:guid}", DeactivateTerritory)
             .RequireAuthorization(AuthPolicies.SystemAdmin);

        return app;
    }

    private static async Task<IResult> GetTerritories(MrmDbContext db, CancellationToken ct)
    {
        var territories = await db.Territories
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TerritoryResponse(t.Id, t.Name, t.Code, t.IsActive))
            .ToListAsync(ct);

        return Results.Ok(territories);
    }

    private static async Task<IResult> CreateTerritory(
        [FromBody] CreateTerritoryRequest request,
        MrmDbContext db,
        CancellationToken ct)
    {
        var territory = new Territory
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Code = request.Code.Trim().ToUpperInvariant(),
            IsActive = true,
        };

        db.Territories.Add(territory);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/territories/{territory.Id}",
            new TerritoryResponse(territory.Id, territory.Name, territory.Code, territory.IsActive));
    }

    private static async Task<IResult> UpdateTerritory(
        Guid id,
        [FromBody] UpdateTerritoryRequest request,
        MrmDbContext db,
        CancellationToken ct)
    {
        var territory = await db.Territories.FindAsync([id], ct);
        if (territory is null) return Results.NotFound();

        territory.Name = request.Name.Trim();
        territory.Code = request.Code.Trim().ToUpperInvariant();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new TerritoryResponse(territory.Id, territory.Name, territory.Code, territory.IsActive));
    }

    private static async Task<IResult> DeactivateTerritory(
        Guid id,
        MrmDbContext db,
        CancellationToken ct)
    {
        var territory = await db.Territories.FindAsync([id], ct);
        if (territory is null) return Results.NotFound();

        territory.IsActive = false;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
