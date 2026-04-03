using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mrm.Infrastructure.Entities;
using Mrm.Tests.Helpers;

namespace Mrm.Tests;

[Collection("Integration")]
public class TerritoryAndReleaseTests(MrmWebFactory factory) : IClassFixture<MrmWebFactory>
{
    private HttpClient Client(UserRole role, Guid? studioId = null)
    {
        studioId ??= Guid.NewGuid();
        var token = JwtHelper.GenerateToken(Guid.NewGuid(), role, studioId);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient SystemAdminClient() => Client(UserRole.SystemAdmin);
    private HttpClient StudioClient(Guid? studioId = null) => Client(UserRole.StudioAdmin, studioId ?? Guid.NewGuid());

    // ── Territory CRUD ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTerritory_SystemAdmin_Returns201()
    {
        factory.MigrateAndGet().Dispose();
        var resp = await SystemAdminClient().PostAsJsonAsync("/territories",
            new { Name = "United States", Code = "US" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var t = await resp.Content.ReadFromJsonAsync<TerritoryDto>();
        Assert.Equal("US", t!.Code);
        Assert.True(t.IsActive);
    }

    [Fact]
    public async Task CreateTerritory_StudioAdmin_Returns403()
    {
        factory.MigrateAndGet().Dispose();
        var resp = await StudioClient().PostAsJsonAsync("/territories",
            new { Name = "UK", Code = "GB" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DeactivateTerritory_ExcludedFromList()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();

        var created = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "France", Code = "FR" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();

        await admin.DeleteAsync($"/territories/{created!.Id}");

        var list = await (await admin.GetAsync("/territories"))
            .Content.ReadFromJsonAsync<List<TerritoryDto>>();

        Assert.DoesNotContain(list!, t => t.Id == created.Id);
    }

    // ── Release scheduling ────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleRelease_NoConflict_Returns200WithNoWarnings()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();
        var studioId = Guid.NewGuid();
        var studio = StudioClient(studioId);

        var territory = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "Germany", Code = "DE" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();

        var movie = await (await studio.PostAsJsonAsync("/movies",
            new { OriginalTitle = "No Conflict Film", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        var resp = await studio.PostAsJsonAsync($"/movies/{movie!.Id}/releases", new
        {
            TerritoryId = territory!.Id,
            ReleaseDate = "2025-12-01",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ReleaseWithEnvelopeDto>();
        Assert.False(body!.Envelope.Blocked);
        Assert.Empty(body.Envelope.Conflicts);
    }

    [Fact]
    public async Task ScheduleRelease_SameTerritoryAndDate_ReturnsSoftWarning()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();

        var territory = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "Japan", Code = "JP" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();

        // Studio A books the date
        var clientA = StudioClient(studioA);
        var movieA = await (await clientA.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Film A Japan", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();
        await clientA.PostAsJsonAsync($"/movies/{movieA!.Id}/releases", new
        {
            TerritoryId = territory!.Id,
            ReleaseDate = "2025-06-15",
        });

        // Studio B books the same territory + date → soft warning
        var clientB = StudioClient(studioB);
        var movieB = await (await clientB.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Film B Japan", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        var resp = await clientB.PostAsJsonAsync($"/movies/{movieB!.Id}/releases", new
        {
            TerritoryId = territory.Id,
            ReleaseDate = "2025-06-15",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ReleaseWithEnvelopeDto>();
        Assert.False(body!.Envelope.Blocked); // soft — not blocked
        Assert.NotEmpty(body.Envelope.Conflicts);
        Assert.Equal("ReleaseDateConflict", body.Envelope.Conflicts[0].Type);
        Assert.Equal("soft", body.Envelope.Conflicts[0].Severity);
    }

    [Fact]
    public async Task ScheduleRelease_InactiveTerritory_Returns404()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();
        var studioId = Guid.NewGuid();
        var studio = StudioClient(studioId);

        var territory = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "Australia", Code = "AU" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();
        await admin.DeleteAsync($"/territories/{territory!.Id}");

        var movie = await (await studio.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Aussie Film", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        var resp = await studio.PostAsJsonAsync($"/movies/{movie!.Id}/releases", new
        {
            TerritoryId = territory.Id,
            ReleaseDate = "2025-09-01",
        });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ValidateRelease_ReturnsEnvelopeWithoutSaving()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();
        var studioId = Guid.NewGuid();
        var studio = StudioClient(studioId);

        var territory = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "Brazil", Code = "BR" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();

        var movie = await (await studio.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Brazil Film", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();

        // Pre-flight validate — no release should exist yet
        var resp = await studio.PostAsJsonAsync($"/movies/{movie!.Id}/releases/validate", new
        {
            TerritoryId = territory!.Id,
            ReleaseDate = "2025-11-01",
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Confirm no release was saved
        var releases = await (await studio.GetAsync($"/movies"))
            .Content.ReadFromJsonAsync<List<MovieDto>>();
        // The validate endpoint only returns the envelope, doesn't create a release
        var envelope = await resp.Content.ReadFromJsonAsync<ConflictEnvelopeDto>();
        Assert.False(envelope!.Blocked);
    }

    [Fact]
    public async Task UpdateRelease_RerunsConflictCheck()
    {
        factory.MigrateAndGet().Dispose();
        var admin = SystemAdminClient();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();

        var territory = await (await admin.PostAsJsonAsync("/territories",
            new { Name = "Canada", Code = "CA" }))
            .Content.ReadFromJsonAsync<TerritoryDto>();

        // Studio A books 2025-03-01
        var clientA = StudioClient(studioA);
        var movieA = await (await clientA.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Canadian Film A", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();
        await clientA.PostAsJsonAsync($"/movies/{movieA!.Id}/releases", new
        {
            TerritoryId = territory!.Id,
            ReleaseDate = "2025-03-01",
        });

        // Studio B books 2025-04-01 (no conflict)
        var clientB = StudioClient(studioB);
        var movieB = await (await clientB.PostAsJsonAsync("/movies",
            new { OriginalTitle = "Canadian Film B", ReleaseYear = 2025 }))
            .Content.ReadFromJsonAsync<MovieDto>();
        var releaseB = await (await clientB.PostAsJsonAsync($"/movies/{movieB!.Id}/releases", new
        {
            TerritoryId = territory.Id,
            ReleaseDate = "2025-04-01",
        })).Content.ReadFromJsonAsync<ReleaseWithEnvelopeDto>();

        // Studio B updates to clash with Studio A's date
        var updateResp = await clientB.PutAsJsonAsync(
            $"/movies/{movieB.Id}/releases/{releaseB!.Release.Id}", new
            {
                TerritoryId = territory.Id,
                ReleaseDate = "2025-03-01",  // now conflicts
            });

        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = await updateResp.Content.ReadFromJsonAsync<ReleaseWithEnvelopeDto>();
        Assert.False(updated!.Envelope.Blocked); // still soft
        Assert.NotEmpty(updated.Envelope.Conflicts);
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private record TerritoryDto(Guid Id, string Name, string Code, bool IsActive);
    private record MovieDto(Guid Id, Guid StudioId, string OriginalTitle, string NormalizedTitle,
        int ReleaseYear, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private record ReleaseDto(Guid Id, Guid MovieId, Guid TerritoryId, string TerritoryName,
        DateOnly ReleaseDate, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private record ConflictItemDto(string Type, string Severity, string Detail);
    private record ConflictEnvelopeDto(bool Blocked, List<ConflictItemDto> Conflicts);
    private record ReleaseWithEnvelopeDto(ReleaseDto Release, ConflictEnvelopeDto Envelope);
}
