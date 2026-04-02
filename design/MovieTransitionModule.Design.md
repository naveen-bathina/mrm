# IMovieTransitionModule — Explicit Pipeline Design

## 1. Interface Signature

```csharp
using System.Security.Claims;

namespace MovieReleaseManager.Transitions;

// ═══════════════════════════════════════════════════════════════════
//  (a) Enums
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// The lifecycle states a movie progresses through.
/// Transitions are governed by <see cref="IStateMachinePolicy"/>.
/// </summary>
public enum MovieStatus
{
    Draft,
    Registered,
    InProduction,
    PostProduction,
    Released,
    Archived,
    Cancelled
}

/// <summary>
/// Tracks the current position within the transition pipeline.
/// Updated on <see cref="TransitionContext"/> as each stage completes,
/// and carried on every <see cref="TransitionResult"/> subtype for diagnostics.
/// </summary>
public enum TransitionPipelineStage
{
    Initiated,
    IntentValidation,
    StateMachineCheck,
    ConflictDetection,
    OverrideEvaluation,
    PersistingTransition,
    AuditWrite,
    Completed
}

/// <summary>
/// Categorizes why a transition was blocked. Determines the appropriate
/// HTTP status code and client-recovery strategy.
/// </summary>
public enum BlockReason
{
    InsufficientPermissions,
    InvalidStateTransition,
    HardConflictRequiresOverride,
    BusinessRuleViolation,
    ConcurrencyConflict
}

// ═══════════════════════════════════════════════════════════════════
//  (b) ValidationResult
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Lightweight value type returned by <see cref="TransitionCommand.ValidateIntent"/>
/// to report whether a command's own invariants are satisfied before
/// the pipeline invests in database work.
/// </summary>
public readonly record struct ValidationResult(bool IsValid, string? ErrorMessage)
{
    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string message) => new(false, message);
}

// ═══════════════════════════════════════════════════════════════════
//  (c) TransitionCommand hierarchy
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract base for all transition commands. Each subtype carries the data
/// specific to its intent and validates its own invariants via
/// <see cref="ValidateIntent"/>. The pipeline calls this before touching the database.
/// </summary>
public abstract class TransitionCommand
{
    /// <summary>The unique identifier of the movie to transition.</summary>
    public required Guid MovieId { get; init; }

    /// <summary>The desired target lifecycle status.</summary>
    public required MovieStatus TargetStatus { get; init; }

    /// <summary>
    /// Validates the command's own structural invariants (non-empty IDs,
    /// reason length, etc.) before the pipeline invests in database work.
    /// </summary>
    public abstract ValidationResult ValidateIntent();
}

/// <summary>
/// A standard, policy-enforced transition. If conflict checkers detect hard blocks
/// the transition fails — no bypass pathway exists from this command type.
/// </summary>
public sealed class StandardTransitionCommand : TransitionCommand
{
    /// <inheritdoc />
    public override ValidationResult ValidateIntent()
    {
        if (MovieId == Guid.Empty)
            return ValidationResult.Fail("MovieId must not be empty.");

        return ValidationResult.Ok();
    }
}

/// <summary>
/// An explicit override transition that bypasses hard conflict blocks.
/// Requires a non-null reason string of at least 10 characters, validated
/// eagerly in the constructor so that an invalid override can never be
/// represented as a value.
/// </summary>
public sealed class OverrideTransitionCommand : TransitionCommand
{
    /// <summary>
    /// The mandatory justification for bypassing conflict blocks.
    /// Persisted verbatim in the audit log.
    /// </summary>
    public string OverrideReason { get; }

    /// <param name="overrideReason">
    /// Mandatory free-text justification. Must be non-null and at least 10 characters
    /// after trimming. Persisted verbatim in the audit log.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="overrideReason"/> is null, empty, or fewer
    /// than 10 characters after trimming.
    /// </exception>
    public OverrideTransitionCommand(string overrideReason)
    {
        if (overrideReason is null || overrideReason.Trim().Length < 10)
            throw new ArgumentException(
                "Override reason must be non-null and at least 10 characters.",
                nameof(overrideReason));

        OverrideReason = overrideReason.Trim();
    }

    /// <inheritdoc />
    public override ValidationResult ValidateIntent()
    {
        if (MovieId == Guid.Empty)
            return ValidationResult.Fail("MovieId must not be empty.");

        if (string.IsNullOrWhiteSpace(OverrideReason) || OverrideReason.Trim().Length < 10)
            return ValidationResult.Fail("Override reason must be at least 10 characters.");

        return ValidationResult.Ok();
    }
}

// ═══════════════════════════════════════════════════════════════════
//  (d) TransitionContext
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable envelope wrapping the command and the authenticated actor.
/// The pipeline creates new instances via <see cref="AdvanceTo"/> as each
/// stage completes, producing a traceable breadcrumb trail without mutation.
/// </summary>
public sealed record TransitionContext
{
    /// <summary>The command describing the desired transition.</summary>
    public required TransitionCommand Command { get; init; }

    /// <summary>
    /// The authenticated principal whose claims are inspected for
    /// authorization decisions at each pipeline stage.
    /// </summary>
    public required ClaimsPrincipal Actor { get; init; }

    /// <summary>
    /// The pipeline stage most recently entered. Defaults to
    /// <see cref="TransitionPipelineStage.Initiated"/> at construction time.
    /// </summary>
    public TransitionPipelineStage CurrentStage { get; init; } = TransitionPipelineStage.Initiated;

    /// <summary>
    /// Returns a copy of this context with <see cref="CurrentStage"/> advanced
    /// to the specified <paramref name="stage"/>. The original instance is unchanged.
    /// </summary>
    public TransitionContext AdvanceTo(TransitionPipelineStage stage) => this with { CurrentStage = stage };
}

// ═══════════════════════════════════════════════════════════════════
//  (e) ConflictReport hierarchy
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Closed date interval used by <see cref="PersonScheduleConflictReport"/>
/// to express a conflicting scheduling period.
/// </summary>
public sealed record DateRange(DateOnly Start, DateOnly End);

/// <summary>
/// Abstract base for all conflict reports. Polymorphic — each conflict checker
/// produces a specific subtype carrying type-specific diagnostic data.
/// </summary>
public abstract record ConflictReport
{
    /// <summary>The identifier of the movie that caused the conflict.</summary>
    public required Guid ConflictingMovieId { get; init; }

    /// <summary>
    /// When <c>true</c>, this conflict is a hard block that prevents the transition
    /// unless overridden. When <c>false</c>, it is an advisory soft warning.
    /// </summary>
    public required bool IsHardBlock { get; init; }

    /// <summary>Human-readable summary of the conflict suitable for display.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Raised by the title conflict checker when a movie with a confusingly similar
/// or identical normalized title exists in the same release year.
/// </summary>
public sealed record TitleConflictReport : ConflictReport
{
    /// <summary>The conflicting movie's title as registered.</summary>
    public required string ConflictingTitle { get; init; }

    /// <summary>
    /// Similarity score between 0.0 (completely different) and 1.0 (exact match
    /// after normalization). Scores above the configured threshold trigger a conflict.
    /// </summary>
    public required double SimilarityScore { get; init; }
}

/// <summary>
/// Raised by the release conflict checker when another movie occupies the same
/// distribution territory on the same release date.
/// </summary>
public sealed record ReleaseConflictReport : ConflictReport
{
    /// <summary>The conflicting release date in the contested territory.</summary>
    public required DateOnly ConflictingReleaseDate { get; init; }

    /// <summary>The title of the movie already scheduled for that date.</summary>
    public required string ConflictingMovieTitle { get; init; }

    /// <summary>The distribution territory code (e.g. "US", "DE", "JP").</summary>
    public required string Market { get; init; }
}

/// <summary>
/// Raised by the person schedule conflict checker when a cast or crew member
/// is double-booked across overlapping shoot periods on different productions.
/// </summary>
public sealed record PersonScheduleConflictReport : ConflictReport
{
    /// <summary>The unique identifier of the person who is double-booked.</summary>
    public required Guid PersonId { get; init; }

    /// <summary>The full name of the double-booked person.</summary>
    public required string PersonName { get; init; }

    /// <summary>The role type the person holds on the conflicting production (e.g. "Actor", "Director").</summary>
    public required string Role { get; init; }

    /// <summary>The date range on the other production that overlaps with this movie's schedule.</summary>
    public required DateRange ConflictingPeriod { get; init; }
}

// ═══════════════════════════════════════════════════════════════════
//  (f) TransitionResult hierarchy
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Abstract base for all transition outcomes. Railway-oriented — every possible
/// outcome is a distinct subtype. Callers must pattern-match; there are no boolean
/// flags, no nullable fields on a single class.
/// </summary>
public abstract record TransitionResult
{
    /// <summary>
    /// The pipeline stage that was active when this result was produced.
    /// Enables diagnostics and correlating failures to specific pipeline phases.
    /// </summary>
    public required TransitionPipelineStage StageAtCompletion { get; init; }

    /// <summary>
    /// The transition completed successfully. The movie's status has been
    /// persisted and an audit entry has been written.
    /// </summary>
    public sealed record TransitionSucceeded : TransitionResult
    {
        /// <summary>The movie that was transitioned.</summary>
        public required Guid MovieId { get; init; }

        /// <summary>The status the movie held before this transition.</summary>
        public required MovieStatus PreviousStatus { get; init; }

        /// <summary>The status the movie now holds.</summary>
        public required MovieStatus NewStatus { get; init; }

        /// <summary>UTC timestamp when the transition was persisted.</summary>
        public required DateTimeOffset OccurredAt { get; init; }

        /// <summary>The unique identifier of the audit log entry for this transition.</summary>
        public required Guid AuditEntryId { get; init; }
    }

    /// <summary>
    /// One or more conflict checkers detected issues with the proposed transition.
    /// Inspect <see cref="Conflicts"/> for details. If <see cref="HasHardBlocks"/>
    /// is <c>true</c>, the caller may retry with an <see cref="OverrideTransitionCommand"/>.
    /// </summary>
    public sealed record ConflictDetected : TransitionResult
    {
        /// <summary>All conflicts detected during the conflict-detection phase.</summary>
        public required IReadOnlyList<ConflictReport> Conflicts { get; init; }

        /// <summary>The target status that was attempted when conflicts were detected.</summary>
        public required MovieStatus AttemptedTargetStatus { get; init; }

        /// <summary>
        /// Computed property: <c>true</c> when at least one conflict in
        /// <see cref="Conflicts"/> is a hard block that prevents the transition.
        /// </summary>
        public bool HasHardBlocks => Conflicts.Any(c => c.IsHardBlock);
    }

    /// <summary>
    /// The transition was blocked for a policy reason unrelated to conflict detection
    /// (e.g. insufficient permissions, invalid state machine edge, concurrency conflict).
    /// </summary>
    public sealed record TransitionBlocked : TransitionResult
    {
        /// <summary>The category of block that prevented the transition.</summary>
        public required BlockReason Reason { get; init; }

        /// <summary>Human-readable explanation suitable for logging and display.</summary>
        public required string Message { get; init; }
    }

    /// <summary>
    /// The command's own <see cref="TransitionCommand.ValidateIntent"/> check
    /// failed before the pipeline performed any database work.
    /// </summary>
    public sealed record IntentInvalid : TransitionResult
    {
        /// <summary>The validation error message from the command's self-check.</summary>
        public required string ValidationError { get; init; }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  (g) Five sub-interfaces
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Encodes the legal state machine graph — which transitions are permitted
/// between statuses. Implementations may use a static adjacency table or
/// load rules from configuration.
/// </summary>
public interface IStateMachinePolicy
{
    /// <summary>
    /// Returns <c>true</c> if the transition from <paramref name="from"/>
    /// to <paramref name="to"/> is a legal edge in the state machine graph.
    /// </summary>
    bool IsTransitionAllowed(MovieStatus from, MovieStatus to);

    /// <summary>
    /// Returns all statuses that <paramref name="from"/> may legally transition to.
    /// Used by preview endpoints and admin tooling.
    /// </summary>
    IReadOnlyList<MovieStatus> GetAllowedTransitions(MovieStatus from);
}

/// <summary>
/// Decides which conflict checker types must run for a given transition.
/// The mapping is internal policy — callers never see this routing table.
/// For example, Draft → Registered runs <c>TitleConflictChecker</c>;
/// InProduction → PostProduction runs <c>PersonScheduleConflictChecker</c>.
/// </summary>
public interface IConflictPolicy
{
    /// <summary>
    /// Returns the ordered set of conflict checker types that must execute
    /// for the specified transition. The returned types are resolved from DI
    /// as keyed services by <see cref="IConflictExecutor"/>.
    /// </summary>
    IReadOnlyList<Type> GetRequiredCheckers(MovieStatus from, MovieStatus to);
}

/// <summary>
/// Resolves and executes the conflict checker implementations for a transition.
/// Implementations typically resolve keyed services from DI and aggregate results.
/// </summary>
public interface IConflictExecutor
{
    /// <summary>
    /// Runs the specified conflict checker types against the given movie and
    /// target status. Returns all detected conflicts, both hard and soft.
    /// </summary>
    /// <param name="movieId">The movie being transitioned.</param>
    /// <param name="targetStatus">The target status being attempted.</param>
    /// <param name="checkerTypes">
    /// The conflict checker types to execute, as returned by
    /// <see cref="IConflictPolicy.GetRequiredCheckers"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An ordered list of all conflicts found. May be empty if no conflicts exist.
    /// </returns>
    Task<IReadOnlyList<ConflictReport>> RunChecksAsync(
        Guid movieId,
        MovieStatus targetStatus,
        IReadOnlyList<Type> checkerTypes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Evaluates whether an override command is permitted given the detected conflicts
/// and the actor's claims. Centralizes all override authorization logic.
/// </summary>
public interface IOverridePolicy
{
    /// <summary>
    /// Returns <c>true</c> if the actor's claims include the required role or
    /// permission to issue overrides, regardless of the specific conflicts.
    /// </summary>
    bool ActorCanOverride(ClaimsPrincipal actor);

    /// <summary>
    /// Evaluates the full override request — the actor's permissions, the specific
    /// conflicts being bypassed, and the command's override reason — and returns
    /// a permit/deny decision with an explanation.
    /// </summary>
    OverrideEvaluation Evaluate(
        IReadOnlyList<ConflictReport> conflicts,
        OverrideTransitionCommand command,
        ClaimsPrincipal actor);
}

/// <summary>
/// Result of an <see cref="IOverridePolicy.Evaluate"/> call.
/// </summary>
public sealed record OverrideEvaluation(bool Permitted, string? DenialReason)
{
    /// <summary>Creates a permitted override evaluation.</summary>
    public static OverrideEvaluation Allow() => new(true, null);

    /// <summary>Creates a denied override evaluation with the specified reason.</summary>
    public static OverrideEvaluation Deny(string reason) => new(false, reason);
}

/// <summary>
/// Writes immutable audit records for transitions and overrides.
/// Implementations may target PostgreSQL via EF Core, an event store,
/// or a message broker — the pipeline is agnostic.
/// </summary>
public interface ITransitionAuditWriter
{
    /// <summary>
    /// Records a standard (non-override) transition in the audit log.
    /// </summary>
    /// <param name="movieId">The movie that was transitioned.</param>
    /// <param name="actor">The principal who performed the transition.</param>
    /// <param name="fromStatus">The status before the transition.</param>
    /// <param name="toStatus">The status after the transition.</param>
    /// <param name="completedStage">The pipeline stage at which the transition was finalized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unique identifier of the created audit entry.</returns>
    Task<Guid> RecordTransitionAsync(
        Guid movieId,
        ClaimsPrincipal actor,
        MovieStatus fromStatus,
        MovieStatus toStatus,
        TransitionPipelineStage completedStage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records an override transition in the audit log, capturing the bypassed
    /// conflicts and the actor's justification.
    /// </summary>
    /// <param name="movieId">The movie that was transitioned via override.</param>
    /// <param name="actor">The principal who performed the override.</param>
    /// <param name="fromStatus">The status before the override transition.</param>
    /// <param name="toStatus">The status after the override transition.</param>
    /// <param name="overrideReason">The mandatory justification supplied by the actor.</param>
    /// <param name="bypassedConflicts">The conflicts that were bypassed by this override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unique identifier of the created audit entry.</returns>
    Task<Guid> RecordOverrideAsync(
        Guid movieId,
        ClaimsPrincipal actor,
        MovieStatus fromStatus,
        MovieStatus toStatus,
        string overrideReason,
        IReadOnlyList<ConflictReport> bypassedConflicts,
        CancellationToken cancellationToken);
}

// ═══════════════════════════════════════════════════════════════════
//  (h) IMovieTransitionModule — the orchestrating facade
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Orchestrates the full movie status transition pipeline:
/// intent validation → state machine check → conflict detection →
/// override evaluation (<see cref="OverrideTransitionCommand"/> only) → persist → audit write.
/// </summary>
/// <remarks>
/// <para>
/// All five pipeline stages are delegated to injected sub-interfaces:
/// <see cref="IStateMachinePolicy"/>, <see cref="IConflictPolicy"/>,
/// <see cref="IConflictExecutor"/>, <see cref="IOverridePolicy"/>,
/// and <see cref="ITransitionAuditWriter"/>.
/// </para>
/// <para>
/// The orchestrator never accesses the database directly. All persistence
/// is delegated to the injected sub-interfaces, keeping the pipeline logic
/// pure and independently testable.
/// </para>
/// </remarks>
public interface IMovieTransitionModule
{
    /// <summary>
    /// Executes a status transition for the movie identified by
    /// <see cref="TransitionContext.Command"/>.<see cref="TransitionCommand.MovieId"/>.
    /// </summary>
    /// <param name="context">
    /// The transition context wrapping the command and the authenticated actor.
    /// The module enforces its own authorization by inspecting the actor's claims
    /// at the appropriate pipeline stage — callers do not need to pre-check permissions.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>
    /// A <see cref="TransitionResult"/> subtype indicating the outcome:
    /// <list type="bullet">
    ///   <item><see cref="TransitionResult.TransitionSucceeded"/> — the transition was persisted and audited.</item>
    ///   <item><see cref="TransitionResult.ConflictDetected"/> — one or more conflicts were found;
    ///         inspect <see cref="TransitionResult.ConflictDetected.HasHardBlocks"/> to determine
    ///         whether an override is possible.</item>
    ///   <item><see cref="TransitionResult.TransitionBlocked"/> — the transition was blocked by
    ///         policy (permissions, invalid state edge, concurrency).</item>
    ///   <item><see cref="TransitionResult.IntentInvalid"/> — the command failed its own
    ///         structural validation before any database work.</item>
    /// </list>
    /// </returns>
    Task<TransitionResult> ExecuteAsync(TransitionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Previews which conflicts would be raised for a hypothetical transition
    /// from the movie's current status to <paramref name="targetStatus"/>,
    /// without persisting or auditing anything.
    /// </summary>
    /// <param name="movieId">The movie to preview conflicts for.</param>
    /// <param name="targetStatus">The hypothetical target status to check against.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <returns>
    /// All conflicts that would be detected for this transition. Returns an empty
    /// list if no conflicts exist. Does not perform authorization checks — the
    /// caller is responsible for gating access to this endpoint.
    /// </returns>
    Task<IReadOnlyList<ConflictReport>> PreviewConflictsAsync(
        Guid movieId,
        MovieStatus targetStatus,
        CancellationToken cancellationToken);
}
```

