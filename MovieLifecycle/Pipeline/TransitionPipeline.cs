// ============================================================================
// SECTION 1 — Concrete Implementation: TransitionPipeline (executor)
// ============================================================================

using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline;

// WHY: Immutable pipeline definition built once at startup, executed per-request.
// The middleware chain is re-composed per-request because each execution needs
// fresh scoped services (DbContext, conflict checkers). Composition is O(n) where
// n is the number of middleware steps (typically 4–5), so the overhead is negligible.
// This mirrors how ASP.NET Core rebuilds the middleware pipeline: the app-level
// pipeline is built once, but each request gets a fresh scope for DI resolution.
public sealed class TransitionPipeline
{
    private readonly List<Func<IServiceProvider, Func<TransitionDelegate, TransitionDelegate>>>
        _middlewareFactories;

    private readonly Dictionary<(MovieStatus, MovieStatus), List<Type>> _edgeCheckers;

    internal TransitionPipeline(
        List<Func<IServiceProvider, Func<TransitionDelegate, TransitionDelegate>>> middlewareFactories,
        Dictionary<(MovieStatus, MovieStatus), List<Type>> edgeCheckers)
    {
        _middlewareFactories = middlewareFactories;
        _edgeCheckers = edgeCheckers;
    }

    // WHY: scopedProvider is the request's DI scope. Each middleware factory resolves
    // its dependencies from this scope, ensuring all components (checkers, DbContext,
    // audit logger) share the same scoped lifetime and participate in one unit of work.
    public async Task ExecuteAsync(
        TransitionContext context,
        IServiceProvider scopedProvider,
        CancellationToken ct)
    {
        // WHY: Terminal delegate is a no-op. It represents the "end" of the pipeline.
        // If all middleware passes without aborting, the pipeline completes successfully.
        TransitionDelegate terminal = (_, _) => Task.CompletedTask;

        // WHY: Compose from back to front so the first registered middleware is the
        // outermost wrapper (executes first), matching ASP.NET Core's ordering behavior.
        // Registration order: [AuditLog, StateGuard, ConflictChecks, OverrideValidation]
        // Execution order:    AuditLog → StateGuard → ConflictChecks → OverrideValidation → terminal
        var chain = terminal;
        for (var i = _middlewareFactories.Count - 1; i >= 0; i--)
        {
            var wrapper = _middlewareFactories[i](scopedProvider);
            chain = wrapper(chain);
        }

        await chain(context, ct);
    }

    public IReadOnlyDictionary<(MovieStatus, MovieStatus), List<Type>> GetEdgeCheckers()
        => _edgeCheckers;
}
