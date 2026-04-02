// =============================================================================
// DESIGN D — Single-Dispatch Discriminated-Union Command Pattern
// Module: IMovieLifecycleService
// Target: .NET 8, C# 12, Nullable reference types enabled
// =============================================================================

// ## Section 1: Interface Signature

// File: Mrm.Movies.Lifecycle/IMovieLifecycleService.cs

#region Section1_InterfaceSignature

namespace Mrm.Movies.Lifecycle;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/// <summary>
/// Represents the lifecycle status of a movie within the release management system.
/// Valid transitions:
///   Draft(0) → UnderReview(1) → Approved(2) → Released(4)
///                                Approved(2) → Rejected(3)
///                UnderReview(1) → Rejected(3)
/// </summary>
public enum MovieStatus
{
    Draft = 0,
    UnderReview = 1,
    Approved = 2,
    Rejected = 3,
    Released = 4
}

/// <summary>
/// Severity level assigned to a conflict violation detected during a lifecycle transition.
/// <see cref="Warning"/> conflicts are informational — they appear in the result but do NOT
/// prevent the transition. <see cref="HardBlock"/> conflicts stop the transition unless
/// an admin override is applied.
/// </summary>
public enum ConflictSeverity
{
    Warning = 0,
    HardBlock = 1
}

// ---------------------------------------------------------------------------
// Conflict Violation
// ---------------------------------------------------------------------------

/// <summary>
/// A single conflict violation detected by a conflict checker during a lifecycle transition.
/// </summary>
/// <param name="CheckerName">
/// The name of the conflict checker that produced this violation
/// (e.g. "TitleConflictChecker", "ReleaseConflictChecker", "PersonScheduleConflictChecker").
/// </param>
/// <param name="Severity">Whether this violation is a soft warning or a hard block.</param>
/// <param name="Description">
/// Human-readable description of the conflict, including the specific entities involved.
/// </param>
/// <param name="ConflictingEntityId">
/// Optional identifier of the conflicting entity (e.g. the other movie's ID, a person's ID,
/// or a territory release ID). Null when the conflict is structural rather than entity-specific.
/// </param>
public sealed record ConflictViolation(
    string CheckerName,
    ConflictSeverity Severity,
    string Description,
    Guid? ConflictingEntityId
);

// ---------------------------------------------------------------------------
// TransitionCommand — Sealed Discriminated Union (Input)
// ---------------------------------------------------------------------------

/// <summary>
/// Base type for all movie lifecycle transition commands. Callers construct one of the
/// sealed subtypes and dispatch it through
/// <see cref="IMovieLifecycleService.ExecuteAsync"/>.
/// </summary>
public abstract record TransitionCommand(Guid MovieId)
{
    // Prevent external inheritance — only the nested sealed subtypes are valid.
    internal TransitionCommand() : this(Guid.Empty) { }

    /// <summary>
    /// Submits a movie for review, transitioning it from Draft to UnderReview.
    /// Triggers TitleConflictChecker and PersonScheduleConflictChecker.
    /// </summary>
    /// <param name="MovieId">The movie to submit.</param>
    /// <param name="ActorId">The user initiating the submission.</param>
    public sealed record SubmitCommand(Guid MovieId, Guid ActorId)
        : TransitionCommand(MovieId);

    /// <summary>
    /// Approves a movie that is currently UnderReview, transitioning it to Approved.
    /// Triggers ReleaseConflictChecker and PersonScheduleConflictChecker.
    /// </summary>
    /// <param name="MovieId">The movie to approve.</param>
    /// <param name="ReviewerId">The reviewer granting approval.</param>
    public sealed record ApproveCommand(Guid MovieId, Guid ReviewerId)
        : TransitionCommand(MovieId);

    /// <summary>
    /// Rejects a movie, transitioning it from UnderReview or Approved to Rejected.
    /// No conflict checkers run on rejection.
    /// </summary>
    /// <param name="MovieId">The movie to reject.</param>
    /// <param name="ReviewerId">The reviewer performing the rejection.</param>
    /// <param name="Reason">Mandatory written reason for rejection.</param>
    public sealed record RejectCommand(Guid MovieId, Guid ReviewerId, string Reason)
        : TransitionCommand(MovieId);

    /// <summary>
    /// Releases a movie that is currently Approved, transitioning it to Released.
    /// No additional conflict checkers run at release time.
    /// </summary>
    /// <param name="MovieId">The movie to release.</param>
    /// <param name="DistributorId">The distributor initiating the release.</param>
    public sealed record ReleaseCommand(Guid MovieId, Guid DistributorId)
        : TransitionCommand(MovieId);

