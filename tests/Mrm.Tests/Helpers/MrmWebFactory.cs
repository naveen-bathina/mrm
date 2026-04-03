using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mrm.Infrastructure;
using Testcontainers.PostgreSql;

namespace Mrm.Tests.Helpers;

public class MrmWebFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mrm_test")
        .WithUsername("mrm")
        .WithPassword("mrm")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the real DbContext with a test one pointing at the container
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<MrmDbContext>));
            if (descriptor != null)
                services.Remove(descriptor);

            services.AddDbContext<MrmDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Override JWT config to match JwtHelper constants
            var overrides = new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "replace-with-32-char-secret-key-here!!",
                ["Jwt:Issuer"] = "mrm-api",
                ["Jwt:Audience"] = "mrm-client",
            };
            config.AddInMemoryCollection(overrides);
        });
    }

    public MrmDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MrmDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        var ctx = new MrmDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
