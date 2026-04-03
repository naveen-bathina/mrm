// ============================================================================
// SECTION 1 — Concrete Implementation: TransitionPipelineBuilder
// ============================================================================

using MovieLifecycle.Pipeline.Middleware;
using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline;

// WHY: Collects middleware registrations and edge-checker mappings at startup,
// then builds an immutable TransitionPipeline. The builder is used once during
// service configuration; the built pipeline is a singleton reused for every request.
// Middleware factories are Func<IServiceProvider, Func<TransitionDelegate, TransitionDelegate>>
// so each factory can resolve scoped dependencies at request time while the pipeline
// definition itself remains stateless and thread-safe.
internal sealed class TransitionPipelineBuilder : ITransitionPipelineBuilder
{
    // WHY: Each middleware factory receives the request's IServiceProvider to resolve
    // scoped dependencies (DbContext, checkers), then returns a wrapper that chains
    // the TransitionDelegate. This two-level factory pattern separates startup
    // registration from request-time resolution.
    internal readonly List<Func<IServiceProvider, Func<TransitionDelegate, TransitionDelegate>>>
        MiddlewareFactories = new();

    // WHY: Edge checker types are stored, not instances. Resolution happens
    // per-request so each checker gets the correct scoped DbContext.
    internal readonly Dictionary<(MovieStatus From, MovieStatus To), List<Type>>
        EdgeCheckers = new();

    public ITransitionEdgeBuilder OnTransition(MovieStatus from, MovieStatus to)
    {
        var key = (from, to);
        if (!EdgeCheckers.ContainsKey(key))
            EdgeCheckers[key] = new List<Type>();
        return new TransitionEdgeBuilder(EdgeCheckers[key]);
    }

    // WHY: Use() accepts a raw TransitionDelegate for custom inline steps.
    // The builder wraps it to call next() when not aborted, so callers don't
    // need to worry about chaining — they just write their logic.
    public ITransitionPipelineBuilder Use(TransitionDelegate middleware)
    {
        MiddlewareFactories.Add(_ => next => async (ctx, ct) =>
        {
            await middleware(ctx, ct);
            if (!ctx.IsAborted) await next(ctx, ct);
        });
        return this;
    }

    // WHY: Named UseXxx() methods register well-known middleware with the correct
    // internal wiring. Each method captures its dependencies (edge checkers, etc.)
    // at registration time and defers service resolution to request time.

    public ITransitionPipelineBuilder UseAuditLog()
    {
        MiddlewareFactories.Add(sp => next =>
            AuditLogMiddleware.Create(sp, next));
        return this;
    }

    public ITransitionPipelineBuilder UseConflictChecks()
    {
        // WHY: Capture the edge checkers dictionary by reference. Since the builder
        // is used once at startup and then discarded, this is safe — no mutation after Build().
        var edgeCheckers = EdgeCheckers;
        MiddlewareFactories.Add(sp => next =>
            ConflictCheckMiddleware.Create(sp, edgeCheckers, next));
        return this;
    }

    public ITransitionPipelineBuilder UseOverrideValidation()
    {
        MiddlewareFactories.Add(_ => next =>
            OverrideValidationMiddleware.Create(next));
        return this;
    }

    public ITransitionPipelineBuilder UseStateGuard()
    {
        MiddlewareFactories.Add(_ => next =>
            StateGuardMiddleware.Create(next));
        return this;
    }

    internal TransitionPipeline Build()
    {
        return new TransitionPipeline(
            new List<Func<IServiceProvider, Func<TransitionDelegate, TransitionDelegate>>>(MiddlewareFactories),
            new Dictionary<(MovieStatus, MovieStatus), List<Type>>(EdgeCheckers));
    }
}
