// ============================================================================
// SECTION 1 — Interface Signatures: TransitionContext + TransitionDelegate
// ============================================================================

namespace MovieLifecycle.Types;

// WHY: Mutable bag shared across middleware steps, directly modeled after HttpContext
// in ASP.NET Core. Each step can read results from previous steps and enrich the
// context for downstream steps. This enables middleware to communicate without
// complex return value threading.
// Trade-off: mutability enables simple middleware signatures but complicates testing
// (must verify the full context state) and thread-safety reasoning (mitigated by
// single-threaded, sequential pipeline execution — no parallel middleware).
public sealed class TransitionContext
{
    public TransitionCommand Command { get; }
    public MovieStatus CurrentStatus { get; set; }
    public List<ConflictResult> ConflictResults { get; } = new();
    public OverrideResult? OverrideResult { get; set; }
    public bool IsAborted { get; private set; }
    public TransitionResult? AbortResult { get; private set; }

    public TransitionContext(TransitionCommand command, MovieStatus currentStatus)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        CurrentStatus = currentStatus;
    }

    // WHY: Abort is the short-circuit mechanism. Once called, pipeline execution
    // should stop — each middleware checks IsAborted before proceeding.
    // Mirrors HttpContext.Response.HasStarted in ASP.NET Core: once the response
    // has started, you cannot change it.
    public void Abort(TransitionResult result)
    {
        IsAborted = true;
        AbortResult = result;
    }
}

// WHY: Mirrors RequestDelegate in ASP.NET Core. A composed pipeline delegate
// that processes a TransitionContext. Individual middleware steps capture their
// "next" delegate via closure when the pipeline is built, exactly like ASP.NET
// Core middleware captures RequestDelegate in its constructor.
public delegate Task TransitionDelegate(TransitionContext context, CancellationToken ct);