    /// <summary>
    /// Admin override command that forces a transition to
    /// <paramref name="TargetStatus"/> regardless of hard-block conflicts.
    /// Only System Admins may issue this command. The reason is mandatory and
    /// recorded in the audit log.
    /// </summary>
    /// <param name="MovieId">The movie to override.</param>
    /// <param name="AdminId">The System Admin performing the override.</param>
    /// <param name="TargetStatus">The desired target status to force.</param>
    /// <param name="Reason">Mandatory written justification for the override.</param>
    public sealed record OverrideCommand(
        Guid MovieId,
        Guid AdminId,
        MovieStatus TargetStatus,
        string Reason
    ) : TransitionCommand(MovieId);
}

// ---------------------------------------------------------------------------
// TransitionResult — Sealed Discriminated Union (Output)
// ---------------------------------------------------------------------------

/// <summary>
/// Base type for all movie lifecycle transition results. Callers pattern-match
/// the sealed subtypes to determine the outcome and map to HTTP responses or
/// domain logic.
/// </summary>
public abstract record TransitionResult(Guid MovieId)
{
    internal TransitionResult() : this(Guid.Empty) { }

    /// <summary>
    /// The transition succeeded. The movie moved from
    /// <see cref="PreviousStatus"/> to <see cref="NewStatus"/> and an audit
    /// entry was recorded.
    /// </summary>
    public sealed record TransitionSucceeded(
        Guid MovieId,
        MovieStatus PreviousStatus,
        MovieStatus NewStatus,
        DateTimeOffset TransitionedAt,
        Guid AuditEntryId
    ) : TransitionResult(MovieId);

    /// <summary>
    /// The transition was blocked by one or more conflict violations. At least one
    /// violation has <see cref="ConflictSeverity.HardBlock"/> severity. The movie's
    /// status was NOT changed.
    /// </summary>
    public sealed record TransitionBlocked(
        Guid MovieId,
        MovieStatus AttemptedStatus,
        IReadOnlyList<ConflictViolation> Conflicts
    ) : TransitionResult(MovieId);

    /// <summary>
    /// The transition is invalid for the movie's current state. This covers
    /// illegal transitions (e.g. Draft → Released), movie not found, or
    /// other structural violations.
    /// </summary>
    public sealed record TransitionInvalid(
        Guid MovieId,
        string Reason,
        MovieStatus? CurrentStatus
    ) : TransitionResult(MovieId);

    /// <summary>
    /// The caller is not authorized to perform the requested transition.
    /// For example, a non-admin attempted an <see cref="TransitionCommand.OverrideCommand"/>.
    /// </summary>
    public sealed record TransitionForbidden(
        Guid MovieId,
        string Reason
    ) : TransitionResult(MovieId);

    /// <summary>
    /// An admin override was applied. The movie was force-transitioned despite
    /// hard-block conflicts. The override reason and audit entry are captured.
    /// </summary>
    public sealed record OverrideApplied(
        Guid MovieId,
        MovieStatus PreviousStatus,
        MovieStatus NewStatus,
        DateTimeOffset TransitionedAt,
        Guid AuditEntryId,
        string AdminReason
    ) : TransitionResult(MovieId);
}

// ---------------------------------------------------------------------------
// Service Interface
// ---------------------------------------------------------------------------

/// <summary>
/// Single entry point for all movie lifecycle transitions in the MRM system.
///
/// <para>
/// <b>Command Dispatch Pattern:</b> This interface exposes exactly one method.
/// Callers construct a typed <see cref="TransitionCommand"/> subtype — such as
/// <see cref="TransitionCommand.SubmitCommand"/>,
/// <see cref="TransitionCommand.ApproveCommand"/>, or
/// <see cref="TransitionCommand.OverrideCommand"/> — and dispatch it through
/// <see cref="ExecuteAsync"/>. The service internally determines the correct
/// state-machine path, runs the appropriate conflict checkers, applies override
/// logic if applicable, writes the audit log, and returns a typed
/// <see cref="TransitionResult"/> that the caller pattern-matches to map into
/// HTTP responses, UI states, or test assertions.
/// </para>
///
/// <para>
/// <b>Sealed hierarchies guarantee exhaustiveness:</b> Because both the command
/// and result types are sealed record hierarchies, the C# compiler will emit
/// warnings if a caller's switch expression does not handle every variant. This
/// turns runtime "forgot a case" bugs into compile-time errors.
/// </para>
///
/// <para>
/// <b>What this hides from callers:</b>
/// <list type="bullet">
///   <item>The state machine transition table and its validation rules</item>
///   <item>Which conflict checkers run at which transition</item>
///   <item>EF Core queries that bypass studio-scoped query filters</item>
///   <item>Transaction boundaries around status update + audit write</item>
///   <item>Override authorization and reason enforcement</item>
///   <item>Parallel execution of independent conflict checkers</item>
///   <item>Conflict severity aggregation (warnings pass through, hard blocks stop the transition)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This service is registered as a Singleton using
/// <c>IDbContextFactory&lt;MovieDbContext&gt;</c>. Each call to
/// <see cref="ExecuteAsync"/> creates a short-lived DbContext via
/// <c>CreateDbContextAsync</c>, ensuring no shared mutable state between
/// concurrent requests.
/// </para>
/// </summary>
public interface IMovieLifecycleService
{
    /// <summary>
    /// Dispatches a lifecycle transition command and returns the typed result.
    /// </summary>
    /// <param name="command">
    /// A sealed <see cref="TransitionCommand"/> subtype describing the desired transition.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>
    /// A <see cref="TransitionResult"/> subtype indicating the outcome. Callers should
    /// use a switch expression to exhaustively handle all result variants.
    /// </returns>
    Task<TransitionResult> ExecuteAsync(
        TransitionCommand command,
        CancellationToken cancellationToken = default
    );
}

