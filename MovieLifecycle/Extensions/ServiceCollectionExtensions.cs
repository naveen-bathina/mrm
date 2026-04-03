// ============================================================================
// SECTION 4 — Dependency Strategy: DI Registration Extension Method
// Shows singleton pipeline (built once) vs scoped services (per request),
// how conflict checkers are resolved from DI per-request, and how EF Core
// DbContext injection works inside scoped conflict checkers.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using MovieLifecycle.ConflictCheckers;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Pipeline;
using MovieLifecycle.Services;
using MovieLifecycle.Types;

namespace MovieLifecycle.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the movie status lifecycle module with the DI container.
    /// The builder callback configures the pipeline (middleware + per-edge checks)
    /// at startup; the built pipeline is a singleton reused for every request.
    /// </summary>
    /// <remarks>
    /// <b>Lifetime strategy:</b>
    /// <list type="bullet">
    ///   <item><b>Singleton:</b> TransitionPipeline — immutable definition, built once at startup</item>
    ///   <item><b>Scoped:</b> IMovieStatusService, MovieDbContext, all IConflictChecker implementations
    ///   — one instance per HTTP request, sharing the same DbContext for unit-of-work consistency</item>
    /// </list>
    ///
    /// <b>How conflict checkers are resolved per-request (not pre-instantiated):</b>
    /// The pipeline stores checker Types (not instances) at startup via OnTransition().Check&lt;T&gt;().
    /// At request time, ConflictCheckMiddleware calls serviceProvider.GetRequiredService(checkerType)
    /// against the request's scoped IServiceProvider. This means each checker gets a fresh instance
    /// with the correct scoped DbContext — no IServiceScopeFactory needed because the pipeline
    /// receives the request's own scope.
    ///
    /// <b>EF Core DbContext injection inside conflict checkers:</b>
    /// Checkers declare MovieDbContext as a constructor parameter. Since checkers are registered
    /// as Scoped and resolved from the request's scope, they automatically receive the same
    /// DbContext instance that MovieStatusService uses. All reads, writes, and audit entries
    /// share one unit of work committed by a single SaveChangesAsync call.
    /// </remarks>
    /// <example>
    /// <code>
    /// // In Program.cs:
    /// builder.Services.AddMovieStatusModule(pipeline =>
    /// {
    ///     // Middleware order = execution order (outermost to innermost)
    ///     pipeline
    ///         .UseAuditLog()           // 1. Wraps everything, logs before/after
    ///         .UseStateGuard()         // 2. Validates transition edge
    ///         .UseConflictChecks()     // 3. Runs per-edge checkers
    ///         .UseOverrideValidation();// 4. Blocks or allows based on mode + severity
    ///
    ///     // Per-edge conflict checker registration
    ///     pipeline
    ///         .OnTransition(MovieStatus.Draft, MovieStatus.Registered)
    ///             .Check&lt;TitleConflictChecker&gt;()
    ///             .Check&lt;ReleaseConflictChecker&gt;();
    ///
    ///     pipeline
    ///         .OnTransition(MovieStatus.Registered, MovieStatus.InProduction)
    ///             .Check&lt;PersonScheduleConflictChecker&gt;();
    ///
    ///     // Edges with no checkers (InProduction→Released, Released→Archived)
    ///     // don't need OnTransition — StateGuard validates them, no conflicts to check
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddMovieStatusModule(
        this IServiceCollection services,
        Action<ITransitionPipelineBuilder> configure)
    {
        // 1. Build the pipeline definition at startup (singleton)
        // WHY: The builder is used once, here, and then discarded. The resulting
        // TransitionPipeline is an immutable snapshot of the middleware chain and
        // edge-checker registrations. Thread-safe for concurrent request execution.
        var builder = new TransitionPipelineBuilder();
        configure(builder);
        var pipeline = builder.Build();

        services.AddSingleton(pipeline);

        // 2. Register conflict checkers as Scoped
        // WHY: Scoped ensures each checker gets the request's DbContext, not a stale
        // singleton or a fresh transient that misses the unit of work. The checkers
        // are resolved by Type from ConflictCheckMiddleware at request time.
        services.AddScoped<TitleConflictChecker>();
        services.AddScoped<ReleaseConflictChecker>();
        services.AddScoped<PersonScheduleConflictChecker>();

        // 3. Register the service as Scoped
        // WHY: Scoped aligns with DbContext lifetime. The service, its checkers, and
        // the audit log middleware all share the same DbContext instance per request.
        services.AddScoped<IMovieStatusService, MovieStatusService>();

        return services;
    }
}
