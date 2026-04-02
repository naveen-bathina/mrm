# IMovieLifecycleService — Interface Design

## Interface Signature

See [`IMovieLifecycleService.cs`](IMovieLifecycleService.cs) — the full contract with all types.

**Surface area: 1 interface, 1 method, 6 command records, 5 result records.**

```
IMovieLifecycleService.ExecuteAsync(MovieCommand, CancellationToken) → TransitionResult
```

### Type Map

| Category | Type | Purpose |
|----------|------|---------|
| **Base command** | `MovieCommand` | Abstract base; carries `MovieId` + `ActorUserId` |
| **Commands** | `SubmitForReviewCommand` | Draft → UnderReview |
| | `ApproveCommand` | UnderReview → Approved |
| | `RejectCommand` | UnderReview → Rejected (requires reason) |
| | `ReleaseCommand` | Approved → Released |
| | `WithdrawCommand` | Any eligible → Withdrawn (requires reason) |
| | `AdminOverrideCommand` | Force any blocked transition (requires reason + target status) |
| **Base result** | `TransitionResult` | Abstract base; pattern-match on subtypes |
| **Results** | `TransitionSucceeded` | Happy path — includes warnings |
| | `TransitionBlocked` | Hard conflicts prevent transition |
| | `InvalidTransition` | State machine rejects the move |
| | `MovieNotFound` | No such movie |
| | `ValidationFailed` | Malformed command (blank reason, etc.) |

### Conflict Policy (internal, callers never configure this)

| Transition | Checkers Run |
|------------|-------------|
| Submit (Draft → UnderReview) | `TitleConflictChecker` (hard), `PersonScheduleConflictChecker` (hard) |
| Approve (UnderReview → Approved) | `ReleaseConflictChecker` (soft), `PersonScheduleConflictChecker` (hard) |
| Reject, Release, Withdraw | None |

---

## Usage Examples

See [`UsageExamples.cs`](UsageExamples.cs) for full compilable controller code.

**Pattern:** Construct command → call `ExecuteAsync` → pattern-match on result → map to HTTP status.

---

## What Complexity It Hides

Callers never see, touch, or configure any of the following:

- **State machine validation** — which transitions are legal from which status, including the asymmetric rules (Withdraw is valid from 3 different statuses; Reject only from UnderReview). The implementation owns the full transition table and rejects invalid moves with a human-readable `InvalidTransition` result.

- **Conflict checker selection and orchestration** — which checkers run at which transition is an internal policy table. Callers don't know that Submit runs two checkers while Approve runs two different ones. They dispatch a command and get back either success or a block with conflict details.

- **Conflict checker implementations** — `TitleConflictChecker` (Unicode NFKD normalization, diacritics stripping, case-insensitive comparison against all studios), `ReleaseConflictChecker` (same-territory/same-date detection), `PersonScheduleConflictChecker` (PostgreSQL `daterange` overlap via `&&` operator across all productions). None of these types appear in the public API.

- **Cross-studio data scoping bypass** — conflict detection queries must read *all* studios' data to detect global conflicts, even though the calling studio normally only sees its own data. The implementation uses an unscoped `DbContext` internally; callers have no idea this happens.

- **Hard vs. soft conflict aggregation** — the implementation collects all conflict results, separates hard blocks from soft warnings, and decides whether to block or proceed. Callers receive a pre-classified `TransitionBlocked` or `TransitionSucceeded` (with warnings attached).

- **Admin override authorization and enforcement** — when an `AdminOverrideCommand` is dispatched, the implementation verifies the actor has System Admin role, re-runs the conflict checkers to capture what's being overridden, then bypasses the block. All of this is internal.

- **Audit logging** — every successful transition, every blocked attempt, and every override (with the mandatory reason, actor ID, timestamp, conflict snapshot) is written to the `AuditLog` table. Callers never call an audit service.

- **Transactional integrity** — the status update, conflict check, audit log write, and notification dispatch all happen within a single database transaction. If any step fails, the entire operation rolls back. Callers get an atomic result.

- **ConflictReport aggregation** — the internal `ConflictReport` type collects results from multiple checkers and computes the aggregate `Blocked` flag. This type is never exposed.

---

## Dependency Strategy

### Registration

```csharp
// In Program.cs or a DI composition root
services.AddScoped<IMovieLifecycleService, MovieLifecycleService>();

// Internal dependencies — registered but never consumed outside the module
services.AddScoped<TitleConflictChecker>();
services.AddScoped<ReleaseConflictChecker>();
services.AddScoped<PersonScheduleConflictChecker>();
```

### EF Core DbContext

