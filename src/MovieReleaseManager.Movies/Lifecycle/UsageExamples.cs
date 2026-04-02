using System.Collections.Immutable;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieReleaseManager.Movies.Lifecycle;

namespace MovieReleaseManager.Api.Controllers;

// ──────────────────────────────────────────────────────────────────
//  Usage Example (a): Studio controller — normal Submit transition
// ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/movies")]
[Authorize(Policy = "StudioAdminPolicy")]
public class MoviesController(IMovieLifecycleService lifecycle) : ControllerBase
{
    [HttpPost("{movieId:guid}/submit")]
    public async Task<IActionResult> SubmitForReview(
        Guid movieId, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();

        var result = await lifecycle.ExecuteAsync(
            new SubmitForReviewCommand(movieId, actorId), ct);

        return result switch
        {
            TransitionSucceeded ok => Ok(new
            {
                ok.MovieId,
                Status = ok.NewStatus.ToString(),
                Warnings = ok.Warnings.Select(w => new
                {
                    w.Type,
                    Severity = w.Severity.ToString(),
                    w.Detail
                })
            }),

            TransitionBlocked blocked => Conflict(new
            {
                Blocked = true,
                Conflicts = blocked.Conflicts.Select(c => new
                {
                    c.Type,
                    Severity = c.Severity.ToString(),
                    c.Detail
                })
            }),

            InvalidTransition invalid => UnprocessableEntity(new
            {
                Error = invalid.Reason
            }),

            MovieNotFound => NotFound(),

            ValidationFailed vf => BadRequest(new { Errors = vf.Errors }),

            _ => StatusCode(500)
        };
    }

    // Similarly for Approve, Reject, Release, Withdraw — same pattern,
    // different command record. Example:

    [HttpPost("{movieId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid movieId, CancellationToken ct)
    {
        var result = await lifecycle.ExecuteAsync(
            new ApproveCommand(movieId, GetCurrentUserId()), ct);

        return MapResult(result);
    }

    [HttpPost("{movieId:guid}/reject")]
    public async Task<IActionResult> Reject(
        Guid movieId, [FromBody] RejectRequest request, CancellationToken ct)
    {
        var result = await lifecycle.ExecuteAsync(
            new RejectCommand(movieId, GetCurrentUserId(), request.Reason), ct);

        return MapResult(result);
    }

    [HttpPost("{movieId:guid}/release")]
    public async Task<IActionResult> Release(Guid movieId, CancellationToken ct)
    {
        var result = await lifecycle.ExecuteAsync(
            new ReleaseCommand(movieId, GetCurrentUserId()), ct);

        return MapResult(result);
    }

    [HttpPost("{movieId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(
        Guid movieId, [FromBody] WithdrawRequest request, CancellationToken ct)
    {
        var result = await lifecycle.ExecuteAsync(
            new WithdrawCommand(movieId, GetCurrentUserId(), request.Reason), ct);

        return MapResult(result);
    }

    private IActionResult MapResult(TransitionResult result) => result switch
    {
        TransitionSucceeded ok => Ok(new
        {
            ok.MovieId,
            Status = ok.NewStatus.ToString(),
            Warnings = ok.Warnings.Select(w => new { w.Type, Severity = w.Severity.ToString(), w.Detail })
        }),
        TransitionBlocked blocked => Conflict(new
        {
            Blocked = true,
            Conflicts = blocked.Conflicts.Select(c => new { c.Type, Severity = c.Severity.ToString(), c.Detail })
        }),
        InvalidTransition invalid => UnprocessableEntity(new { Error = invalid.Reason }),
        MovieNotFound => NotFound(),
        ValidationFailed vf => BadRequest(new { Errors = vf.Errors }),
        _ => StatusCode(500)
    };

    private Guid GetCurrentUserId() =>
        Guid.Parse(User.FindFirst("sub")!.Value);
}

public record RejectRequest(string Reason);
public record WithdrawRequest(string Reason);

// ──────────────────────────────────────────────────────────────────
//  Usage Example (b): Admin controller — override a blocked transition
// ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/admin/movies")]
[Authorize(Policy = "SystemAdminPolicy")]
public class AdminMoviesController(IMovieLifecycleService lifecycle) : ControllerBase
{
    /// <summary>
    /// Force-override a transition that was hard-blocked by conflicts.
    /// Requires a written justification that is permanently recorded
    /// in the audit log.
    /// </summary>
    [HttpPost("{movieId:guid}/override")]
    public async Task<IActionResult> OverrideTransition(
        Guid movieId,
        [FromBody] AdminOverrideRequest request,
        CancellationToken ct)
    {
        var result = await lifecycle.ExecuteAsync(
            new AdminOverrideCommand(
                movieId,
                GetCurrentUserId(),
                request.TargetStatus,
                request.Reason),
            ct);

        return result switch
        {
            TransitionSucceeded ok => Ok(new
            {
                ok.MovieId,
                Status = ok.NewStatus.ToString(),
                OverrideApplied = true
            }),

            InvalidTransition invalid => UnprocessableEntity(new
            {
                Error = invalid.Reason
            }),

            MovieNotFound => NotFound(),

            ValidationFailed vf => BadRequest(new { Errors = vf.Errors }),

            // AdminOverride should never produce TransitionBlocked — the
            // whole point is to bypass blocks. If it does, something is
            // seriously wrong internally.
            _ => StatusCode(500)
        };
    }

    private Guid GetCurrentUserId() =>
        Guid.Parse(User.FindFirst("sub")!.Value);
}

public record AdminOverrideRequest(MovieStatus TargetStatus, string Reason);
