// ============================================================================
// SECTION 1 — Interface Signatures: Core Abstractions
// ============================================================================

namespace MovieLifecycle.Types;

// WHY: Single async method with nullable return. Returning null means "no conflict found."
// Simpler than wrapping in Result<T> since the only two outcomes are "conflict" or "clear."
// Each checker is a focused, single-responsibility class that queries one concern.
public interface IConflictChecker
{
    Task<ConflictResult?> CheckAsync(TransitionContext context, CancellationToken ct);
}

// WHY: Fluent DSL for registering conflict checkers on a specific transition edge.
// Generic Check<TChecker>() captures the type at registration (startup) but defers
// instantiation to request time, enabling proper scoped DI resolution.
public interface ITransitionEdgeBuilder
{
    ITransitionEdgeBuilder Check<TChecker>() where TChecker : class, IConflictChecker;
}

// WHY: Builder separates pipeline configuration (startup, singleton) from execution
// (request, scoped). The fluent API reads like a declaration of intent:
//   "on this edge, check these; use this middleware."
// UseXxx() methods register well-known middleware with correct ordering semantics;
// Use() allows custom inline steps for extension points.
public interface ITransitionPipelineBuilder
{
    ITransitionEdgeBuilder OnTransition(MovieStatus from, MovieStatus to);
    ITransitionPipelineBuilder Use(TransitionDelegate middleware);
    ITransitionPipelineBuilder UseAuditLog();
    ITransitionPipelineBuilder UseConflictChecks();
    ITransitionPipelineBuilder UseOverrideValidation();
    ITransitionPipelineBuilder UseStateGuard();
}

// WHY: Single method enforces that ALL transition logic flows through one code path.
// No second method can bypass validation, conflict checks, or audit logging.
// The TransitionCommand type hierarchy carries all variation (standard vs override)
// without multiplying methods. Callers interact with one method; the pipeline's
// internal middleware handles all branching.
public interface IMovieStatusService
{
    Task<TransitionResult> TransitionAsync(TransitionCommand command, CancellationToken ct);
}
