// ============================================================================
// SECTION 1 — Interface Signatures: TransitionResult (OneOf-style struct)
// ============================================================================

namespace MovieLifecycle.Types;

// WHY: Struct avoids heap allocation on the success hot path (most transitions succeed).
// OneOf-style discriminator with Match<T> forces exhaustive branch handling at every
// call site, eliminating null-check bugs that plague Try* and nullable-return patterns.
// Trade-off: struct may box in async Task<TransitionResult> contexts, negating the
// allocation benefit — but the exhaustive-handling guarantee remains valuable.
public readonly struct TransitionResult
{
    private enum Tag : byte { Uninitialized = 0, Success = 1, Failure = 2 }

    private readonly Tag _tag;
    private readonly MovieStatus _success;
    private readonly TransitionError? _failure;

    private TransitionResult(MovieStatus success)
    {
        _tag = Tag.Success;
        _success = success;
        _failure = null;
    }

    private TransitionResult(TransitionError failure)
    {
        _tag = Tag.Failure;
        _success = default;
        _failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    // WHY: Implicit conversions let middleware return bare values without ceremony:
    //   context.Abort(MovieStatus.Registered);
    //   context.Abort(new TransitionError("CODE", "msg", []));
    // The compiler infers the conversion, keeping middleware code terse.
    public static implicit operator TransitionResult(MovieStatus status) => new(status);
    public static implicit operator TransitionResult(TransitionError error) => new(error);

    public bool IsSuccess => _tag == Tag.Success;
    public bool IsFailure => _tag == Tag.Failure;

    // WHY: Match forces callers to handle both branches at compile time.
    // Unlike if/else on IsSuccess, you cannot accidentally forget the failure path.
    // The Func<..., T> return type means the compiler warns about unreachable code
    // if you try to return incompatible types from the two branches.
    public T Match<T>(
        Func<MovieStatus, T> onSuccess,
        Func<TransitionError, T> onFailure) =>
        _tag switch
        {
            Tag.Success => onSuccess(_success),
            Tag.Failure => onFailure(_failure!),
            _ => throw new InvalidOperationException(
                "TransitionResult was not initialized. " +
                "Use implicit conversion or Match only on results returned from the pipeline.")
        };
}
