// ============================================================================
// SECTION 1 — Concrete Implementation: TransitionEdgeBuilder
// ============================================================================

using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline;

// WHY: Captures the (from, to) edge and accumulates checker types into the
// shared dictionary owned by TransitionPipelineBuilder.
// Types (not instances) are stored because checkers are scoped — they must be
// resolved fresh from DI at request time to receive the correct DbContext.
internal sealed class TransitionEdgeBuilder : ITransitionEdgeBuilder
{
    private readonly List<Type> _checkerTypes;

    internal TransitionEdgeBuilder(List<Type> checkerTypes)
    {
        _checkerTypes = checkerTypes;
    }

    public ITransitionEdgeBuilder Check<TChecker>() where TChecker : class, IConflictChecker
    {
        _checkerTypes.Add(typeof(TChecker));
        return this;
    }
}