---

## 2. Controller Usage

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieReleaseManager.Transitions;

namespace MovieReleaseManager.Api.Controllers;

// ── Request DTOs ─────────────────────────────────────────────────

/// <summary>DTO for a standard (non-override) transition request.</summary>
public sealed record TransitionRequest
{
    public required MovieStatus TargetStatus { get; init; }
}

/// <summary>DTO for an override transition request with mandatory justification.</summary>
public sealed record OverrideTransitionRequest
{
    public required MovieStatus TargetStatus { get; init; }
    public required string OverrideReason { get; init; }
}

// ── Controller ───────────────────────────────────────────────────

[ApiController]
[Route("api/movies/{movieId:guid}/transitions")]
[Authorize]
public sealed class MovieTransitionsController : ControllerBase
{
    private readonly IMovieTransitionModule _transitions;

    public MovieTransitionsController(IMovieTransitionModule transitions)
    {
        _transitions = transitions;
    }

    /// <summary>
    /// Performs a standard lifecycle transition for the specified movie.
    /// Hard conflicts block the transition; soft conflicts are returned
    /// with a hint to retry using the override endpoint.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TransitionResult.TransitionSucceeded), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionResult.ConflictDetected), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TransitionResult.ConflictDetected), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(TransitionResult.TransitionBlocked), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(TransitionResult.IntentInvalid), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TransitionAsync(
        Guid movieId,
        [FromBody] TransitionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new StandardTransitionCommand
        {
            MovieId = movieId,
            TargetStatus = request.TargetStatus
        };