```csharp
// The module's implementation takes a scoped MrmDbContext
internal sealed class MovieLifecycleService : IMovieLifecycleService
{
    private readonly MrmDbContext _db;
    private readonly TitleConflictChecker _titleChecker;
    private readonly ReleaseConflictChecker _releaseChecker;
    private readonly PersonScheduleConflictChecker _scheduleChecker;
    private readonly TimeProvider _clock;    // for testable timestamps

    public MovieLifecycleService(
        MrmDbContext db,
        TitleConflictChecker titleChecker,
        ReleaseConflictChecker releaseChecker,
        PersonScheduleConflictChecker scheduleChecker,
        TimeProvider clock) { /* assign fields */ }
}
```

**Lifetime: `Scoped`.** The `DbContext` is scoped to the HTTP request (the default EF Core lifetime). The service and all three checkers share the same `DbContext` instance within a request, so they participate in the same transaction and connection.

**The `DbContext` used by conflict checkers is NOT studio-scoped.** While the API layer's global query filter restricts queries to the calling studio's `studioId`, the conflict checkers need to see all studios' data. The implementation uses `db.Movies.IgnoreQueryFilters()` (or an equivalent unscoped `DbSet` accessor) to bypass the tenant filter for conflict queries only.

**Transaction strategy:** `ExecuteAsync` wraps the entire operation — load movie, run checkers, update status, write audit log — in a single `IDbContextTransaction` via `db.Database.BeginTransactionAsync()`. On success it commits; on any exception it rolls back.

### Test strategy

Tests use **Testcontainers** to spin up a real PostgreSQL instance and run against the actual `MrmDbContext` — no mocks, no in-memory provider. The test registers the same DI graph as production, calls `ExecuteAsync` with real commands, and asserts on the `TransitionResult` plus the database state.

---

## Trade-offs

### What You Gain

| Gain | Detail |
|------|--------|
| **Minimal surface area** | 1 method. Callers cannot misuse the API by calling methods out of order or forgetting to run conflict checks — there's only one entry point. |
| **Exhaustive result handling** | The sealed result hierarchy forces callers to handle every outcome at compile time (with C# exhaustive switch expressions). Adding a new result type produces compiler warnings at every call site. |
| **Extensible without breaking callers** | Adding a new transition (e.g., `ArchiveCommand`) means adding a new sealed record to the command hierarchy. Existing callers are untouched — only call sites that want the new transition need updating. |
| **Deep module** | Maximum complexity hidden behind minimum surface. The interface reveals *what* transitions exist but hides *how* they work — conflict policy, checker selection, state machine rules, override logic, audit logging, transactional integrity. |
| **Single dispatch point for cross-cutting concerns** | Logging, metrics, retry policies, and distributed tracing can be added via a single decorator around `ExecuteAsync` rather than wrapping N methods. |
| **Testable via behavior, not implementation** | Tests dispatch commands and assert on results. They never mock individual checkers or verify internal call sequences — they verify external behavior through the real PostgreSQL database. |

### What You Give Up

| Cost | Detail |
|------|--------|
| **Loss of per-method discoverability** | With a method-per-transition design, IntelliSense shows `SubmitForReview(...)`, `Approve(...)`, etc. With this design, callers must know to construct a `SubmitForReviewCommand` record. The command hierarchy is the "menu" instead of the method list. Mitigated by XML docs and the sealed type hierarchy showing all options. |
| **Switch exhaustiveness is not compiler-enforced on commands** | While the *result* hierarchy produces warnings on missing cases, the *command* dispatch inside the implementation is a runtime switch. A new command type added without a handler will fail at runtime, not compile time. Mitigated by integration tests covering all command types. |
| **Slight indirection cost** | Callers allocate a record object and the implementation dispatches on its runtime type. This is trivial overhead but is conceptually more indirect than a direct method call. |
| **Harder to set per-method authorization attributes** | With method-per-transition, you can slap `[Authorize(Policy = "X")]` on each method. With a single `ExecuteAsync`, authorization must be handled *inside* the implementation (checking the command type) or at the controller level (separate controller endpoints that construct different commands). We chose the latter — see the usage examples. |
| **All-or-nothing contract** | If a caller only needs one transition, they still depend on the full command/result hierarchy. In practice this is fine — lifecycle is a cohesive module — but it's a tighter coupling to the full vocabulary than a per-method interface would impose. |

### Verdict

The single-method design is the right choice when the module owns significant internal complexity (state machine + conflict orchestration + audit + override) and the callers' only job is to dispatch an intent and react to the outcome. The trade-offs are acceptable because the module's cohesion is high — every command flows through the same state machine and audit pipeline.