// ---------------------------------------------------------------------------
// Pattern-matching usage example (for documentation / onboarding)
// ---------------------------------------------------------------------------

/*
// ── Dispatching a command ──────────────────────────────────────────────────

TransitionCommand command = transitionType switch
{
    "submit"   => new TransitionCommand.SubmitCommand(movieId, callerId),
    "approve"  => new TransitionCommand.ApproveCommand(movieId, callerId),
    "reject"   => new TransitionCommand.RejectCommand(movieId, callerId, reason!),
    "release"  => new TransitionCommand.ReleaseCommand(movieId, callerId),
    "override" => new TransitionCommand.OverrideCommand(movieId, callerId, targetStatus, reason!),
    _          => throw new ArgumentOutOfRangeException(nameof(transitionType))
};

var result = await lifecycleService.ExecuteAsync(command, cancellationToken);

// ── Exhaustive pattern match on the result ─────────────────────────────────

var httpResult = result switch
{
    TransitionResult.TransitionSucceeded ok => Results.Ok(new
    {
        ok.MovieId,
        ok.PreviousStatus,
        ok.NewStatus,
        ok.TransitionedAt,
        ok.AuditEntryId
    }),

    TransitionResult.OverrideApplied ov => Results.Ok(new
    {
        ov.MovieId,
        ov.PreviousStatus,
        ov.NewStatus,
        ov.TransitionedAt,
        ov.AuditEntryId,
        ov.AdminReason,
        Overridden = true
    }),

    TransitionResult.TransitionBlocked blocked => Results.Conflict(new
    {
        blocked.MovieId,
        blocked.AttemptedStatus,
        blocked.Conflicts
    }),

    TransitionResult.TransitionInvalid invalid => Results.UnprocessableEntity(new
    {
        invalid.MovieId,
        invalid.Reason,
        invalid.CurrentStatus
    }),

    TransitionResult.TransitionForbidden forbidden => Results.Json(
        new { forbidden.MovieId, forbidden.Reason },
        statusCode: 403
    ),

    _ => Results.StatusCode(500)
};
*/

#endregion

// =============================================================================
// ## Section 2: Usage Example
// =============================================================================

#region Section2_UsageExample

// File: Mrm.Movies.Api/Controllers/MoviesController.cs

namespace Mrm.Movies.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using Mrm.Movies.Lifecycle;

// ---------------------------------------------------------------------------
// Request DTOs
// ---------------------------------------------------------------------------

/// <summary>Request body for submitting a movie for review.</summary>
public sealed record SubmitMovieRequest(Guid ActorId);

/// <summary>Request body for approving a movie.</summary>
public sealed record ApproveMovieRequest(Guid ReviewerId);

/// <summary>Request body for the admin override endpoint.</summary>
public sealed record OverrideMovieRequest(
    Guid AdminId,
    MovieStatus TargetStatus,
    string Reason
);

// ---------------------------------------------------------------------------
// Response DTOs — shared by both controllers
// ---------------------------------------------------------------------------

public sealed record TransitionSucceededResponse(
    Guid MovieId,
    MovieStatus PreviousStatus,
    MovieStatus NewStatus,
    DateTimeOffset TransitionedAt,
    Guid AuditEntryId,
    bool Overridden,
    string? AdminReason
);

public sealed record TransitionBlockedResponse(
    Guid MovieId,
    MovieStatus AttemptedStatus,
    IReadOnlyList<ConflictViolation> Conflicts
);

public sealed record TransitionInvalidResponse(
    Guid MovieId,
    string Reason,
    MovieStatus? CurrentStatus
);

public sealed record TransitionForbiddenResponse(
    Guid MovieId,
    string Reason
);

// ---------------------------------------------------------------------------
// MoviesController — regular lifecycle transitions
// ---------------------------------------------------------------------------

[ApiController]
[Route("api/movies")]
public sealed class MoviesController : ControllerBase
{
    private readonly IMovieLifecycleService _lifecycle;

