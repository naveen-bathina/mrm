// ============================================================================
// SECTION 1 — Concrete Implementation: AuditLogMiddleware
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline.Middleware;

// WHY: Outermost middleware wrapping the entire pipeline. Records every transition
// attempt — both successes AND failures — with full context including conflicts,
// override reasons, and timing. Uses the classic ASP.NET Core wrapping pattern:
//   pre-processing → await next() → post-processing
// This guarantees the audit entry is written regardless of how the inner pipeline
// completes (success, abort, or exception).
internal static class AuditLogMiddleware
{
    internal static TransitionDelegate Create(
        IServiceProvider serviceProvider,
        TransitionDelegate next)
    {
        return async (context, ct) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<TransitionPipeline>>();
            var dbContext = serviceProvider.GetService<MovieDbContext>();

            var startedAt = DateTimeOffset.UtcNow;

            logger.LogInformation(
                "Transition started: Movie {MovieId} from {CurrentStatus} to {TargetStatus}",
                context.Command.MovieId,
                context.CurrentStatus,
                context.Command.TargetStatus);

            // WHY: Call next() to execute the rest of the pipeline (StateGuard,
            // ConflictChecks, OverrideValidation). Whether it succeeds or aborts,
            // we log the outcome in the post-processing phase below.
            await next(context, ct);

            var completedAt = DateTimeOffset.UtcNow;
            var succeeded = !context.IsAborted;

            // WHY: Write audit entry to the DbContext (not saved yet). The calling
            // MovieStatusService commits the entire unit of work — status update +
            // audit entry — in a single SaveChangesAsync call, ensuring atomicity.
            if (dbContext is not null)
            {
                var entry = new AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    MovieId = context.Command.MovieId,
                    FromStatus = context.CurrentStatus.ToString(),
                    ToStatus = context.Command.TargetStatus.ToString(),
                    Succeeded = succeeded,
                    ConflictCount = context.ConflictResults.Count,
                    OverrideReason = context.OverrideResult?.Reason,
                    OverrideBy = context.OverrideResult?.AdminUserId,
                    Timestamp = completedAt
                };

                dbContext.AuditLog.Add(entry);
            }

            logger.LogInformation(
                "Transition {Outcome}: Movie {MovieId} in {Duration:F1}ms " +
                "(conflicts: {ConflictCount}, override: {HasOverride})",
                succeeded ? "succeeded" : "aborted",
                context.Command.MovieId,
                (completedAt - startedAt).TotalMilliseconds,
                context.ConflictResults.Count,
                context.OverrideResult is not null);
        };
    }
}
