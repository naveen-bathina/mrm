// ============================================================================
// SECTION 1 — Interface Signatures: TransitionCommand hierarchy + Visitor
// ============================================================================

namespace MovieLifecycle.Types;

// WHY: Sealed class hierarchy with private constructor prevents external subclassing.
// Static factories enforce invariants at construction time: Override always has a non-empty
// reason, Standard never does. This eliminates an entire class of runtime validation bugs.
// The visitor pattern enables type-safe dispatch without downcasting or type-checking.
public abstract class TransitionCommand
{
    public Guid MovieId { get; }
    public MovieStatus TargetStatus { get; }

    // WHY: Private constructor blocks external subclassing — only nested types can inherit.
    // This creates a closed type hierarchy that the visitor can enumerate exhaustively.
    private TransitionCommand(Guid movieId, MovieStatus targetStatus)
    {
        MovieId = movieId;
        TargetStatus = targetStatus;
    }

    // WHY: Accept/visitor double-dispatch lets the pipeline determine TransitionMode
    // and extract override reasons without any downcasting, is-checks, or switch expressions.
    // Every consumer is forced to handle all command types — missing a branch is a compile error.
    public abstract TResult Accept<TResult>(ITransitionCommandVisitor<TResult> visitor);

    // ── Static Factories ─────────────────────────────────────────────────────

    // WHY: Only way to construct commands. No public constructors exist.
    // Standard() enforces that standard transitions carry zero override state.
    public static TransitionCommand Standard(Guid movieId, MovieStatus targetStatus)
        => new StandardTransitionCommand(movieId, targetStatus);

    // WHY: Override() enforces non-null/non-whitespace reason at construction site.
    // Callers cannot create an override command and "forget" the reason — the compiler
    // requires the parameter, and the runtime rejects empty/whitespace strings.
    public static TransitionCommand Override(Guid movieId, MovieStatus targetStatus, string reason)
        => new OverrideTransitionCommand(movieId, targetStatus, reason);

    // ── Nested Sealed Command Types ──────────────────────────────────────────

    // WHY: Nested sealed classes form a closed set. Adding a new command type
    // requires adding a new Visit overload to ITransitionCommandVisitor<TResult>,
    // which is a compile-time breaking change on every visitor implementation.
    // This prevents silent "default case" bugs when the command set evolves.

    public sealed class StandardTransitionCommand : TransitionCommand
    {
        internal StandardTransitionCommand(Guid movieId, MovieStatus targetStatus)
            : base(movieId, targetStatus) { }

        public override TResult Accept<TResult>(ITransitionCommandVisitor<TResult> visitor)
            => visitor.Visit(this);
    }

    public sealed class OverrideTransitionCommand : TransitionCommand
    {
        public string Reason { get; }

        internal OverrideTransitionCommand(Guid movieId, MovieStatus targetStatus, string reason)
            : base(movieId, targetStatus)
        {
            // WHY: Fail-fast on construction, not deep inside the pipeline.
            // This shifts validation left — the caller gets an immediate exception
            // at the construction site rather than a mysterious pipeline failure.
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "Override reason must be provided and non-whitespace.", nameof(reason));
            Reason = reason;
        }

        public override TResult Accept<TResult>(ITransitionCommandVisitor<TResult> visitor)
            => visitor.Visit(this);
    }
}

// WHY: Visitor forces every consumer to handle all command types exhaustively.
// Covariant TResult (out) enables variance: a visitor returning `object` satisfies
// a call expecting a visitor returning a more specific type.
public interface ITransitionCommandVisitor<out TResult>
{
    TResult Visit(TransitionCommand.StandardTransitionCommand command);
    TResult Visit(TransitionCommand.OverrideTransitionCommand command);
}