    public MoviesController(IMovieLifecycleService lifecycle)
    {
        _lifecycle = lifecycle;
    }

    /// <summary>
    /// Submits a movie for review, transitioning it from Draft to UnderReview.
    /// </summary>
    [HttpPost("{id:guid}/submit")]
    [ProducesResponseType(typeof(TransitionSucceededResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionBlockedResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TransitionInvalidResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(TransitionForbiddenResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Submit(
        [FromRoute] Guid id,
        [FromBody] SubmitMovieRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransitionCommand.SubmitCommand(id, request.ActorId);
        var result = await _lifecycle.ExecuteAsync(command, cancellationToken);

        return result switch
        {
            TransitionResult.TransitionSucceeded ok => Ok(new TransitionSucceededResponse(
                ok.MovieId,
                ok.PreviousStatus,
                ok.NewStatus,
                ok.TransitionedAt,
                ok.AuditEntryId,
                Overridden: false,
                AdminReason: null
            )),

            TransitionResult.OverrideApplied ov => Ok(new TransitionSucceededResponse(
                ov.MovieId,
                ov.PreviousStatus,
                ov.NewStatus,
                ov.TransitionedAt,
                ov.AuditEntryId,
                Overridden: true,
                AdminReason: ov.AdminReason
            )),

            TransitionResult.TransitionBlocked blocked => Conflict(new TransitionBlockedResponse(
                blocked.MovieId,
                blocked.AttemptedStatus,
                blocked.Conflicts
            )),

            TransitionResult.TransitionInvalid invalid => UnprocessableEntity(
                new TransitionInvalidResponse(
                    invalid.MovieId,
                    invalid.Reason,
                    invalid.CurrentStatus
                )),

            TransitionResult.TransitionForbidden forbidden => StatusCode(
                StatusCodes.Status403Forbidden,
                new TransitionForbiddenResponse(forbidden.MovieId, forbidden.Reason)
            ),

            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>
    /// Approves a movie currently under review, transitioning it to Approved.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(TransitionSucceededResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionBlockedResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TransitionInvalidResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(TransitionForbiddenResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Approve(
        [FromRoute] Guid id,
        [FromBody] ApproveMovieRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransitionCommand.ApproveCommand(id, request.ReviewerId);
        var result = await _lifecycle.ExecuteAsync(command, cancellationToken);

        return result switch
        {
            TransitionResult.TransitionSucceeded ok => Ok(new TransitionSucceededResponse(
                ok.MovieId,
                ok.PreviousStatus,
                ok.NewStatus,
                ok.TransitionedAt,
                ok.AuditEntryId,
                Overridden: false,
                AdminReason: null
            )),

            TransitionResult.OverrideApplied ov => Ok(new TransitionSucceededResponse(
                ov.MovieId,
                ov.PreviousStatus,
                ov.NewStatus,
                ov.TransitionedAt,
                ov.AuditEntryId,
                Overridden: true,
                AdminReason: ov.AdminReason
            )),

            TransitionResult.TransitionBlocked blocked => Conflict(new TransitionBlockedResponse(
                blocked.MovieId,
                blocked.AttemptedStatus,
                blocked.Conflicts
            )),

            TransitionResult.TransitionInvalid invalid => UnprocessableEntity(
                new TransitionInvalidResponse(
                    invalid.MovieId,
                    invalid.Reason,
                    invalid.CurrentStatus
                )),

            TransitionResult.TransitionForbidden forbidden => StatusCode(
                StatusCodes.Status403Forbidden,
                new TransitionForbiddenResponse(forbidden.MovieId, forbidden.Reason)
            ),

            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}

// ---------------------------------------------------------------------------
// AdminMoviesController — admin override transitions
// ---------------------------------------------------------------------------

[ApiController]
[Route("api/admin/movies")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "SystemAdmin")]
public sealed class AdminMoviesController : ControllerBase
{
    private readonly IMovieLifecycleService _lifecycle;

    public AdminMoviesController(IMovieLifecycleService lifecycle)
    {
        _lifecycle = lifecycle;
    }

    /// <summary>
    /// Forces a movie to a target status, bypassing hard-block conflicts.
    /// Only System Admins may call this endpoint. A written reason is mandatory.
    /// </summary>
    [HttpPost("{id:guid}/override")]
    [ProducesResponseType(typeof(TransitionSucceededResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionBlockedResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TransitionInvalidResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(TransitionForbiddenResponse), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Override(
        [FromRoute] Guid id,
        [FromBody] OverrideMovieRequest request,
        CancellationToken cancellationToken)
    {
        var command = new TransitionCommand.OverrideCommand(
            id,
            request.AdminId,
            request.TargetStatus,
            request.Reason
        );

        var result = await _lifecycle.ExecuteAsync(command, cancellationToken);

        return result switch
        {
            TransitionResult.OverrideApplied ov => Ok(new TransitionSucceededResponse(
                ov.MovieId,
                ov.PreviousStatus,
                ov.NewStatus,
                ov.TransitionedAt,
                ov.AuditEntryId,
                Overridden: true,
                AdminReason: ov.AdminReason
            )),

            TransitionResult.TransitionSucceeded ok => Ok(new TransitionSucceededResponse(
                ok.MovieId,
                ok.PreviousStatus,
                ok.NewStatus,
                ok.TransitionedAt,
                ok.AuditEntryId,
                Overridden: false,
                AdminReason: null
            )),

            TransitionResult.TransitionBlocked blocked => Conflict(new TransitionBlockedResponse(
                blocked.MovieId,
                blocked.AttemptedStatus,
                blocked.Conflicts
            )),

            TransitionResult.TransitionInvalid invalid => UnprocessableEntity(
                new TransitionInvalidResponse(
                    invalid.MovieId,
                    invalid.Reason,
                    invalid.CurrentStatus
                )),

            TransitionResult.TransitionForbidden forbidden => StatusCode(
                StatusCodes.Status403Forbidden,
                new TransitionForbiddenResponse(forbidden.MovieId, forbidden.Reason)
            ),

            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}

#endregion

// =============================================================================
// ## Section 3: What Complexity It Hides
// =============================================================================

#region Section3_HiddenComplexity

/*
Exactly 10 internal complexity items that callers NEVER see:

1. **State machine transition table** — An internal
   `FrozenDictionary<(MovieStatus From, Type CommandType), MovieStatus To>` maps each
   (current status, command type) pair to the allowed target status. For example,
   `(Draft, SubmitCommand) → UnderReview`, `(UnderReview, ApproveCommand) → Approved`,
   `(Approved, RejectCommand) → Rejected`. Invalid combinations (e.g. `Draft + ApproveCommand`)
   return `TransitionInvalid` immediately without touching the database.

2. **Conflict checker routing policy** — An internal
   `FrozenDictionary<Type, IReadOnlyList<IConflictChecker>>` maps command types to the
   ordered list of conflict checker instances that must run:
   `SubmitCommand → [TitleConflictChecker, PersonScheduleConflictChecker]`,
   `ApproveCommand → [ReleaseConflictChecker, PersonScheduleConflictChecker]`,
   `RejectCommand → []`, `ReleaseCommand → []`. Callers never configure or know which
   checkers exist.

3. **TitleConflictChecker SQL** — Queries the `movies` table across ALL studios (bypassing
   the `studio_id` global query filter via `IgnoreQueryFilters()`) using:
   `WHERE normalized_title = @normalizedTitle AND release_year = @releaseYear
   AND id != @movieId AND status NOT IN (3)` (where 3 = Rejected). Returns a `HardBlock`
   ConflictViolation with the conflicting movie's ID as `ConflictingEntityId`.

4. **PersonScheduleConflictChecker SQL** — Joins `production_roles` → `schedule_blocks`
   and uses PostgreSQL's native `daterange` overlap operator (`&&`) to detect double-booked
   cast or crew: `WHERE person_id IN (SELECT person_id FROM production_roles WHERE movie_id
   = @movieId) AND schedule_block.during && ANY(@movieScheduleRanges)`. Returns one
   `HardBlock` ConflictViolation per overlapping person, with the person's ID as
   `ConflictingEntityId`.

5. **ReleaseConflictChecker SQL** — Queries `movie_territory_releases` across all studios
   with: `WHERE territory_id = @territoryId AND release_date BETWEEN @releaseDate - INTERVAL
   '14 days' AND @releaseDate + INTERVAL '14 days' AND movie_id != @movieId`. Returns
   `Warning`-severity violations because release date proximity is an advisory, not a block.
   The 14-day window is configurable via `ConflictCheckOptions`.

6. **Parallel conflict execution** — When a command maps to multiple checkers (e.g.
   SubmitCommand triggers both TitleConflictChecker and PersonScheduleConflictChecker),
   the service spawns them via `Task.WhenAll`, each receiving its own `DbContext` from
   `IDbContextFactory<MovieDbContext>.CreateDbContextAsync()`. Results are merged into a
   single `IReadOnlyList<ConflictViolation>`, sorted by severity descending. Callers see
   only the aggregated list.

7. **Global query filter bypass** — Each conflict checker creates a dedicated
   `MovieDbContext` via the factory and calls `dbContext.Movies.IgnoreQueryFilters()` to
   disable the `HasQueryFilter(m => m.StudioId == _currentStudioId)` tenant-scoping filter.
   This isolated context is disposed after the checker completes, so the filter bypass never
   leaks to other code sharing the HTTP request scope.

8. **Transaction boundary with optimistic concurrency** — The status update
   (`UPDATE movies SET status = @newStatus, updated_at = @now WHERE id = @movieId
   AND status = @expectedStatus`) and the audit log insert
   (`INSERT INTO audit_logs (...)`) execute inside a single `IDbContextTransaction`
   with `IsolationLevel.ReadCommitted`. If `SaveChangesAsync` reports zero rows affected
   on the movie update (another request transitioned the movie concurrently), the
   transaction is rolled back and `TransitionInvalid` is returned with reason
   "Movie was concurrently modified".

9. **Audit log persistence** — Every call to `ExecuteAsync` — whether it succeeds, blocks,
   or is overridden — writes a row to the `audit_logs` table with columns:
   `id (uuid PK)`, `event_type ('Transition' | 'Override' | 'Blocked' | 'Invalid')`,
   `actor_user_id (uuid)`, `movie_id (uuid)`, `previous_status (int)`,
   `attempted_status (int)`, `new_status (int?)`, `command_type_name (text)`,
   `conflict_snapshot (jsonb)`, `override_reason (text?)`, `timestamp (timestamptz)`.
   The `AuditEntryId` in success results is the PK of this row. The `conflict_snapshot`
   column stores the full serialized `ConflictViolation[]` even on overrides, creating
   a permanent record of what was bypassed.

10. **Override authorization and reason validation** — When an `OverrideCommand` arrives,
    the service loads the user row from the `users` table
    (`SELECT role FROM users WHERE id = @adminId`) and confirms
    `role = 'SystemAdmin'`. It also validates that `Reason` is non-whitespace and at
    least 10 characters. If the role check fails, it returns `TransitionForbidden` with
    reason "User {adminId} does not have SystemAdmin role". If the reason is too short,
    it returns `TransitionInvalid` with reason "Override reason must be at least 10
    characters". If both pass, the service runs the normal conflict checkers for the
    implied transition, ignores all `HardBlock` violations (but still records them in the
    audit log's `conflict_snapshot`), and returns `OverrideApplied`.
*/

#endregion

// =============================================================================
// ## Section 4: Dependency Strategy
// =============================================================================

#region Section4_DependencyStrategy

/*
### Why `IDbContextFactory<MovieDbContext>` over scoped `DbContext`

The `IMovieLifecycleService` is registered as a **Singleton** in the DI container. A
Singleton service cannot depend on Scoped services — injecting a scoped `MovieDbContext`
directly would throw an `InvalidOperationException` at startup when `ValidateScopes` is
enabled (the default in Development environments). Even if you worked around this by
registering the service as Scoped instead, you would still hit a deeper architectural
problem: a single `DbContext` instance cannot safely serve multiple concurrent queries
within one `ExecuteAsync` call, and it carries tenant-scoping state that conflict checkers
must bypass. The `IDbContextFactory<MovieDbContext>` pattern sidesteps both issues. The
factory itself is Singleton-safe (it captures the `DbContextOptions` at registration time
and creates new instances on demand), and each call to `CreateDbContextAsync` produces a
fresh, short-lived `DbContext` instance that is disposed when the operation completes.

### The studio-scoping query filter problem

`MovieDbContext` applies a global query filter on the `Movies` DbSet:
`builder.Entity<Movie>().HasQueryFilter(m => m.StudioId == _currentStudioId)`. This filter
is essential for multi-tenant data isolation in regular CRUD endpoints — a studio user must
never see another studio's movies in list or detail views. However, conflict checkers must
see data across ALL studios: a title conflict is only meaningful if it detects collisions
across the entire catalog, not just within one studio's silo. A scoped `DbContext` is
pre-configured with the current HTTP request's `StudioId` baked into its filter closure.
Calling `IgnoreQueryFilters()` on that shared scoped instance is technically possible but
dangerous — it disables the filter for the remainder of that scope's lifetime, potentially
leaking cross-studio data to other repository methods that share the same scoped `DbContext`
within the same request pipeline. By creating a dedicated `DbContext` per conflict checker
via the factory, each checker calls `IgnoreQueryFilters()` on its own isolated instance.
When that instance is disposed at the end of the checker method, the filter bypass dies
with it. No other code path in the request pipeline is affected.

### Parallel conflict checks and DbContext thread safety

At Submit time, the service must run `TitleConflictChecker` and
`PersonScheduleConflictChecker` concurrently via `Task.WhenAll` — the title check hits
the `movies` table while the schedule check hits `production_roles` joined to
`schedule_blocks`, so there is no query-level dependency between them and parallelism
cuts latency roughly in half. EF Core's `DbContext` is explicitly NOT thread-safe —
Microsoft's documentation states that "EF Core does not support multiple parallel
operations being run on the same DbContext instance" and that concurrent access produces
undefined behavior, including silent data corruption, cached navigation property
inconsistencies, and cryptic `InvalidOperationException` throws from the change tracker.
If both checkers shared one scoped `DbContext`, `Task.WhenAll` would immediately violate
this constraint. The factory pattern makes parallel execution safe by construction: each
checker receives its own `DbContext` instance backed by its own `NpgsqlConnection` drawn
from the connection pool. There is no shared mutable state — the change trackers,
identity maps, and query compilation caches are completely independent.

### Registration and constructor
*/

// ── Program.cs ─────────────────────────────────────────────────────────────

/*
```csharp
// Program.cs — service registration

using Microsoft.EntityFrameworkCore;
using Mrm.Movies.Lifecycle;
using Mrm.Movies.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Register the DbContext factory — NOT AddDbContext (which registers Scoped).
// The factory is Singleton-safe and produces short-lived DbContext instances on demand.
builder.Services.AddDbContextFactory<MovieDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("MovieDb"),
        npgsql =>
        {
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null
            );
            npgsql.MigrationsAssembly("Mrm.Movies.Infrastructure");
        }
    )
);

// TimeProvider for testable timestamps (.NET 8 abstraction).
builder.Services.AddSingleton(TimeProvider.System);

// The lifecycle service is a Singleton — it holds no mutable state.
// It receives the factory, not a DbContext instance.
builder.Services.AddSingleton<IMovieLifecycleService, MovieLifecycleService>();

var app = builder.Build();
// ... middleware, endpoints, app.Run()
```
*/

// ── Internal service constructor ───────────────────────────────────────────

/*
```csharp
// MovieLifecycleService.cs — internal implementation (constructor only)

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mrm.Movies.Lifecycle;

namespace Mrm.Movies.Infrastructure;

internal sealed class MovieLifecycleService : IMovieLifecycleService
{
    private readonly IDbContextFactory<MovieDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MovieLifecycleService> _logger;

    public MovieLifecycleService(
        IDbContextFactory<MovieDbContext> dbContextFactory,
        TimeProvider timeProvider,
        ILogger<MovieLifecycleService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<TransitionResult> ExecuteAsync(
        TransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        // Primary DbContext for the status update + audit write transaction.
        // Created here, disposed at the end of this method — NOT at service disposal.
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // 1. Load movie, validate state machine
        var movie = await dbContext.Movies.FindAsync([command.MovieId], cancellationToken);
        if (movie is null)
            return new TransitionResult.TransitionInvalid(command.MovieId, "Movie not found.", CurrentStatus: null);

        // 2. Resolve target status from state machine table
        // 3. Run conflict checkers in parallel — each gets its OWN DbContext:
        //
        //    var titleTask  = RunCheckerAsync<TitleConflictChecker>(command, cancellationToken);
        //    var personTask = RunCheckerAsync<PersonScheduleConflictChecker>(command, cancellationToken);
        //    await Task.WhenAll(titleTask, personTask);
        //
        //    Inside RunCheckerAsync:
        //      await using var checkerDb = await _dbContextFactory.CreateDbContextAsync(ct);
        //      return await checker.CheckAsync(checkerDb, command, ct);
        //
        // 4. Aggregate violations, apply override logic if OverrideCommand
        // 5. Begin transaction, update status with optimistic concurrency, write audit log
        // 6. Commit and return typed result

        throw new NotImplementedException("Implementation follows in a separate file.");
    }
}
```
*/

#endregion

// =============================================================================
// ## Section 5: Trade-offs
// =============================================================================

#region Section5_Tradeoffs

/*
### Gains

**1. Exhaustive result handling is compiler-enforced.** Because `TransitionResult` is a
sealed hierarchy of five record types, a C# switch expression over it triggers compiler
warning CS8509 ("the switch expression does not handle all possible values of its input
type") if any variant is omitted. This is not a theoretical nicety — in this codebase,
`TransitionBlocked` maps to HTTP 409 Conflict with a list of `ConflictViolation` objects
in the body, while `TransitionForbidden` maps to HTTP 403 with a simple reason string.
Without sealed hierarchies, a developer adding a new controller endpoint could forget to
handle `TransitionForbidden` and silently fall through to a 500 response, which in
production would surface as an opaque error to the admin trying to understand why their
override was rejected. The type system catches this at compile time, not in a QA cycle.

**2. Adding a new lifecycle command requires zero changes to existing callers.** When the
product team asks for a `WithdrawCommand` (Draft|UnderReview|Approved → Withdrawn), we add
a sealed record to `TransitionCommand`, add one entry to the internal state machine
dictionary, and wire up its conflict checker list (likely empty). Existing controllers that
dispatch `SubmitCommand` or `ApproveCommand` compile and run untouched — the new command is
an additive code path, not a modification to existing ones. This matters operationally
because the studio-facing `MoviesController` and the platform `AdminMoviesController` are
owned by different teams, and a lifecycle extension should not force cross-team PRs.

**3. The single dispatch point creates a natural audit and logging chokepoint.** Every
lifecycle transition — Submit, Approve, Reject, Release, and Override — flows through the
same `ExecuteAsync` method. This means structured logging (correlation ID, command type,
duration), OpenTelemetry trace spans, and the `audit_logs` INSERT happen in exactly one
code path. There is no risk of a developer adding a `QuickApproveAsync` shortcut that
bypasses the audit trail. In a domain where contractual territory release windows and
regulatory compliance require a complete audit history, this architectural guarantee matters
more than convenience. The audit log is not a best-effort add-on — it is structurally
impossible to skip.

**4. Integration tests read like specifications without mock ceremony.** A test constructs
a `TransitionCommand.SubmitCommand`, calls `ExecuteAsync` on the real `MovieLifecycleService`
backed by Testcontainers PostgreSQL, and pattern-matches the result:
`Assert.IsType<TransitionResult.TransitionBlocked>(result)` followed by
`Assert.Contains(blocked.Conflicts, c => c.CheckerName == "TitleConflictChecker")`. There
are no mock setups for `IConflictChecker`, no fake state machine stubs, no `Verify()` calls.
The sealed command record IS the test input and the sealed result record IS the assertion
target. Test readability scales linearly with the number of scenarios — each test is 8–12
lines of arrange-act-assert, not 30+ lines of mock configuration.

### Costs

**1. Per-endpoint authorization attributes cannot discriminate by command type.** In a
multi-method interface, you place `[Authorize(Roles = "Reviewer")]` on the `ApproveAsync`
method's controller endpoint and `[Authorize(Policy = "SystemAdmin")]` on
`OverrideAsync`. With single dispatch, the HTTP layer only knows about endpoints, not
command subtypes. We mitigate this with a separate `AdminMoviesController` gated by the
`SystemAdmin` policy, but the service must ALSO check authorization internally for
`OverrideCommand` because nothing structurally prevents a developer from accidentally
dispatching `new TransitionCommand.OverrideCommand(...)` from the regular
`MoviesController`. Authorization lives in two layers — ASP.NET attributes AND
service-internal role verification — which is harder to audit for correctness and creates
a subtle duplication-of-responsibility risk.

**2. IntelliSense discoverability is worse than a multi-method surface.** A new developer
exploring `IMovieLifecycleService` in Visual Studio or Rider sees exactly one method:
`ExecuteAsync(TransitionCommand, CancellationToken)`. To discover what operations exist,
they must navigate into the `TransitionCommand` class definition and read its nested sealed
records. With `SubmitAsync`, `ApproveAsync`, `RejectAsync`, `ReleaseAsync`,
`OverrideAsync` as separate methods, the completion list IS the documentation — all five
operations appear at a glance with their parameter signatures. This cost is real during
onboarding: new team members consistently report that sealed-hierarchy dispatch "feels like
a black box" until they internalize the pattern, typically taking 1–2 days of exposure.

**3. Every controller endpoint carries boilerplate for all five result variants.** The
Submit and Approve endpoints contain nearly identical 30-line switch expressions mapping
`TransitionResult` subtypes to HTTP responses. With a multi-method interface, `SubmitAsync`
could return a narrow `SubmitResult` type that excludes `OverrideApplied` (since a Submit
can never be an override), eliminating dead arms from the switch. The sealed hierarchy
forces every endpoint to handle every result variant even when some are structurally
impossible for that command. A shared `ToActionResult(this TransitionResult)` extension
method reduces the duplication to one line per endpoint, but the cognitive tax of knowing
that the `OverrideApplied` arm in the Submit endpoint is dead code remains.

**4. Cross-cutting middleware requires runtime type inspection.** If the operations team
wants per-command-type Prometheus metrics (e.g. `movie_transitions_total{command="Submit"}`)
or per-command rate limiting (throttle `SubmitCommand` to 10/minute per studio, leave
`ApproveCommand` unlimited), you cannot wrap individual methods with decorators. Instead,
you write a decorator around `IMovieLifecycleService` that switches on the runtime type of
the `TransitionCommand` inside `ExecuteAsync`. This is the same pattern as MediatR pipeline
behaviors — it works, but it means your metrics middleware mirrors the same `switch
(command)` dispatch that the service implementation performs internally. You have traded
compile-time method dispatch for runtime type inspection in every cross-cutting concern
layer. In practice, this bites when you need command-specific timeout policies or circuit
breakers: each policy requires another type-switch arm in the decorator.

### Verdict

For a lifecycle module whose non-negotiable invariant is "every transition must flow through
the same validation pipeline, conflict detection, and audit log," single-dispatch with sealed
hierarchies is the right choice — the audit-chokepoint guarantee and compiler-enforced
exhaustive result handling outweigh the boilerplate and discoverability costs.
*/

#endregion