        var context = new TransitionContext
        {
            Command = command,
            Actor = User
        };

        var result = await _transitions.ExecuteAsync(context, cancellationToken);

        return MapResult(result);
    }

    /// <summary>
    /// Performs an override transition, bypassing hard conflict blocks.
    /// Requires System Admin privileges and a written justification of at least 10 characters.
    /// The override reason is persisted in the audit log.
    /// </summary>
    [HttpPost("override")]
    [ProducesResponseType(typeof(TransitionResult.TransitionSucceeded), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionResult.ConflictDetected), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TransitionResult.ConflictDetected), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(TransitionResult.TransitionBlocked), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(TransitionResult.IntentInvalid), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> OverrideTransitionAsync(
        Guid movieId,
        [FromBody] OverrideTransitionRequest request,
        CancellationToken cancellationToken)
    {
        OverrideTransitionCommand command;
        try
        {
            command = new OverrideTransitionCommand(request.OverrideReason)
            {
                MovieId = movieId,
                TargetStatus = request.TargetStatus
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }

        var context = new TransitionContext
        {
            Command = command,
            Actor = User
        };

        var result = await _transitions.ExecuteAsync(context, cancellationToken);

        return MapResult(result);
    }

    private IActionResult MapResult(TransitionResult result) => result switch
    {
        TransitionResult.TransitionSucceeded succeeded => Ok(new
        {
            succeeded.MovieId,
            PreviousStatus = succeeded.PreviousStatus.ToString(),
            NewStatus = succeeded.NewStatus.ToString(),
            succeeded.OccurredAt,
            succeeded.AuditEntryId,
            succeeded.StageAtCompletion
        }),

        TransitionResult.ConflictDetected { HasHardBlocks: false } softOnly => Conflict(new
        {
            Conflicts = softOnly.Conflicts.Select(c => new
            {
                c.ConflictingMovieId,
                c.IsHardBlock,
                c.Description,
                Type = c.GetType().Name
            }),
            AttemptedTargetStatus = softOnly.AttemptedTargetStatus.ToString(),
            softOnly.StageAtCompletion,
            Hint = "All conflicts are soft warnings. Retry with override endpoint if needed."
        }),

        TransitionResult.ConflictDetected hardBlocks => UnprocessableEntity(new
        {
            Conflicts = hardBlocks.Conflicts.Select(c => new
            {
                c.ConflictingMovieId,
                c.IsHardBlock,
                c.Description,
                Type = c.GetType().Name
            }),
            AttemptedTargetStatus = hardBlocks.AttemptedTargetStatus.ToString(),
            hardBlocks.StageAtCompletion,
            Hint = "Hard blocks detected. Use the override endpoint with a System Admin account and a written justification."
        }),

        TransitionResult.TransitionBlocked blocked => StatusCode(
            StatusCodes.Status403Forbidden,
            new
            {
                Reason = blocked.Reason.ToString(),
                blocked.Message,
                blocked.StageAtCompletion
            }),

        TransitionResult.IntentInvalid invalid => BadRequest(new
        {
            invalid.ValidationError,
            invalid.StageAtCompletion
        }),

        // CS8509 fires here if a new TransitionResult subtype is added without being handled
        _ => throw new UnreachableException(
            $"Unhandled TransitionResult subtype: {result.GetType().Name}. " +
            "A new result type was added to the hierarchy without updating the controller switch expression.")
    };
}
```

---

## 3. What Complexity It Hides

Callers never see, touch, or configure any of the following:

- **State machine graph traversal** — the full adjacency table of legal transitions (Draft→Registered, Registered→InProduction, etc.) and the rejection of illegal edges (Draft→Released). The `IStateMachinePolicy` implementation owns this graph; callers just specify a target status and get back `TransitionBlocked` with `InvalidStateTransition` if the edge is illegal.

- **Per-transition conflict checker routing** — the internal policy table mapping each transition to its required conflict checkers (e.g. Draft→Registered runs `TitleConflictChecker`; InProduction→PostProduction runs `PersonScheduleConflictChecker`). `IConflictPolicy.GetRequiredCheckers` returns `Type` references that `IConflictExecutor` resolves from DI. Callers never know which checkers ran.

- **EF Core queries for each checker type** — `TitleConflictChecker` performs Unicode NFKD normalization and queries `normalizedTitle + releaseYear` with `EF.Functions.ILike`. `ReleaseConflictChecker` queries `MovieTerritoryRelease` for same-territory/same-date collisions. `PersonScheduleConflictChecker` uses PostgreSQL's `daterange` type with the `&&` overlap operator. Each checker is an internal implementation detail behind `IConflictExecutor`.

- **PostgreSQL-specific indexing assumptions** — the title checker assumes a unique index on `(normalizedTitle, releaseYear)`. The schedule checker assumes a GiST index on the `daterange` column of `ScheduleBlock`. The release checker assumes an index on `(territoryId, releaseDate)`. These are migration-level concerns invisible to API callers.

- **Role and claim parsing** — `IOverridePolicy.ActorCanOverride` inspects `ClaimsPrincipal` for `role` claims, `studioId` claims, and any custom authorization attributes. Callers pass `User` from the controller and never parse claims themselves.

- **Override eligibility logic** — the decision tree for whether an override is permitted: does the actor have the SystemAdmin role? Are any of the conflicts non-overridable? Is the override reason sufficiently detailed? `IOverridePolicy.Evaluate` encapsulates this logic and returns `OverrideEvaluation`.

- **Audit entry serialization** — `ITransitionAuditWriter` serializes the transition metadata, actor identity, override reason, and the full list of bypassed conflicts into the `AuditLog` table's JSON `detail` column. The serialization format, column layout, and indexing strategy are invisible.

- **Pipeline stage tracking** — `TransitionContext.CurrentStage` is advanced via `AdvanceTo()` at each pipeline step, and `TransitionPipelineStage StageAtCompletion` appears on every result subtype. Callers receive this for diagnostics but never manage it.

- **Concurrency handling** — the implementation uses EF Core's optimistic concurrency via a `RowVersion`/`xmin` column on the `Movie` entity. If a concurrent transition modifies the movie between the read and the save, a `DbUpdateConcurrencyException` is caught and mapped to `TransitionBlocked` with `ConcurrencyConflict`. Callers see a typed result, not an exception.

- **The difference between soft and hard conflicts internally** — the pipeline collects all conflict reports, partitions them by `IsHardBlock`, and decides the outcome. For `StandardTransitionCommand`, any hard block produces `ConflictDetected`. For `OverrideTransitionCommand`, hard blocks are evaluated against `IOverridePolicy` and potentially bypassed. Callers receive the pre-classified result without understanding this branching logic.

- **Cross-studio data scoping bypass** — conflict checkers must read all studios' data to detect global conflicts. Implementations call `IgnoreQueryFilters()` on the `DbContext` to bypass the tenant-scoping global filter, then re-apply scoping for the result data that gets surfaced. Callers never know this happens.

- **Transaction boundaries** — the status update, conflict re-verification, and audit log write all execute within a single `IDbContextTransaction`. If the audit write fails, the entire transition rolls back. Callers get an atomic result.

---

## 4. Dependency Strategy

### Why Scoped

`IMovieTransitionModule` is registered as **Scoped** because:
1. Its sub-interfaces (`IConflictExecutor`, `ITransitionAuditWriter`) depend on EF Core's `MrmDbContext`, which is scoped to the HTTP request by default.
2. The pipeline participates in a single unit-of-work per request — conflict checks and the audit write share the same `DbContext` instance and transaction.
3. Singleton registration would either capture a scoped `DbContext` (causing runtime errors) or require service-locator patterns to resolve it per-call.

### DbContext Flow

The `MovieTransitionModule` implementation does **not** take `MrmDbContext` as a constructor parameter. Only the infrastructure implementations do:

```
IMovieTransitionModule (MovieTransitionModule)
 ├── IStateMachinePolicy        → MovieStateMachinePolicy        (no DbContext — pure logic)
 ├── IConflictPolicy             → MovieConflictPolicy             (no DbContext — pure lookup table)
 ├── IConflictExecutor           → PostgresConflictExecutor        (takes MrmDbContext)
 │    └── resolves keyed services: TitleConflictChecker, ReleaseConflictChecker, PersonScheduleConflictChecker
 ├── IOverridePolicy             → SystemAdminOverridePolicy       (no DbContext — claim inspection only)
 └── ITransitionAuditWriter      → EfCoreTransitionAuditWriter     (takes MrmDbContext)
