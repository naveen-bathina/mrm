// ============================================================================
// SECTION 1 — Concrete Implementation: ConflictCheckMiddleware
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline.Middleware;

// WHY: Resolves conflict checkers from DI per-edge and populates context.ConflictResults.
// Checkers are NOT pre-instantiated at startup — they're resolved from the request's DI
// scope at execution time so each checker receives the correct scoped DbContext and
// participates in the same unit of work as the rest of the pipeline.
internal static class ConflictCheckMiddleware
{
    internal static TransitionDelegate Create(
        IServiceProvider serviceProvider,
        Dictionary<(MovieStatus, MovieStatus), List<Type>> edgeCheckers,
        TransitionDelegate next)
    {
        return async (context, ct) =>
        {
            var edge = (context.CurrentStatus, context.Command.TargetStatus);

            if (edgeCheckers.TryGetValue(edge, out var checkerTypes))
            {
                foreach (var checkerType in checkerTypes)
                {
                    // WHY: Resolve from the scoped provider so checkers share the
                    // request's DbContext and participate in the same unit of work.
                    // GetRequiredService throws if the checker isn't registered — this
                    // is a fail-fast for misconfigured DI, caught on first request.
                    var checker = (IConflictChecker)serviceProvider.GetRequiredService(checkerType);

                    var result = await checker.CheckAsync(context, ct);

                    if (result is not null)
                    {
                        context.ConflictResults.Add(result);
                    }

                    // WHY: A checker may have called context.Abort() directly for
                    // critical/unrecoverable failures. Respect the short-circuit.
                    if (context.IsAborted) return;
                }
            }

            // WHY: Always call next() even if conflicts were found. The downstream
            // OverrideValidationMiddleware decides whether conflicts are blocking
            // (based on severity and mode). This separation of concerns means
            // ConflictCheckMiddleware only gathers data — it never makes policy decisions.
            await next(context, ct);
        };
    }
}
