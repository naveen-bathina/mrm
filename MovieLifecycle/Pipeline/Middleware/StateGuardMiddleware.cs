// ============================================================================
// SECTION 1 — Concrete Implementation: StateGuardMiddleware
// ============================================================================

using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline.Middleware;

// WHY: First line of defense — rejects invalid/out-of-order transitions before any
// business logic (DB queries, conflict checks) executes. This prevents wasted work
// for transitions that are structurally impossible in the state machine.
// Invalid transitions are NEVER overridable — even System Admin cannot skip states
// or go backward. This is a hard domain invariant, not a policy decision.
internal static class StateGuardMiddleware
{
    // WHY: Static set of valid edges. This is the single source of truth for the
    // state machine. Adding or removing edges requires changing only this set.
    // HashSet gives O(1) lookup for every transition request.
    private static readonly HashSet<(MovieStatus From, MovieStatus To)> ValidTransitions = new()
    {
        (MovieStatus.Draft, MovieStatus.Registered),
        (MovieStatus.Registered, MovieStatus.InProduction),
        (MovieStatus.InProduction, MovieStatus.Released),
        (MovieStatus.Released, MovieStatus.Archived),
    };

    // WHY: Returns a TransitionDelegate that wraps `next`. If the edge is valid,
    // it calls next(); if invalid, it short-circuits via context.Abort().
    // This is the ASP.NET Core middleware pattern: wrap the downstream delegate.
    internal static TransitionDelegate Create(TransitionDelegate next)
    {
        return async (context, ct) =>
        {
            var edge = (context.CurrentStatus, context.Command.TargetStatus);

            if (!ValidTransitions.Contains(edge))
            {
                // WHY: Short-circuit immediately. Abort prevents all downstream
                // middleware from executing — no conflict checks, no audit of
                // "attempted" transitions for structurally invalid requests.
                context.Abort(new TransitionError(
                    "INVALID_TRANSITION",
                    $"Transition from {context.CurrentStatus} to {context.Command.TargetStatus} " +
                    "is not allowed. Valid transitions follow the lifecycle: " +
                    "Draft → Registered → InProduction → Released → Archived.",
                    Array.Empty<ConflictResult>()));
                return;
            }

            await next(context, ct);
        };
    }
}