```

This means the orchestrator (`MovieTransitionModule`) is testable with in-memory fakes for all five sub-interfaces, and only the two infrastructure implementations need integration tests against a real PostgreSQL instance.

### Fluent DI Registration

```csharp
// Program.cs
builder.Services.AddMovieTransitions(b => b
    .UsePostgresConflictCheckers()
    .WithAuditLog());
```

### Full Implementation

```csharp
using Microsoft.Extensions.DependencyInjection;
using MovieReleaseManager.Transitions;

namespace MovieReleaseManager.Transitions.DependencyInjection;

/// <summary>
/// Extension methods for registering the movie transition pipeline in the DI container.
/// </summary>
public static class MovieTransitionServiceCollectionExtensions
{
    /// <summary>
    /// Registers all movie transition pipeline services using a fluent builder.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// A delegate that configures the builder. At minimum,
    /// <see cref="MovieTransitionBuilder.UsePostgresConflictCheckers"/>
    /// must be called before build completes.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMovieTransitions(
        this IServiceCollection services,
        Action<MovieTransitionBuilder> configure)
    {
        var builder = new MovieTransitionBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }
}

/// <summary>
/// Fluent builder for composing the movie transition pipeline's DI registrations.
/// Validates that all required infrastructure has been configured before finalizing.
/// </summary>
public sealed class MovieTransitionBuilder
{
    private readonly IServiceCollection _services;
    private bool _conflictCheckersConfigured;
    private bool _auditLogConfigured;

