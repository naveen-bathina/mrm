// ============================================================================
// SECTION 1 — Interface Signatures: Records (value objects)
// ============================================================================

namespace MovieLifecycle.Types;

// WHY: Immutable record ties each conflict back to its originating checker.
// CheckerName enables targeted override decisions and audit reporting.
public sealed record ConflictResult(
    string CheckerName,
    ConflictSeverity Severity,
    string Message);

// WHY: Captures full override context (who, why, when) for the audit trail.
// Timestamp is system-set to prevent clock manipulation by callers.
public sealed record OverrideResult(
    string AdminUserId,
    string Reason,
    DateTimeOffset Timestamp);

// WHY: Structured error with machine-readable Code for API consumers,
// human-readable Message for UI/logs, and the full conflict list for transparency.
// Conflicts is never null — empty list when no conflicts are relevant.
public sealed record TransitionError(
    string Code,
    string Message,
    IReadOnlyList<ConflictResult> Conflicts);
