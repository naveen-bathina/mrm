using System.Collections.Immutable;

namespace MovieReleaseManager.Movies.Lifecycle;

// ──────────────────────────────────────────────────────────────────
//  Movie Status — the states a movie can occupy
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// The lifecycle states a movie progresses through.
/// Valid forward transitions:
///   Draft → UnderReview → Approved → Released
/// Lateral/terminal transitions:
///   UnderReview → Rejected
///   Draft | UnderReview | Approved → Withdrawn
/// </summary>
public enum MovieStatus
{
    Draft,
    UnderReview,
    Approved,
    Released,
    Rejected,
    Withdrawn
}

// ──────────────────────────────────────────────────────────────────
//  Commands — callers build one of these, the module does the rest
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base for every lifecycle command dispatched to
/// <see cref="IMovieLifecycleService"/>. Each concrete command
/// carries only the data that transition needs — the service
/// resolves state, conflicts, and audit internally.
/// </summary>
public abstract record MovieCommand(Guid MovieId, Guid ActorUserId);

/// <summary>Submit a Draft movie for review. Runs TitleConflictChecker and PersonScheduleConflictChecker.</summary>
public sealed record SubmitForReviewCommand(Guid MovieId, Guid ActorUserId)
    : MovieCommand(MovieId, ActorUserId);

/// <summary>Approve a movie that is UnderReview. Runs ReleaseConflictChecker and PersonScheduleConflictChecker.</summary>
public sealed record ApproveCommand(Guid MovieId, Guid ActorUserId)
    : MovieCommand(MovieId, ActorUserId);

/// <summary>Reject a movie that is UnderReview. No conflict checks.</summary>
public sealed record RejectCommand(Guid MovieId, Guid ActorUserId, string Reason)
    : MovieCommand(MovieId, ActorUserId);

/// <summary>Release an Approved movie. No conflict checks.</summary>
public sealed record ReleaseCommand(Guid MovieId, Guid ActorUserId)
    : MovieCommand(MovieId, ActorUserId);

/// <summary>Withdraw a movie from Draft, UnderReview, or Approved. No conflict checks.</summary>
public sealed record WithdrawCommand(Guid MovieId, Guid ActorUserId, string Reason)
    : MovieCommand(MovieId, ActorUserId);

/// <summary>
/// System Admin override: force a transition that was previously
/// hard-blocked by conflicts. <paramref name="OverrideReason"/> is mandatory
/// and is persisted verbatim in the audit log.
/// </summary>
public sealed record AdminOverrideCommand(
    Guid MovieId,
    Guid ActorUserId,
    MovieStatus TargetStatus,
    string OverrideReason
) : MovieCommand(MovieId, ActorUserId);

// ──────────────────────────────────────────────────────────────────
//  Result hierarchy — discriminated-union style
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// Severity of a detected conflict. <see cref="Hard"/> blocks prevent
/// the transition; <see cref="Soft"/> warnings are informational only.
/// </summary>
public enum ConflictSeverity { Hard, Soft }

/// <summary>
/// A single conflict surfaced during a lifecycle transition attempt.
/// Callers see the type and a human-readable detail but never the
/// internal checker that produced it.
/// </summary>
public sealed record ConflictItem(
    string Type,
    ConflictSeverity Severity,
    string Detail
);

/// <summary>
/// Base result returned by <see cref="IMovieLifecycleService.ExecuteAsync"/>.
/// Pattern-match on the concrete sealed subtypes to handle each outcome.
/// </summary>
public abstract record TransitionResult;

/// <summary>
/// The transition succeeded. <see cref="NewStatus"/> is the movie's
/// current status. <see cref="Warnings"/> contains any soft conflicts
/// that did not block the transition (may be empty).
/// </summary>
public sealed record TransitionSucceeded(
    Guid MovieId,
    MovieStatus PreviousStatus,
    MovieStatus NewStatus,
    ImmutableArray<ConflictItem> Warnings
) : TransitionResult;

/// <summary>
/// The transition was blocked by one or more hard conflicts.
/// Callers should display <see cref="Conflicts"/> to the user.
/// A System Admin can retry via <see cref="AdminOverrideCommand"/>.
/// </summary>
public sealed record TransitionBlocked(
    Guid MovieId,
    MovieStatus CurrentStatus,
    MovieStatus AttemptedStatus,
    ImmutableArray<ConflictItem> Conflicts
) : TransitionResult;

/// <summary>
/// The requested transition is not valid from the movie's current
/// status (e.g., Draft → Released).
/// </summary>
public sealed record InvalidTransition(
    Guid MovieId,
    MovieStatus CurrentStatus,
    MovieStatus AttemptedStatus,
    string Reason
) : TransitionResult;

/// <summary>
/// The movie referenced by <see cref="MovieId"/> was not found.
/// </summary>
public sealed record MovieNotFound(Guid MovieId) : TransitionResult;

/// <summary>
/// The command itself was malformed (e.g., AdminOverride with a
/// blank reason, or Reject without a reason).
/// </summary>
public sealed record ValidationFailed(
    ImmutableArray<string> Errors
) : TransitionResult;

// ──────────────────────────────────────────────────────────────────
//  The interface — one method to rule them all
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// Single entry-point for every movie lifecycle operation. Internally
/// owns the state machine, conflict-checker orchestration (title,
/// release-date, person-schedule), override policy, and audit logging.
/// <para>
/// Callers construct a <see cref="MovieCommand"/> subtype and
/// pattern-match on the returned <see cref="TransitionResult"/>.
/// All database access, conflict resolution, and auditing happen
/// behind this boundary — callers never touch EF Core, checkers,
/// or the audit log directly.
/// </para>
/// </summary>
public interface IMovieLifecycleService
{
    /// <summary>
    /// Execute a lifecycle command against a movie.
    /// </summary>
    /// <param name="command">
    /// A concrete <see cref="MovieCommand"/> subtype describing the
    /// desired transition or override.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="TransitionResult"/> subtype indicating success,
    /// block, invalid transition, not-found, or validation failure.
    /// </returns>
    Task<TransitionResult> ExecuteAsync(
        MovieCommand command,
        CancellationToken cancellationToken = default);
}