    internal MovieTransitionBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// Registers PostgreSQL-backed conflict checker implementations using EF Core
    /// and .NET 8 keyed services for checker resolution.
    /// </summary>
    public MovieTransitionBuilder UsePostgresConflictCheckers()
    {
        _services.AddScoped<IConflictExecutor, PostgresConflictExecutor>();

        _services.AddKeyedScoped<IConflictChecker, TitleConflictChecker>("TitleConflictChecker");
        _services.AddKeyedScoped<IConflictChecker, ReleaseConflictChecker>("ReleaseConflictChecker");
        _services.AddKeyedScoped<IConflictChecker, PersonScheduleConflictChecker>("PersonScheduleConflictChecker");

        _conflictCheckersConfigured = true;
        return this;
    }

    /// <summary>
    /// Registers the EF Core audit writer that persists transition and override
    /// records to the <c>AuditLog</c> table.
    /// </summary>
    public MovieTransitionBuilder WithAuditLog()
    {
        _services.AddScoped<ITransitionAuditWriter, EfCoreTransitionAuditWriter>();
        _auditLogConfigured = true;
        return this;
    }

    /// <summary>
    /// Validates that all required infrastructure has been configured and registers
    /// the orchestrator and policy services.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="UsePostgresConflictCheckers"/> was not called.
    /// Conflict checking is not optional — the pipeline cannot function without it.
    /// </exception>
    internal void Build()
    {
        if (!_conflictCheckersConfigured)
        {
            throw new InvalidOperationException(
                "A conflict checker implementation is required. " +
                "Call UsePostgresConflictCheckers() (or a custom conflict checker registration) " +
                "before building the movie transition pipeline.");
        }

        if (!_auditLogConfigured)
        {
            _services.AddScoped<ITransitionAuditWriter, NullTransitionAuditWriter>();
        }

        _services.AddScoped<IStateMachinePolicy, MovieStateMachinePolicy>();
        _services.AddScoped<IConflictPolicy, MovieConflictPolicy>();
        _services.AddScoped<IOverridePolicy, SystemAdminOverridePolicy>();
        _services.AddScoped<IMovieTransitionModule, MovieTransitionModule>();
    }
}

// ── Internal marker interface for keyed conflict checker resolution ───

/// <summary>
/// Marker interface for individual conflict checker implementations resolved
/// via .NET 8 keyed services from <see cref="IConflictExecutor"/>.
/// </summary>
internal interface IConflictChecker
{
    Task<IReadOnlyList<ConflictReport>> CheckAsync(
        Guid movieId,
        MovieStatus targetStatus,
        CancellationToken cancellationToken);
}

// ── Stub types referenced by builder (internal implementations) ──────

