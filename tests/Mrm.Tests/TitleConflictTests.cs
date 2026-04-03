using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mrm.Infrastructure.Entities;
using Mrm.Tests.Helpers;

namespace Mrm.Tests;

[Collection("Integration")]
public class TitleConflictTests(MrmWebFactory factory) : IClassFixture<MrmWebFactory>
{
    private HttpClient Client(UserRole role = UserRole.StudioAdmin, Guid? studioId = null)
    {
        studioId ??= Guid.NewGuid();
        var token = JwtHelper.GenerateToken(Guid.NewGuid(), role, studioId);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── POST /movies/validate ────────────────────────────────────────────────

    [Fact]
    public async Task Validate_NoDuplicate_ReturnsNotBlocked()
    {
        factory.MigrateAndGet().Dispose();
        var client = Client();

        var resp = await client.PostAsJsonAsync("/movies/validate", new
        {
            OriginalTitle = "Unique Title XYZ",
            ReleaseYear = 2030,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var envelope = await resp.Content.ReadFromJsonAsync<ConflictEnvelopeDto>();
        Assert.False(envelope!.Blocked);
        Assert.Empty(envelope.Conflicts);
    }

    [Fact]
    public async Task Validate_DuplicateNormalizedTitle_ReturnsBlocked()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        await client.PostAsJsonAsync("/movies", new { OriginalTitle = "Spider-Man", ReleaseYear = 2002 });

        var resp = await client.PostAsJsonAsync("/movies/validate", new
        {
            OriginalTitle = "spiderman",   // normalizes to same: "spiderman"
            ReleaseYear = 2002,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var envelope = await resp.Content.ReadFromJsonAsync<ConflictEnvelopeDto>();
        Assert.True(envelope!.Blocked);
        Assert.NotEmpty(envelope.Conflicts);
        Assert.Equal("TitleConflict", envelope.Conflicts[0].Type);
    }

    [Fact]
    public async Task Validate_SameTitleDifferentYear_ReturnsNotBlocked()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        await client.PostAsJsonAsync("/movies", new { OriginalTitle = "Batman", ReleaseYear = 1989 });

        var resp = await client.PostAsJsonAsync("/movies/validate", new
        {
            OriginalTitle = "Batman",
            ReleaseYear = 2022,   // different year — no conflict
        });

        var envelope = await resp.Content.ReadFromJsonAsync<ConflictEnvelopeDto>();
        Assert.False(envelope!.Blocked);
    }

    // ── POST /movies/{id}/transition (Draft → Registered) ───────────────────

    [Fact]
    public async Task Transition_DraftToRegistered_NoConflict_Succeeds()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        var created = await (await client.PostAsJsonAsync("/movies",
            new { OriginalTitle = "My Unique Film", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        var resp = await client.PostAsJsonAsync(
            $"/movies/{created!.Id}/transition",
            new { TargetStatus = "Registered" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var movie = await resp.Content.ReadFromJsonAsync<MovieDto>();
        Assert.Equal("Registered", movie!.Status);
    }

    [Fact]
    public async Task Transition_DraftToRegistered_WithConflict_Returns409()
    {
        factory.MigrateAndGet().Dispose();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();

        // Studio A registers the title first
        var clientA = Client(studioId: studioA);
        var movieA = await (await clientA.PostAsJsonAsync("/movies",
            new { OriginalTitle = "The Matrix", ReleaseYear = 1999 }))
            .Content.ReadFromJsonAsync<MovieDto>();
        await clientA.PostAsJsonAsync($"/movies/{movieA!.Id}/transition",
            new { TargetStatus = "Registered" });

        // Studio B tries to register same normalized title + year
        var clientB = Client(studioId: studioB);
        var movieB = await (await clientB.PostAsJsonAsync("/movies",
            new { OriginalTitle = "the matrix", ReleaseYear = 1999 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        var resp = await clientB.PostAsJsonAsync(
            $"/movies/{movieB!.Id}/transition",
            new { TargetStatus = "Registered" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Transition_InvalidOrder_Returns422()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        var movie = await (await client.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Jump Ahead", ReleaseYear = 2026 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        // Draft → InProduction is not allowed (must go through Registered)
        var resp = await client.PostAsJsonAsync(
            $"/movies/{movie!.Id}/transition",
            new { TargetStatus = "InProduction" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // ── PUT /movies/{id} ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMovie_TitleConflict_Returns409()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        await client.PostAsJsonAsync("/movies", new { OriginalTitle = "Inception", ReleaseYear = 2010 });
        var m2 = await (await client.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Interstellar", ReleaseYear = 2014 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        // Try to rename m2 to clash with Inception
        var resp = await client.PutAsJsonAsync($"/movies/{m2!.Id}", new
        {
            OriginalTitle = "INCEPTION",
            ReleaseYear = 2010,
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateMovie_SameTitle_ExcludesSelf_Succeeds()
    {
        factory.MigrateAndGet().Dispose();
        var studioId = Guid.NewGuid();
        var client = Client(studioId: studioId);

        var movie = await (await client.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Dune", ReleaseYear = 2021 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        // Update with the same title — should not conflict with itself
        var resp = await client.PutAsJsonAsync($"/movies/{movie!.Id}", new
        {
            OriginalTitle = "Dune",
            ReleaseYear = 2021,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private record MovieDto(Guid Id, Guid StudioId, string OriginalTitle,
        string NormalizedTitle, int ReleaseYear, string Status,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private record ConflictItemDto(string Type, string Severity, string Detail);
    private record ConflictEnvelopeDto(bool Blocked, List<ConflictItemDto> Conflicts);
}
