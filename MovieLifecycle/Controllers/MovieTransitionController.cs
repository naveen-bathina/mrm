// ============================================================================
// SECTION 2 — Controller Usage
// Demonstrates that the controller has ZERO knowledge of conflict checkers,
// state machine rules, audit logs, or override validation.
// ============================================================================

using Microsoft.AspNetCore.Mvc;
using MovieLifecycle.Types;

namespace MovieLifecycle.Controllers;

// WHY: Flat DTO with optional OverrideReason. The presence/absence of
// OverrideReason drives command selection via a ternary — no separate
// endpoints for standard vs override transitions. One endpoint, one DTO.
public sealed class TransitionRequest
{
    public MovieStatus TargetStatus { get; init; }
    public string? OverrideReason { get; init; }
}

// WHY: The controller is a thin HTTP adapter. It:
//   1. Parses the request into a TransitionCommand (via static factory + ternary)
//   2. Calls the single IMovieStatusService.TransitionAsync method
//   3. Maps the TransitionResult to HTTP status codes via Match<IActionResult>
// It has ZERO knowledge of:
//   - Which conflict checkers exist or when they run
//   - The state machine (which transitions are valid)
//   - Audit logging (happens in middleware)
//   - Override validation (happens in middleware)
//   - The pipeline structure or ordering
[ApiController]
[Route("api/movies")]
public sealed class MovieTransitionController : ControllerBase
{
    private readonly IMovieStatusService _service;

    public MovieTransitionController(IMovieStatusService service)
    {
        _service = service;
    }

    /// <summary>
    /// Transitions a movie to a new lifecycle status.
    /// Studio Admins call without OverrideReason for standard transitions.
    /// System Admins include OverrideReason to bypass hard conflict blocks.
    /// </summary>
    [HttpPost("{id:guid}/transition")]
    public async Task<IActionResult> Transition(
        Guid id,
        [FromBody] TransitionRequest request,
        CancellationToken ct)
    {
        // WHY: Ternary on OverrideReason — no if/else. The static factory methods
        // enforce that Override commands always have a reason (ArgumentException if
        // null/whitespace) and Standard commands never do. The ternary is the only
        // branching the controller needs to perform.
        var command = string.IsNullOrWhiteSpace(request.OverrideReason)
            ? TransitionCommand.Standard(id, request.TargetStatus)
            : TransitionCommand.Override(id, request.TargetStatus, request.OverrideReason);

        var result = await _service.TransitionAsync(command, ct);

        // WHY: Match<IActionResult> forces exhaustive handling of success/failure.
        // The controller maps error codes to HTTP status codes — it never inspects
        // conflict details, override state, or pipeline internals. The Match pattern
        // guarantees both branches produce an IActionResult.
        return result.Match<IActionResult>(
            onSuccess: status => Ok(new
            {
                movieId = id,
                newStatus = status.ToString()
            }),
            onFailure: error => error.Code switch
            {
                "INVALID_TRANSITION" => UnprocessableEntity(new
                {
                    error.Code,
                    error.Message
                }),
                "CONFLICT" => Conflict(new
                {
                    error.Code,
                    error.Message,
                    error.Conflicts
                }),
                "NOT_FOUND" => NotFound(new
                {
                    error.Code,
                    error.Message
                }),
                _ => StatusCode(500, new
                {
                    error.Code,
                    error.Message
                })
            });
    }
}