internal sealed class TitleConflictChecker : IConflictChecker
{
    private readonly MrmDbContext _db;
    public TitleConflictChecker(MrmDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConflictReport>> CheckAsync(
        Guid movieId, MovieStatus targetStatus, CancellationToken cancellationToken)
    {
        // Queries (normalizedTitle, releaseYear) with IgnoreQueryFilters()
        // to detect cross-studio title collisions. Uses EF.Functions.ILike
        // for case-insensitive comparison after NFKD normalization.
        await Task.CompletedTask;
        return Array.Empty<ConflictReport>();
    }
}

internal sealed class ReleaseConflictChecker : IConflictChecker
{
    private readonly MrmDbContext _db;
    public ReleaseConflictChecker(MrmDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConflictReport>> CheckAsync(
        Guid movieId, MovieStatus targetStatus, CancellationToken cancellationToken)
    {
        // Queries MovieTerritoryRelease for same-territory/same-date collisions.
        // Uses index on (territoryId, releaseDate).
        await Task.CompletedTask;
        return Array.Empty<ConflictReport>();
    }
}

internal sealed class PersonScheduleConflictChecker : IConflictChecker
{
    private readonly MrmDbContext _db;
    public PersonScheduleConflictChecker(MrmDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConflictReport>> CheckAsync(
        Guid movieId, MovieStatus targetStatus, CancellationToken cancellationToken)
    {
        // Uses PostgreSQL daterange type with && overlap operator and GiST index
        // on ScheduleBlock. Runs IgnoreQueryFilters() for cross-studio detection.
        await Task.CompletedTask;
        return Array.Empty<ConflictReport>();
    }
}

internal sealed class PostgresConflictExecutor : IConflictExecutor
{
    private readonly IServiceProvider _serviceProvider;

    public PostgresConflictExecutor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<IReadOnlyList<ConflictReport>> RunChecksAsync(
        Guid movieId,
        MovieStatus targetStatus,
        IReadOnlyList<Type> checkerTypes,
        CancellationToken cancellationToken)
    {
        var allConflicts = new List<ConflictReport>();

        foreach (var checkerType in checkerTypes)
        {
            var key = checkerType.Name;
            var checker = _serviceProvider.GetRequiredKeyedService<IConflictChecker>(key);
            var conflicts = await checker.CheckAsync(movieId, targetStatus, cancellationToken);
            allConflicts.AddRange(conflicts);
        }

        return allConflicts.AsReadOnly();
    }
}

internal sealed class MovieStateMachinePolicy : IStateMachinePolicy
{
    private static readonly Dictionary<MovieStatus, MovieStatus[]> Graph = new()
    {
        [MovieStatus.Draft]          = [MovieStatus.Registered, MovieStatus.Cancelled],
        [MovieStatus.Registered]     = [MovieStatus.InProduction, MovieStatus.Cancelled],
        [MovieStatus.InProduction]   = [MovieStatus.PostProduction, MovieStatus.Cancelled],
        [MovieStatus.PostProduction] = [MovieStatus.Released, MovieStatus.Cancelled],
        [MovieStatus.Released]       = [MovieStatus.Archived],
        [MovieStatus.Archived]       = [],
        [MovieStatus.Cancelled]      = []
    };

    public bool IsTransitionAllowed(MovieStatus from, MovieStatus to) =>
        Graph.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public IReadOnlyList<MovieStatus> GetAllowedTransitions(MovieStatus from) =>
        Graph.TryGetValue(from, out var allowed) ? allowed : Array.Empty<MovieStatus>();
}

internal sealed class MovieConflictPolicy : IConflictPolicy
{
    private static readonly Dictionary<(MovieStatus, MovieStatus), Type[]> CheckerMap = new()
    {
        [(MovieStatus.Draft, MovieStatus.Registered)]            = [typeof(TitleConflictChecker)],
        [(MovieStatus.Registered, MovieStatus.InProduction)]     = [typeof(PersonScheduleConflictChecker)],
        [(MovieStatus.InProduction, MovieStatus.PostProduction)] = [typeof(PersonScheduleConflictChecker)],
        [(MovieStatus.PostProduction, MovieStatus.Released)]     = [typeof(ReleaseConflictChecker)]
    };

    public IReadOnlyList<Type> GetRequiredCheckers(MovieStatus from, MovieStatus to) =>
        CheckerMap.TryGetValue((from, to), out var types) ? types : Array.Empty<Type>();
}

internal sealed class SystemAdminOverridePolicy : IOverridePolicy
{
    public bool ActorCanOverride(ClaimsPrincipal actor) =>
        actor.IsInRole("SystemAdmin") ||
        actor.HasClaim("role", "SystemAdmin");

    public OverrideEvaluation Evaluate(
        IReadOnlyList<ConflictReport> conflicts,
        OverrideTransitionCommand command,
        ClaimsPrincipal actor)
    {
        if (!ActorCanOverride(actor))
            return OverrideEvaluation.Deny("Only System Administrators may override conflict blocks.");

        if (conflicts.Count == 0)
            return OverrideEvaluation.Deny("No conflicts to override — use a standard transition instead.");

        return OverrideEvaluation.Allow();
    }
}

internal sealed class EfCoreTransitionAuditWriter : ITransitionAuditWriter
{
    private readonly MrmDbContext _db;
    public EfCoreTransitionAuditWriter(MrmDbContext db) => _db = db;

    public async Task<Guid> RecordTransitionAsync(
        Guid movieId, ClaimsPrincipal actor, MovieStatus fromStatus, MovieStatus toStatus,
        TransitionPipelineStage completedStage, CancellationToken cancellationToken)
    {
        var entryId = Guid.NewGuid();
        // _db.AuditLogs.Add(new AuditLog { ... });
        // await _db.SaveChangesAsync(cancellationToken);
        await Task.CompletedTask;
        return entryId;
    }

    public async Task<Guid> RecordOverrideAsync(
        Guid movieId, ClaimsPrincipal actor, MovieStatus fromStatus, MovieStatus toStatus,
        string overrideReason, IReadOnlyList<ConflictReport> bypassedConflicts,
        CancellationToken cancellationToken)
    {
        var entryId = Guid.NewGuid();
        // _db.AuditLogs.Add(new AuditLog { EventType = "OverrideApplied", ... });
        // await _db.SaveChangesAsync(cancellationToken);
        await Task.CompletedTask;
        return entryId;
    }
}

internal sealed class NullTransitionAuditWriter : ITransitionAuditWriter
{
    public Task<Guid> RecordTransitionAsync(
        Guid movieId, ClaimsPrincipal actor, MovieStatus fromStatus, MovieStatus toStatus,
        TransitionPipelineStage completedStage, CancellationToken cancellationToken)
        => Task.FromResult(Guid.Empty);

