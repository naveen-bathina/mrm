// ============================================================================
// SECTION 1 — Concrete Implementation: MovieStatusService
// Shows how the service uses the visitor pattern to dispatch without casting.
// ============================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieLifecycle.Infrastructure;
using MovieLifecycle.Pipeline;
using MovieLifecycle.Types;

namespace MovieLifecycle.Services;

// WHY: Thin orchestration layer. Loads the movie, creates the context, runs the
// pipeline, and persists changes. ALL business logic lives in the pipeline middleware.
// This service is purely structural — it wires data access to pipeline execution.
// Scoped lifetime aligns with DbContext: one service instance per HTTP request.
public sealed class MovieStatusService : IMovieStatusService
{
    private readonly TransitionPipeline _pipeline;
    private readonly MovieDbContext _dbContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MovieStatusService> _logger;

    public MovieStatusService(
        TransitionPipeline pipeline,
        MovieDbContext dbContext,
        IServiceProvider serviceProvider,
        ILogger<MovieStatusService> logger)
    {
        _pipeline = pipeline;
        _dbContext = dbContext;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<TransitionResult> TransitionAsync(
        TransitionCommand command,
        CancellationToken ct)
    {
        // WHY: Visitor extracts TransitionMode for logging without casting the command.
        // This is double-dispatch in action: the command's Accept method calls the
        // correct Visit overload, and TransitionModeVisitor returns the mode.
        // No instanceof, no switch-on-type, no GetType() — pure polymorphic dispatch.
        var mode = command.Accept(TransitionModeVisitor.Instance);
        _logger.LogInformation(
            "Processing {Mode} transition for movie {MovieId} → {TargetStatus}",
            mode, command.MovieId, command.TargetStatus);

        // 1. Load movie from DB
        var movie = await _dbContext.Movies
            .FirstOrDefaultAsync(m => m.Id == command.MovieId, ct);

        if (movie is null)
        {
            // WHY: Early return via implicit conversion. The TransitionResult struct's
            // implicit operator converts TransitionError to TransitionResult automatically.
            return new TransitionError(
                "NOT_FOUND",
                $"Movie {command.MovieId} not found.",
                Array.Empty<ConflictResult>());
        }

        // 2. Create mutable context with current state from DB
        var context = new TransitionContext(command, movie.Status);

        // 3. Execute the pipeline — all validation, conflict checks, override
        //    logic, and audit logging happen inside the middleware chain
        await _pipeline.ExecuteAsync(context, _serviceProvider, ct);

        // 4. Always persist (SaveChanges writes audit log entries even on failure)
        if (context.IsAborted)
        {
            // WHY: Save even on abort — the AuditLogMiddleware added an audit entry
            // for the failed transition attempt. Persisting ensures the audit trail
            // captures both successful and blocked transitions.
            await _dbContext.SaveChangesAsync(ct);
            return context.AbortResult!.Value;
        }

        // 5. Pipeline passed — persist the state transition + audit entry atomically
        movie.Status = command.TargetStatus;
        await _dbContext.SaveChangesAsync(ct);

        // 6. Return success via implicit conversion (MovieStatus → TransitionResult)
        return command.TargetStatus;
    }
}
