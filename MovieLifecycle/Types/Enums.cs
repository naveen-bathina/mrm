// ============================================================================
// SECTION 1 — Interface Signatures: Enums
// ============================================================================

namespace MovieLifecycle.Types;

/// <summary>
/// Lifecycle states for a movie. Numeric ordering enforces forward-only progression.
/// </summary>
// WHY: Explicit numeric values document the allowed ordering and prevent
// accidental reordering of enum members from breaking the state guard.
public enum MovieStatus
{
    Draft = 0,
    Registered = 1,
    InProduction = 2,
    Released = 3,
    Archived = 4
}

// WHY: Inferred from command type via visitor, never supplied by the caller.
// This makes it impossible to create a "Standard" command with override semantics.
public enum TransitionMode
{
    Standard,
    Override
}

// WHY: Two-tier severity drives override policy. Warnings are informational only;
// Hard conflicts block standard transitions but can be overridden by System Admin.
public enum ConflictSeverity
{
    Warning,
    Hard
}