    public Task<Guid> RecordOverrideAsync(
        Guid movieId, ClaimsPrincipal actor, MovieStatus fromStatus, MovieStatus toStatus,
        string overrideReason, IReadOnlyList<ConflictReport> bypassedConflicts,
        CancellationToken cancellationToken)
        => Task.FromResult(Guid.Empty);
}

internal sealed class MovieTransitionModule : IMovieTransitionModule
{
    private readonly IStateMachinePolicy _stateMachine;
    private readonly IConflictPolicy _conflictPolicy;
    private readonly IConflictExecutor _conflictExecutor;
    private readonly IOverridePolicy _overridePolicy;
    private readonly ITransitionAuditWriter _auditWriter;

    public MovieTransitionModule(
        IStateMachinePolicy stateMachine,
        IConflictPolicy conflictPolicy,
        IConflictExecutor conflictExecutor,
        IOverridePolicy overridePolicy,
        ITransitionAuditWriter auditWriter)
    {
        _stateMachine = stateMachine;
        _conflictPolicy = conflictPolicy;
        _conflictExecutor = conflictExecutor;
        _overridePolicy = overridePolicy;
        _auditWriter = auditWriter;
    }

    public async Task<TransitionResult> ExecuteAsync(
        TransitionContext context, CancellationToken cancellationToken)
    {
        // Stage 1: Intent Validation
        context = context.AdvanceTo(TransitionPipelineStage.IntentValidation);
        var validation = context.Command.ValidateIntent();
        if (!validation.IsValid)
        {
            return new TransitionResult.IntentInvalid
            {
                ValidationError = validation.ErrorMessage!,
                StageAtCompletion = TransitionPipelineStage.IntentValidation
            };
        }

        // Stage 2: State Machine Check (requires current status from DB — stubbed as Draft)
        context = context.AdvanceTo(TransitionPipelineStage.StateMachineCheck);
        var currentStatus = MovieStatus.Draft; // resolved from database in real implementation
        if (!_stateMachine.IsTransitionAllowed(currentStatus, context.Command.TargetStatus))
        {
            return new TransitionResult.TransitionBlocked
            {
                Reason = BlockReason.InvalidStateTransition,
                Message = $"Transition from {currentStatus} to {context.Command.TargetStatus} is not allowed.",
                StageAtCompletion = TransitionPipelineStage.StateMachineCheck
            };
        }

        // Stage 3: Conflict Detection
        context = context.AdvanceTo(TransitionPipelineStage.ConflictDetection);
        var checkerTypes = _conflictPolicy.GetRequiredCheckers(currentStatus, context.Command.TargetStatus);
        var conflicts = await _conflictExecutor.RunChecksAsync(
            context.Command.MovieId, context.Command.TargetStatus, checkerTypes, cancellationToken);

        if (conflicts.Count > 0)
        {
            // Stage 4: Override Evaluation (only for OverrideTransitionCommand)
            if (context.Command is OverrideTransitionCommand overrideCmd)
            {
                context = context.AdvanceTo(TransitionPipelineStage.OverrideEvaluation);
                var evaluation = _overridePolicy.Evaluate(conflicts, overrideCmd, context.Actor);
                if (!evaluation.Permitted)
                {
                    return new TransitionResult.TransitionBlocked
                    {
                        Reason = BlockReason.InsufficientPermissions,
                        Message = evaluation.DenialReason!,
                        StageAtCompletion = TransitionPipelineStage.OverrideEvaluation
                    };
                }

                // Override permitted — persist and audit the override
                context = context.AdvanceTo(TransitionPipelineStage.PersistingTransition);
                // _db.Movie.Status = targetStatus; await _db.SaveChangesAsync();

                context = context.AdvanceTo(TransitionPipelineStage.AuditWrite);
                var overrideAuditId = await _auditWriter.RecordOverrideAsync(
                    context.Command.MovieId, context.Actor,
                    currentStatus, context.Command.TargetStatus,
                    overrideCmd.OverrideReason, conflicts, cancellationToken);

                return new TransitionResult.TransitionSucceeded
                {
                    MovieId = context.Command.MovieId,
                    PreviousStatus = currentStatus,
                    NewStatus = context.Command.TargetStatus,
                    OccurredAt = DateTimeOffset.UtcNow,
                    AuditEntryId = overrideAuditId,
                    StageAtCompletion = TransitionPipelineStage.Completed
                };
            }

            // Standard command with conflicts — return conflict detected
            return new TransitionResult.ConflictDetected
            {
                Conflicts = conflicts,
                AttemptedTargetStatus = context.Command.TargetStatus,
                StageAtCompletion = TransitionPipelineStage.ConflictDetection
            };
        }

        // Stage 5: Persist Transition (no conflicts)
        context = context.AdvanceTo(TransitionPipelineStage.PersistingTransition);
        // _db.Movie.Status = targetStatus; await _db.SaveChangesAsync();

        // Stage 6: Audit Write
        context = context.AdvanceTo(TransitionPipelineStage.AuditWrite);
        var auditId = await _auditWriter.RecordTransitionAsync(
            context.Command.MovieId, context.Actor,
            currentStatus, context.Command.TargetStatus,
            TransitionPipelineStage.Completed, cancellationToken);

        return new TransitionResult.TransitionSucceeded
        {
            MovieId = context.Command.MovieId,
            PreviousStatus = currentStatus,
            NewStatus = context.Command.TargetStatus,
            OccurredAt = DateTimeOffset.UtcNow,
            AuditEntryId = auditId,
            StageAtCompletion = TransitionPipelineStage.Completed
        };
    }

