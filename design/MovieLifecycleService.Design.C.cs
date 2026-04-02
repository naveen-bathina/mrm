// =============================================================================
// DESIGN C — Common Base Interface with Role-Specific Leaves
// MovieWorkflowService · C# 12 / .NET 8
// =============================================================================
//
// Design constraint:
//   IMovieLifecycle (base, no methods) ← IMovieWorkflow (regular transitions)
//                                      ← IMovieAdminWorkflow (admin override)
//   A single concrete class MovieWorkflowService implements both leaf interfaces.
//   Regular controllers depend on IMovieWorkflow; admin controllers on IMovieAdminWorkflow.
//
// =============================================================================

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 1 — Interface Signatures
// ─────────────────────────────────────────────────────────────────────────────

// ── 1a. Supporting Types ─────────────────────────────────────────────────────

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MovieReleaseManager.Movies.Lifecycle;

/// <summary>
/// The lifecycle states a movie progresses through.
/// Valid forward transitions: Draft → UnderReview → Approved → Released.
/// Lateral/terminal: UnderReview → Rejected (only from UnderReview).
/// </summary>
public enum MovieStatus
{
    Draft,
    UnderReview,
    Approved,
    Released,
    Rejected
}

/// <summary>
/// Roles that govern which lifecycle operations a caller may perform.
/// </summary>
public enum UserRole
{
    Editor,
    Reviewer,
    SystemAdmin
}

/// <summary>
/// Categorises why a lifecycle transition was rejected by the service.
/// </summary>
public enum FailureReason
{
    /// <summary>The requested status change violates the state-machine rules.</summary>
    InvalidTransition,

    /// <summary>One or more conflict checkers blocked the transition.</summary>
    ConflictDetected,

    /// <summary>The caller's role does not permit this operation.</summary>
    Unauthorized,

    /// <summary>No movie with the supplied identifier exists.</summary>
    MovieNotFound,

    /// <summary>An admin override was attempted without a written justification.</summary>
    OverrideReasonRequired,

    /// <summary>The audit log write failed — the transition was rolled back.</summary>
    AuditFailure
}

/// <summary>
/// Immutable result of every lifecycle transition attempt. Callers inspect
/// <see cref="Success"/> and branch on <see cref="FailureReason"/> to decide
/// the HTTP response. No exceptions for domain-level errors.
/// </summary>
public readonly struct TransitionOutcome : IEquatable<TransitionOutcome>
{
    /// <summary><c>true</c> when the transition was persisted successfully.</summary>
    public bool Success { get; }

    /// <summary>
    /// The categorised failure reason. <c>null</c> when <see cref="Success"/> is <c>true</c>.
    /// </summary>
    public FailureReason? FailureReason { get; }

    /// <summary>
    /// Human-readable detail about what blocked the transition — e.g. the conflict
    /// checker name and conflicting movie title. <c>null</c> on success or when no
    /// additional detail is available.
    /// </summary>
    public string? BlockingDetail { get; }

    private TransitionOutcome(bool success, FailureReason? failureReason, string? blockingDetail)
    {
        Success = success;
        FailureReason = failureReason;
        BlockingDetail = blockingDetail;
    }

    /// <summary>Creates a successful outcome.</summary>
    public static TransitionOutcome Ok() => new(true, null, null);

    /// <summary>Creates a failed outcome with the given reason and optional detail.</summary>
    public static TransitionOutcome Fail(FailureReason reason, string? blockingDetail = null) =>
        new(false, reason, blockingDetail);

    public bool Equals(TransitionOutcome other) =>
        Success == other.Success
        && FailureReason == other.FailureReason
        && BlockingDetail == other.BlockingDetail;

    public override bool Equals(object? obj) => obj is TransitionOutcome o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Success, FailureReason, BlockingDetail);
    public static bool operator ==(TransitionOutcome left, TransitionOutcome right) => left.Equals(right);
    public static bool operator !=(TransitionOutcome left, TransitionOutcome right) => !left.Equals(right);

    public override string ToString() => Success
        ? "TransitionOutcome { Success }"
        : $"TransitionOutcome {{ {FailureReason}: {BlockingDetail ?? "(no detail)"} }}";
}

// ── 1b. IMovieLifecycle — shared base interface ──────────────────────────────

