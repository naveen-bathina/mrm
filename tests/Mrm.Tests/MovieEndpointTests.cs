using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mrm.Infrastructure.Entities;
using Mrm.Tests.Helpers;

namespace Mrm.Tests;

[Collection("Integration")]
public class MovieEndpointTests(MrmWebFactory factory) : IClassFixture<MrmWebFactory>
{
    private HttpClient CreateClient(UserRole role, Guid? studioId = null)
    {
        var userId = Guid.NewGuid();
        var token = JwtHelper.GenerateToken(userId, role, studioId);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task PostMovie_StudioAdmin_Returns201()
    {
        using var db = factory.MigrateAndGet();
        var studioId = Guid.NewGuid();
        var client = CreateClient(UserRole.StudioAdmin, studioId);

        var response = await client.PostAsJsonAsync("/movies", new
        {
            OriginalTitle = "The Dark Knight",
            ReleaseYear = 2008,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MovieDto>();
        Assert.NotNull(body);
        Assert.Equal("the dark knight", body!.NormalizedTitle);
        Assert.Equal(studioId, body.StudioId);
        Assert.Equal("Draft", body.Status);
    }

    [Fact]
    public async Task PostMovie_ProductionManager_Returns403()
    {
        using var db = factory.MigrateAndGet();
        var studioId = Guid.NewGuid();
        var client = CreateClient(UserRole.ProductionManager, studioId);

        var response = await client.PostAsJsonAsync("/movies", new
        {
            OriginalTitle = "Unauthorized",
            ReleaseYear = 2024,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostMovie_Unauthenticated_Returns401()
    {
        using var db = factory.MigrateAndGet();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/movies", new
        {
            OriginalTitle = "No Token",
            ReleaseYear = 2024,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMovies_StudioAdmin_ReturnsOnlyOwnStudioMovies()
    {
        using var db = factory.MigrateAndGet();
        var studioA = Guid.NewGuid();
        var studioB = Guid.NewGuid();
        var client = CreateClient(UserRole.StudioAdmin, studioA);

        // Create movie for studio A
        await client.PostAsJsonAsync("/movies", new { OriginalTitle = "Studio A Film", ReleaseYear = 2024 });

        // Create movie for studio B using a different client
        var clientB = CreateClient(UserRole.StudioAdmin, studioB);
        await clientB.PostAsJsonAsync("/movies", new { OriginalTitle = "Studio B Film", ReleaseYear = 2024 });

        var response = await client.GetAsync("/movies");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var movies = await response.Content.ReadFromJsonAsync<List<MovieDto>>();
        Assert.NotNull(movies);
        Assert.All(movies!, m => Assert.Equal(studioA, m.StudioId));
        Assert.DoesNotContain(movies!, m => m.StudioId == studioB);
    }

    [Fact]
    public async Task TitleNormalization_DiacriticsAndPunctuation_Normalized()
    {
        using var db = factory.MigrateAndGet();
        var studioId = Guid.NewGuid();
        var client = CreateClient(UserRole.StudioAdmin, studioId);

        var response = await client.PostAsJsonAsync("/movies", new
        {
            OriginalTitle = "Café  de Flôre!",
            ReleaseYear = 2011,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MovieDto>();
        Assert.Equal("cafe de flore", body!.NormalizedTitle);
    }

    private record MovieDto(
        Guid Id,
        Guid StudioId,
        string OriginalTitle,
        string NormalizedTitle,
        int ReleaseYear,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt
    );
}
