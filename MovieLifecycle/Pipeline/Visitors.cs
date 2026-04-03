// ============================================================================
// SECTION 1 — Concrete Implementation: Visitor Implementations
// ============================================================================

using MovieLifecycle.Types;

namespace MovieLifecycle.Pipeline;

// WHY: Extracts TransitionMode from the command type via double-dispatch — no casting.
// Used by OverrideValidationMiddleware and MovieStatusService to branch on mode.
// Singleton instance: stateless, thread-safe, zero allocation per use.
internal sealed class TransitionModeVisitor : ITransitionCommandVisitor<TransitionMode>
{
    public static readonly TransitionModeVisitor Instance = new();

    public TransitionMode Visit(TransitionCommand.StandardTransitionCommand command)
        => TransitionMode.Standard;

    public TransitionMode Visit(TransitionCommand.OverrideTransitionCommand command)
        => TransitionMode.Override;
}

// WHY: Extracts the override reason string without casting. Returns null for standard
// commands. Used by AuditLogMiddleware and OverrideValidationMiddleware to capture
// the reason for the audit trail without knowing the concrete command type.
internal sealed class OverrideReasonVisitor : ITransitionCommandVisitor<string?>
{
    public static readonly OverrideReasonVisitor Instance = new();

    public string? Visit(TransitionCommand.StandardTransitionCommand command)
        => null;

    public string? Visit(TransitionCommand.OverrideTransitionCommand command)
        => command.Reason;
}

// WHY: Builds the full OverrideResult (including admin identity and timestamp) from
// the command via visitor dispatch. Returns null for standard commands. Encapsulates
// the OverrideResult construction logic so middleware doesn't need to know command internals.
internal sealed class OverrideResultVisitor : ITransitionCommandVisitor<OverrideResult?>
{
    private readonly string _adminUserId;

    internal OverrideResultVisitor(string adminUserId)
    {
        _adminUserId = adminUserId;
    }

    public OverrideResult? Visit(TransitionCommand.StandardTransitionCommand command)
        => null;

    public OverrideResult? Visit(TransitionCommand.OverrideTransitionCommand command)
        => new OverrideResult(_adminUserId, command.Reason, DateTimeOffset.UtcNow);
}