/// <summary>
/// Marker base interface for all movie lifecycle operations.
/// <para>
/// <b>Purpose:</b> Establishes a common type root so that shared supporting types
/// (<see cref="MovieStatus"/>, <see cref="TransitionOutcome"/>, <see cref="FailureReason"/>)
/// have a single conceptual home, and both role-specific leaf interfaces
/// (<see cref="IMovieWorkflow"/> and <see cref="IMovieAdminWorkflow"/>) share a
/// compile-time lineage without forcing one to extend the other.
/// </para>
/// <para>
/// This interface carries <b>no methods</b>. It exists solely as a substitutability
/// anchor: code that needs to accept "any lifecycle service" (e.g. health checks,
/// DI diagnostics, or decorator registrations) can constrain on <c>IMovieLifecycle</c>
/// without coupling to a specific role's method surface.
/// </para>
/// <para>
/// All database access, conflict detection, state-machine enforcement, override
/// authorization, and audit logging are implementation concerns hidden behind the
/// leaf interfaces that derive from this base.
/// </para>
/// </summary>
public interface IMovieLifecycle { }

// ── 1c. IMovieWorkflow — regular lifecycle transitions ───────────────────────

/// <summary>
/// Standard lifecycle transitions available to studio users (Editors, Reviewers).
/// <para>
/// Each method encodes a single named business action. The implementation enforces
/// the state machine (Draft → UnderReview → Approved → Released; Rejected only
/// from UnderReview), runs the appropriate conflict checkers per transition, writes
/// audit log entries, and returns a <see cref="TransitionOutcome"/> — never throws
/// for domain errors.
/// </para>
/// </summary>
public interface IMovieWorkflow : IMovieLifecycle
{
    /// <summary>
    /// Transitions a movie from <see cref="MovieStatus.Draft"/> to
    /// <see cref="MovieStatus.UnderReview"/>.
    /// <para>
    /// Runs <c>TitleConflictChecker</c> and <c>PersonScheduleConflictChecker</c>.
    /// Returns <see cref="FailureReason.ConflictDetected"/> if either checker
    /// finds a blocking conflict.
    /// </para>
    /// </summary>
    /// <param name="movieId">Unique identifier of the movie to submit.</param>
    /// <param name="userId">Authenticated user requesting the transition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="TransitionOutcome.Ok"/> on success;
    /// <see cref="FailureReason.InvalidTransition"/> if the movie is not in Draft;
    /// <see cref="FailureReason.ConflictDetected"/> with blocking detail on conflict;
    /// <see cref="FailureReason.MovieNotFound"/> if the movie does not exist.
    /// </returns>
    Task<TransitionOutcome> SubmitForReview(Guid movieId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Transitions a movie from <see cref="MovieStatus.UnderReview"/> to
    /// <see cref="MovieStatus.Approved"/>.
    /// <para>
    /// Runs <c>ReleaseConflictChecker</c> and <c>PersonScheduleConflictChecker</c>.
    /// Returns <see cref="FailureReason.ConflictDetected"/> if either checker
    /// finds a blocking conflict.
    /// </para>
    /// </summary>
    /// <param name="movieId">Unique identifier of the movie to approve.</param>
    /// <param name="userId">Authenticated user requesting the transition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="TransitionOutcome.Ok"/> on success;
    /// <see cref="FailureReason.InvalidTransition"/> if the movie is not in UnderReview;
    /// <see cref="FailureReason.ConflictDetected"/> with blocking detail on conflict;
    /// <see cref="FailureReason.MovieNotFound"/> if the movie does not exist.
    /// </returns>
    Task<TransitionOutcome> Approve(Guid movieId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Transitions a movie from <see cref="MovieStatus.UnderReview"/> to
    /// <see cref="MovieStatus.Rejected"/>.
    /// <para>
    /// No conflict checkers are run. The rejection reason is recorded in the audit log.
    /// </para>
    /// </summary>
    /// <param name="movieId">Unique identifier of the movie to reject.</param>
    /// <param name="userId">Authenticated user requesting the rejection.</param>
    /// <param name="rejectionReason">
    /// Mandatory human-readable justification for the rejection.
    /// Persisted verbatim in the audit log.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="TransitionOutcome.Ok"/> on success;
    /// <see cref="FailureReason.InvalidTransition"/> if the movie is not in UnderReview;
    /// <see cref="FailureReason.MovieNotFound"/> if the movie does not exist.
    /// </returns>
    Task<TransitionOutcome> Reject(Guid movieId, Guid userId, string rejectionReason, CancellationToken ct = default);

    /// <summary>
    /// Transitions a movie from <see cref="MovieStatus.Approved"/> to
    /// <see cref="MovieStatus.Released"/>.
    /// <para>
    /// No conflict checkers are run at this stage. The release is recorded in the audit log.
    /// </para>
    /// </summary>
    /// <param name="movieId">Unique identifier of the movie to release.</param>
    /// <param name="userId">Authenticated user requesting the release.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="TransitionOutcome.Ok"/> on success;
    /// <see cref="FailureReason.InvalidTransition"/> if the movie is not in Approved;
    /// <see cref="FailureReason.MovieNotFound"/> if the movie does not exist.
    /// </returns>
    Task<TransitionOutcome> Release(Guid movieId, Guid userId, CancellationToken ct = default);
}

// ── 1d. IMovieAdminWorkflow — admin-only override ────────────────────────────

/// <summary>
/// Administrative override operations for System Admins who need to bypass
/// conflict-blocked transitions with a mandatory written justification.
/// <para>
/// This interface does <b>not</b> extend <see cref="IMovieWorkflow"/>. Admin
/// controllers depend on <see cref="IMovieAdminWorkflow"/> exclusively; they
/// cannot invoke regular transitions through this interface. This separation
/// ensures that admin override capability is never accidentally exposed through
/// a standard controller injection.
/// </para>
/// </summary>
public interface IMovieAdminWorkflow : IMovieLifecycle
{
    /// <summary>
    /// Forces a movie into <paramref name="targetStatus"/>, bypassing any conflict
    /// checkers that would normally block the transition.
    /// <para>
    /// The state-machine rules still apply — you cannot override an invalid transition
    /// (e.g. Draft → Released). Only conflict blocks are bypassed.
    /// </para>
    /// <para>
    /// The override reason, admin identity, conflict snapshot, and target status are
    /// all recorded in the audit log as an <c>OverrideApplied</c> event.
    /// </para>
    /// </summary>
    /// <param name="movieId">Unique identifier of the movie to override.</param>
    /// <param name="targetStatus">The desired target status to force the movie into.</param>
    /// <param name="adminUserId">
    /// The System Admin's user identifier. Must hold <see cref="UserRole.SystemAdmin"/> role.
    /// </param>
    /// <param name="overrideReason">
    /// Mandatory written justification for the override. Must be non-empty.
    /// Persisted verbatim in the audit log.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="TransitionOutcome.Ok"/> on success;
    /// <see cref="FailureReason.InvalidTransition"/> if the state machine rejects the move;
    /// <see cref="FailureReason.Unauthorized"/> if the caller is not a System Admin;
    /// <see cref="FailureReason.OverrideReasonRequired"/> if the reason is empty/whitespace;
    /// <see cref="FailureReason.MovieNotFound"/> if the movie does not exist.
    /// </returns>
    Task<TransitionOutcome> AdminOverride(
        Guid movieId,
        MovieStatus targetStatus,
        Guid adminUserId,
        string overrideReason,
        CancellationToken ct = default);
}

// ── 1e. Concrete class — constructor and private fields ──────────────────────

// ── Internal types (NEVER exposed to callers) ────────────────────────────────
//
// These classes are internal to the assembly. They query the unscoped DbContext
// directly against PostgreSQL across all studios.

// internal sealed class TitleConflictChecker(MovieDbContext db)
// {
//     internal Task<ConflictReport> CheckAsync(Guid movieId, CancellationToken ct) => ...;
// }
//
// internal sealed class ReleaseConflictChecker(MovieDbContext db)
// {
//     internal Task<ConflictReport> CheckAsync(Guid movieId, CancellationToken ct) => ...;
// }
//
// internal sealed class PersonScheduleConflictChecker(MovieDbContext db)
// {
//     internal Task<ConflictReport> CheckAsync(Guid movieId, CancellationToken ct) => ...;
// }
//
// internal sealed record ConflictReport(bool IsBlocked, string? Detail);

/// <summary>
/// Single implementation of both <see cref="IMovieWorkflow"/> and
/// <see cref="IMovieAdminWorkflow"/>. Owns the state machine, conflict-checker
/// orchestration, override authorization, and audit logging. Registered as
/// scoped — one instance per HTTP request — aligned with the EF Core DbContext lifetime.
/// </summary>
public sealed class MovieWorkflowService : IMovieWorkflow, IMovieAdminWorkflow
{
    private readonly MovieDbContext _db;
    private readonly TitleConflictChecker _titleChecker;
    private readonly ReleaseConflictChecker _releaseChecker;
    private readonly PersonScheduleConflictChecker _scheduleChecker;
    private readonly ILogger<MovieWorkflowService> _logger;

    public MovieWorkflowService(
        MovieDbContext db,
        ILogger<MovieWorkflowService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Checkers are internal implementation details — instantiated directly
        // with the shared DbContext, never resolved from DI, never exposed.
        _titleChecker = new TitleConflictChecker(db);
        _releaseChecker = new ReleaseConflictChecker(db);
        _scheduleChecker = new PersonScheduleConflictChecker(db);
    }

    public Task<TransitionOutcome> SubmitForReview(Guid movieId, Guid userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<TransitionOutcome> Approve(Guid movieId, Guid userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<TransitionOutcome> Reject(Guid movieId, Guid userId, string rejectionReason, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<TransitionOutcome> Release(Guid movieId, Guid userId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<TransitionOutcome> AdminOverride(Guid movieId, MovieStatus targetStatus, Guid adminUserId, string overrideReason, CancellationToken ct = default)
        => throw new NotImplementedException();
}

// ── 1f. DI Registration — Program.cs ─────────────────────────────────────────

// Scoped lifetime because:
// 1. MovieDbContext is scoped (one per HTTP request) — the service must share that scope.
// 2. The three internal conflict checkers hold a reference to the same DbContext instance,
//    so they participate in the same EF Core change tracker and transaction.
// 3. Singleton would hold a stale DbContext across requests; transient would create
//    multiple DbContext instances within a single request pipeline.

/*

// In Program.cs:

builder.Services.AddDbContext<MovieDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("MovieDb")));

// Register the single concrete instance as scoped, then alias both interfaces
// to the SAME scoped instance — avoids double-instantiation.
builder.Services.AddScoped<MovieWorkflowService>();

builder.Services.AddScoped<IMovieWorkflow>(sp =>
    sp.GetRequiredService<MovieWorkflowService>());

builder.Services.AddScoped<IMovieAdminWorkflow>(sp =>
    sp.GetRequiredService<MovieWorkflowService>());

*/


// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 2 — Usage Examples
// ─────────────────────────────────────────────────────────────────────────────

// ── 2a. MoviesController — regular transitions (injects IMovieWorkflow only) ─

/// <summary>
/// Handles standard movie lifecycle transitions for studio users.
/// Depends exclusively on <see cref="IMovieWorkflow"/> — has no access to admin overrides.
/// </summary>
[ApiController]
[Route("api/movies")]
[Authorize(Policy = "StudioUserPolicy")]
public class MoviesController(IMovieWorkflow workflow) : ControllerBase
{
    /// <summary>
    /// Submit a Draft movie for review. Runs title and person-schedule conflict checks.
    /// </summary>
    [HttpPost("{movieId:guid}/submit")]
    public async Task<IActionResult> SubmitForReview(Guid movieId, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var outcome = await workflow.SubmitForReview(movieId, userId, ct);

        if (outcome.Success)
        {
            return Ok(new { movieId, status = MovieStatus.UnderReview.ToString() });
        }

        return outcome.FailureReason switch
        {
            Lifecycle.FailureReason.ConflictDetected => Conflict(new
            {
                error = "ConflictDetected",
                detail = outcome.BlockingDetail
            }),

            Lifecycle.FailureReason.MovieNotFound => NotFound(new
            {
                error = "MovieNotFound",
                movieId
            }),

            Lifecycle.FailureReason.InvalidTransition => UnprocessableEntity(new
            {
                error = "InvalidTransition",
                detail = outcome.BlockingDetail
            }),

            _ => StatusCode(500, new
            {
                error = "InternalError",
                detail = "An unexpected error occurred during the transition."
            })
        };
    }

    [HttpPost("{movieId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid movieId, CancellationToken ct)
    {
        var outcome = await workflow.Approve(movieId, GetCurrentUserId(), ct);
        return MapOutcome(outcome, movieId, MovieStatus.Approved);
    }

    [HttpPost("{movieId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid movieId,
        [FromBody] RejectRequest request,
        CancellationToken ct)
    {
        var outcome = await workflow.Reject(movieId, GetCurrentUserId(), request.Reason, ct);
        return MapOutcome(outcome, movieId, MovieStatus.Rejected);
    }

    [HttpPost("{movieId:guid}/release")]
    public async Task<IActionResult> Release(Guid movieId, CancellationToken ct)
    {
        var outcome = await workflow.Release(movieId, GetCurrentUserId(), ct);
        return MapOutcome(outcome, movieId, MovieStatus.Released);
    }

    private IActionResult MapOutcome(TransitionOutcome outcome, Guid movieId, MovieStatus targetStatus)
    {
        if (outcome.Success)
            return Ok(new { movieId, status = targetStatus.ToString() });

        return outcome.FailureReason switch
        {
            Lifecycle.FailureReason.ConflictDetected    => Conflict(new { error = "ConflictDetected", detail = outcome.BlockingDetail }),
            Lifecycle.FailureReason.MovieNotFound        => NotFound(new { error = "MovieNotFound", movieId }),
            Lifecycle.FailureReason.InvalidTransition    => UnprocessableEntity(new { error = "InvalidTransition", detail = outcome.BlockingDetail }),
            Lifecycle.FailureReason.Unauthorized         => Forbid(),
            _                                            => StatusCode(500, new { error = "InternalError" })
        };
    }

    private Guid GetCurrentUserId() =>
        Guid.Parse(User.FindFirst("sub")!.Value);
}

public record RejectRequest(string Reason);

// ── 2b. AdminMoviesController — override (injects IMovieAdminWorkflow only) ──

/// <summary>
/// Handles System Admin override operations.
/// Depends exclusively on <see cref="IMovieAdminWorkflow"/> — cannot invoke
/// regular transitions; admin must use the standard controller for those.
/// </summary>
[ApiController]
[Route("api/admin/movies")]
[Authorize(Policy = "SystemAdminPolicy")]
public class AdminMoviesController(IMovieAdminWorkflow adminWorkflow) : ControllerBase
{
    /// <summary>
    /// Force-override a transition that was hard-blocked by conflicts.
    /// Requires a written justification that is permanently recorded in the audit log.
    /// </summary>
    [HttpPost("{movieId:guid}/override")]
    public async Task<IActionResult> OverrideTransition(
        Guid movieId,
        [FromBody] AdminOverrideRequest request,
        CancellationToken ct)
    {
        var adminUserId = GetCurrentUserId();

        var outcome = await adminWorkflow.AdminOverride(
            movieId,
            request.TargetStatus,
            adminUserId,
            request.OverrideReason,
            ct);

        if (outcome.Success)
        {
            return Ok(new
            {
                movieId,
                status = request.TargetStatus.ToString(),
                overrideApplied = true
            });
        }

        return outcome.FailureReason switch
        {
            Lifecycle.FailureReason.OverrideReasonRequired => BadRequest(new
            {
                error = "OverrideReasonRequired",
                detail = "A written justification is mandatory for admin overrides."
            }),

            Lifecycle.FailureReason.Unauthorized => StatusCode(403, new
            {
                error = "Unauthorized",
                detail = "Only System Admin users can perform overrides."
            }),

            Lifecycle.FailureReason.MovieNotFound => NotFound(new
            {
                error = "MovieNotFound",
                movieId
            }),

            Lifecycle.FailureReason.InvalidTransition => UnprocessableEntity(new
            {
                error = "InvalidTransition",
                detail = outcome.BlockingDetail
            }),

            _ => StatusCode(500, new
            {
                error = "InternalError",
                detail = "An unexpected error occurred during the override."
            })
        };
    }

    private Guid GetCurrentUserId() =>
        Guid.Parse(User.FindFirst("sub")!.Value);
}

public record AdminOverrideRequest(MovieStatus TargetStatus, string OverrideReason);


// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 3 — What Complexity It Hides (callers NEVER need to know)
// ─────────────────────────────────────────────────────────────────────────────

/*
 1. State-machine rules — which transitions are valid from which status (Draft →
    UnderReview → Approved → Released; Rejected only from UnderReview). Callers
    call a named method and get back InvalidTransition if it's wrong.

 2. Conflict-checker selection per transition — SubmitForReview runs
    TitleConflictChecker + PersonScheduleConflictChecker; Approve runs
    ReleaseConflictChecker + PersonScheduleConflictChecker; Reject and Release
    run none. This policy table is entirely internal.

 3. TitleConflictChecker implementation — Unicode NFKD normalization, diacritics
    stripping, case-insensitive comparison against all studios. The checker type
    itself is internal.

 4. ReleaseConflictChecker implementation — same-territory/same-date detection
    across all studios' release schedules. Internal type.

 5. PersonScheduleConflictChecker implementation — PostgreSQL daterange overlap
    via the && operator across all productions for a person. Internal type.

 6. ConflictReport aggregation — the internal ConflictReport type collects
    results from multiple checkers, decides whether the aggregate is a hard
    block, and produces the BlockingDetail string. Never exposed.

 7. Cross-studio data scoping bypass — conflict checkers use
    db.Movies.IgnoreQueryFilters() to read all studios' data. Callers have no
    idea this happens; their own DbContext remains studio-scoped.

 8. Admin override authorization — AdminOverride verifies the caller holds
    SystemAdmin role internally. The admin controller's [Authorize] policy is a
    first gate, but the service double-checks. Callers don't manage this.

 9. Audit logging — every transition (success or failure) and every override
    writes to the AuditLog table with actor ID, timestamp, from/to status,
    conflict snapshot, and override reason. Callers never call an audit service.

10. Transactional integrity — the status update, conflict check, audit log write,
    and (for overrides) conflict snapshot capture all happen within a single
    IDbContextTransaction. If any step fails, everything rolls back. Callers
    receive an atomic TransitionOutcome.

11. EF Core and PostgreSQL details — the Npgsql provider, connection string,
    query construction, IgnoreQueryFilters(), daterange operators, and GiST
    index usage are all internal. Callers depend only on the interface.

12. Checker instantiation strategy — the three checkers are new'd up in the
    constructor with the shared DbContext. Callers don't know whether checkers
    are DI-resolved, new'd, or pooled.
*/


// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 4 — Dependency Strategy
// ─────────────────────────────────────────────────────────────────────────────

// ── 4a–4c. Full constructor with EF Core and private checker instantiation ───

// (Shown inline in the MovieWorkflowService class above in Section 1e.
//  Repeated here for clarity of the dependency strategy narrative.)

/*

public sealed class MovieWorkflowService : IMovieWorkflow, IMovieAdminWorkflow
{
    // 4a. EF Core DbContext — injected via constructor, NEVER exposed to callers.
    private readonly MovieDbContext _db;

    // 4b. Conflict checkers — private, constructed inside the constructor with
    //     the shared DbContext. Not DI-resolved. Not on any interface.
    private readonly TitleConflictChecker _titleChecker;
    private readonly ReleaseConflictChecker _releaseChecker;
    private readonly PersonScheduleConflictChecker _scheduleChecker;

    private readonly ILogger<MovieWorkflowService> _logger;

    // 4c. Full constructor signature — two DI-resolved params, three internal instantiations.
    public MovieWorkflowService(
        MovieDbContext db,
        ILogger<MovieWorkflowService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _titleChecker = new TitleConflictChecker(db);
        _releaseChecker = new ReleaseConflictChecker(db);
        _scheduleChecker = new PersonScheduleConflictChecker(db);
    }
}

*/

// ── 4d. Full DI wiring in Program.cs ─────────────────────────────────────────

/*

// ── Program.cs ───────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Register EF Core with Npgsql for PostgreSQL.
builder.Services.AddDbContext<MovieDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("MovieDb"),
        npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3)));

// Register the concrete service as scoped (matches DbContext lifetime).
// This is the SINGLE instance that serves both interfaces per request.
builder.Services.AddScoped<MovieWorkflowService>();

// Alias both leaf interfaces to the SAME scoped MovieWorkflowService instance.
// Using the factory overload of AddScoped ensures that resolving either interface
// within the same scope returns the identical object — no double-instantiation,
// no duplicate DbContext, no split change-tracker state.
builder.Services.AddScoped<IMovieWorkflow>(sp =>
    sp.GetRequiredService<MovieWorkflowService>());

builder.Services.AddScoped<IMovieAdminWorkflow>(sp =>
    sp.GetRequiredService<MovieWorkflowService>());

// Result: within a single HTTP request scope:
//   IMovieWorkflow          → MovieWorkflowService@0x1A
//   IMovieAdminWorkflow     → MovieWorkflowService@0x1A  (same instance)
//   MovieWorkflowService    → MovieWorkflowService@0x1A  (same instance)
//
// Different HTTP requests get different instances (scoped).

var app = builder.Build();

// ... controller mapping, middleware, etc.

app.Run();

*/


// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 5 — Trade-offs of This Specific Design
// ─────────────────────────────────────────────────────────────────────────────

/*

## What You GAIN from the shared base `IMovieLifecycle`

The `IMovieLifecycle` base gives the two leaf interfaces a common compile-time
ancestor without forcing one to extend the other. This matters in three concrete
ways. First, any infrastructure code that needs to say "I accept any lifecycle
service" — a health check that verifies the service is registered, a decorator
that adds logging or metrics around lifecycle calls, or a DI diagnostic that
enumerates all lifecycle-related registrations — can constrain on
`IMovieLifecycle` rather than picking one leaf arbitrarily. Second, the shared
ancestry communicates intent: anyone reading the interface hierarchy instantly
sees that `IMovieWorkflow` and `IMovieAdminWorkflow` are siblings in the same
domain, not unrelated services that happen to share some enums. The type system
documents the relationship. Third, the shared base means the supporting types
(`MovieStatus`, `TransitionOutcome`, `FailureReason`) live in a conceptual
namespace anchored to `IMovieLifecycle`, making it clear that both leaf
interfaces operate on the same vocabulary. If a third leaf ever appeared (say,
`IMovieBatchWorkflow` for bulk operations), it would naturally extend
`IMovieLifecycle` and callers would not need to invent a new common root.

## What You GIVE UP

The base interface is a pure marker — it has no methods. This means the
"substitutability" it provides is extremely weak: you can pass an
`IMovieWorkflow` where an `IMovieLifecycle` is expected, but you cannot *do*
anything with it once you have it. The common root adds a layer to the type
hierarchy that callers must understand conceptually even if they never interact
with it directly. IntelliSense will show `IMovieLifecycle` as a base, which
adds noise to the type tree without adding callable surface. Additionally, the
two leaf interfaces are fully independent: injecting `IMovieAdminWorkflow` gives
you zero access to `SubmitForReview` or any regular transition. This is a
feature for security isolation, but it means any code that needs *both*
capabilities must take two dependencies — there is no single interface that
unifies them. In testing, this means you may need to cast the service or inject
it twice if a test scenario spans both regular and admin operations.

## When This Design is RIGHT vs. When a Single Interface Would Be Better

This three-interface hierarchy is right when the two call sites (regular
controllers and admin controllers) are genuinely separate — deployed behind
different authorization policies, possibly in different assemblies, with
different security review requirements. The separation makes it impossible for a
regular controller to accidentally call `AdminOverride` because its injected
type literally doesn't have that method. It's also right when the team is large
enough that "admin can override" is a sensitive capability that should be
discoverable only by people reading `IMovieAdminWorkflow`, not buried as one
more method in a long interface.

A simpler single-interface design (`IMovieLifecycleService` with all five
methods) would be better when: the team is small and everyone is trusted; there
is no security boundary between regular and admin operations; or the admin
override is rare enough that the risk of accidental invocation is negligible.
In that case, the three-level hierarchy adds cognitive overhead without a
commensurate safety benefit.

## Is `IMovieLifecycle` "Deep" or "Shallow"?

`IMovieLifecycle` is intentionally shallow — it is a marker interface with zero
methods. Its depth is zero. The *leaf* interfaces are where the depth lives:
`IMovieWorkflow` has four methods that each hide state-machine validation,
conflict-checker orchestration, cross-studio scoping bypass, audit logging, and
transactional integrity. `IMovieAdminWorkflow` has one method that hides
override authorization, conflict snapshot capture, and audit recording. The
total hidden complexity behind the five methods is enormous — the interface-to-
implementation ratio is extremely favorable.

The shallow base is acceptable here because its job is not to *do* anything —
it exists to *organize* the type hierarchy and provide a compile-time grouping
point. If the base interface were given methods just to make it "deep" (e.g.,
moving `GetMovieStatus()` onto it), that would blur the role-specific
separation that is the whole point of this design. A marker base that says
"I am a movie lifecycle service" is the right level of abstraction for a
type whose sole purpose is to anchor two role-specific contracts.

*/