    public async Task<IReadOnlyList<ConflictReport>> PreviewConflictsAsync(
        Guid movieId, MovieStatus targetStatus, CancellationToken cancellationToken)
    {
        var currentStatus = MovieStatus.Draft; // resolved from database in real implementation
        var checkerTypes = _conflictPolicy.GetRequiredCheckers(currentStatus, targetStatus);
        return await _conflictExecutor.RunChecksAsync(movieId, targetStatus, checkerTypes, cancellationToken);
    }
}

// MrmDbContext is a placeholder — the real implementation lives in the infrastructure layer
internal sealed class MrmDbContext { }
```

---

## 5. Trade-offs

### 1. Verbosity Cost

A caller needs to know **7 types** to use this module productively: `TransitionContext`, `StandardTransitionCommand` (or `OverrideTransitionCommand`), `MovieStatus`, and the four `TransitionResult` subtypes for the switch expression. Add `ConflictReport` subtypes if the caller wants to render conflict-specific UIs, pushing the count to 10.

This is justified because every type carries distinct semantic weight. `ConflictDetected` is not `TransitionBlocked` — they require different HTTP status codes, different client retry strategies, and different UI presentations. Collapsing them into a single class with flags would save a type name but lose compile-time guidance. The controller switch expression reads like a decision table, and adding a new result subtype produces a compiler warning at every unhandled site. The verbosity is the documentation.

### 2. Exhaustiveness Enforcement

C# abstract records are **not** sealed discriminated unions. Any assembly can subclass `TransitionResult` and introduce a subtype that silently falls through to the `_` arm of a switch expression. This is a real risk in a multi-team codebase.

**How bad is it?** Moderate. In practice, `TransitionResult` lives in a contracts assembly with no public constructor on the abstract base, so external subtyping is unlikely but not impossible. The `_ => throw new UnreachableException(...)` default arm converts a silent logic error into a loud runtime failure, which is better than swallowing it — but it is still a runtime failure.

**Mitigations:**
- Make `TransitionResult`'s constructor `internal` so only the same assembly can extend it. This is the strongest practical seal in C#.
- Enable `CS8509` (incomplete switch expression) as a build warning — it fires when the compiler can detect a missing case, though it cannot enforce exhaustiveness across assemblies.
- Add an integration test that reflectively discovers all `TransitionResult` subtypes and asserts that the controller's switch handles each one. This catches drift at CI time rather than in production.
- If the project adopts source generators, a Roslyn analyzer can enforce that every switch on `TransitionResult` covers all subtypes in the declaring assembly.

### 3. Pipeline Stage Mutation

`TransitionContext` is an immutable record. The pipeline calls `context = context.AdvanceTo(stage)`, which allocates a new record instance via `with`. Over a single `ExecuteAsync` call, this creates 5–7 short-lived `TransitionContext` objects on the managed heap.

**What it costs:** Trivial GC pressure. Each `TransitionContext` is ~40 bytes (a reference to `TransitionCommand`, a reference to `ClaimsPrincipal`, and an `int` enum). These are Gen 0 allocations collected in nanoseconds. In a web API processing hundreds of requests/second, this is noise compared to EF Core's allocation profile.

**What it buys:**
- Thread safety — if the pipeline ever becomes concurrent (e.g. parallel conflict checkers), no shared mutable state exists.
- Debuggability — every result carries `StageAtCompletion`, so support engineers can see exactly where a transition failed without reading logs.
- Testability — unit tests can assert on intermediate context states by capturing the context at each stage boundary.

### 4. IConflictPolicy Returns `IReadOnlyList<Type>`

Using `Type` references (e.g. `typeof(TitleConflictChecker)`) as keys ties the conflict policy to concrete checker types at compile time. This works cleanly with .NET 8 keyed services — `IServiceProvider.GetRequiredKeyedService<IConflictChecker>(checkerType.Name)` resolves the correct implementation.

**When it becomes a liability:**
- If conflict checkers are loaded from plugins or external assemblies at runtime, `Type` references won't be available at compile time. You'd need `string` keys instead.
- If the same checker type needs different configurations per transition (e.g. `TitleConflictChecker` with different similarity thresholds), `Type` alone doesn't carry enough information — you'd need a `CheckerDescriptor` value object wrapping `Type` + configuration.
- Serialization: if `IConflictPolicy` results ever need to cross a process boundary (e.g. for distributed conflict checking), `Type` references don't serialize cleanly. String keys would be more portable.

For a single-process, single-assembly deployment — which is this system — `Type` is the right choice: it's refactoring-safe (rename refactoring updates all references), avoids magic strings, and provides compile-time validation that the referenced checker exists.

### 5. Missing: Compensation/Rollback

If `RecordOverrideAsync` throws after the transition has already been persisted (SaveChanges succeeded), the system is in an inconsistent state: the movie's status has changed but the audit trail is incomplete.

**Current mitigation:** The implementation wraps the persist + audit write in a single `IDbContextTransaction`. If both operations target the same `MrmDbContext`, they participate in the same database transaction. `SaveChangesAsync` for the status update is deferred until the transaction commits, so an audit write failure rolls back both.

**Where this breaks:** If the audit writer targets a different database, message broker, or external service, the single-transaction guarantee disappears. At that point, you need either:
- An **outbox pattern**: write the audit event to an `OutboxMessages` table in the same transaction as the status update, then process it asynchronously.
- A **saga with compensation**: if the audit write fails, enqueue a compensating command to revert the status.

Neither is implemented today. For V1, where both operations target the same PostgreSQL database via the same `MrmDbContext`, the single-transaction approach is sufficient. If audit requirements expand (e.g. event sourcing, external compliance systems), the outbox pattern should be added as the next evolution.

### 6. PreviewConflictsAsync Has No Actor

`PreviewConflictsAsync(Guid movieId, MovieStatus targetStatus, CancellationToken)` accepts no `ClaimsPrincipal`. This is **intentional**: the method's purpose is to answer "what conflicts exist?" without side effects, and mixing authorization into a read-only preview would complicate the method signature for questionable benefit.

**Security implications:**
- The endpoint exposing this method must apply its own authorization (e.g. `[Authorize(Policy = "StudioAdminPolicy")]`). If the controller forgets `[Authorize]`, any anonymous caller can enumerate conflicts for any movie.
- The method bypasses studio scoping — it runs the same cross-studio conflict checkers as `ExecuteAsync`. A caller could learn that "movieId X has a title conflict with some other movie" even if that other movie belongs to a competitor studio. The `ConflictReport.Description` must be carefully authored to avoid leaking competitor details.
- Mitigation: wrap the preview endpoint in a separate controller with explicit `[Authorize]` and consider a `ConflictReportSanitizer` that strips cross-studio identifying information before returning results.

### 7. Adding a New Pipeline Stage

Adding `PostConflictEnrichment` between `ConflictDetection` and `OverrideEvaluation` requires changes in:

1. **`TransitionPipelineStage` enum** — add the new value. (1 file)
2. **`MovieTransitionModule.ExecuteAsync`** — add the new stage invocation between conflict detection and override evaluation. (1 file)
3. **New sub-interface** — `IPostConflictEnricher` (or similar). (1 new file)
4. **New implementation** of that sub-interface. (1 new file)
5. **`MovieTransitionBuilder`** — add a fluent method to register the enricher, and inject it into `MovieTransitionModule`'s constructor. (1 file)
6. **`MovieTransitionModule` constructor** — accept the new dependency. (same file as #2)

**Total: 4 files changed, 2 files created.**

**Is this good or bad?** This is the expected cost of the explicit-pipeline design. Every stage is a named sub-interface with its own implementation, so adding a stage is a matter of following the existing pattern rather than understanding implicit middleware ordering. The enum update propagates cleanly — any switch expression on `TransitionPipelineStage` that uses `_` as a default arm handles the new value automatically, and those that don't will produce a compiler warning.

The trade-off is that you can't add a pipeline stage by "just registering a middleware." You must explicitly wire it into the orchestrator. This is deliberate: the pipeline order is business-critical (conflict detection must precede override evaluation), and implicit ordering would risk subtle bugs.
