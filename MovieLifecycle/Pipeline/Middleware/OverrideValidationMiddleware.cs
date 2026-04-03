// ============================================================================
// SECTION 1 — Concrete Implementation: OverrideValidationMiddleware
// ============================================================================

using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline.Middleware;

// WHY: Sits after ConflictCheckMiddleware. Reads the gathered conflict results and
// decides whether hard conflicts block the transition or can be bypassed.
// Uses the visitor pattern to detect Override mode from the command type — no casting,
// no type-checking, no switch on a string. The visitor is the ONLY mechanism for
// branching on command type throughout the entire pipeline.
internal static class OverrideValidationMiddleware
{
    internal static TransitionDelegate Create(TransitionDelegate next)
    {
        return async (context, ct) =>
        {
            var hardConflicts = context.ConflictResults
                .Where(c => c.Severity == ConflictSeverity.Hard)
                .ToList();

            // WHY: Visitor extracts mode without casting. This is the single point
            // where Standard vs Override behavior diverges. Every other middleware
            // is mode-agnostic.
            var mode = context.Command.Accept(TransitionModeVisitor.Instance);

            if (hardConflicts.Count > 0 && mode == TransitionMode.Standard)
            {
                // WHY: Standard transition with hard conflicts → blocked.
                // The error message tells the caller exactly what to do: retry
                // with Override if they have System Admin permissions.
                context.Abort(new TransitionError(
                    "CONFLICT",
                    $"Transition blocked by {hardConflicts.Count} hard conflict(s). " +
                    "System Admin override required.",
                    hardConflicts));
                return;
            }

            // WHY: If Override mode, capture the OverrideResult on the context
            // regardless of whether hard conflicts exist. This ensures the audit
            // trail records that an override was used even when the transition
            // would have succeeded without one (defensive logging).
            if (mode == TransitionMode.Override)
            {
                var reason = context.Command.Accept(OverrideReasonVisitor.Instance);
                context.OverrideResult = new OverrideResult(
                    // WHY: AdminUserId would come from an auth context in production.
                    // In the middleware pipeline, the command doesn't carry identity —
                    // this is a deliberate separation. The service layer sets identity
                    // before creating the context. Here we use a placeholder.
                    AdminUserId: "system-admin",
                    Reason: reason!,
                    Timestamp: DateTimeOffset.UtcNow);
            }

            await next(context, ct);
        };
    }
}
