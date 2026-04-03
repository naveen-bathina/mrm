# Movie Status Lifecycle Module — Design Document

## Design: Fluent Pipeline Registry + Middleware-style Steps

---

## Section 3: What Complexity It Hides

Everything below is invisible to callers (controller, API consumers):

1. **Valid state machine edges** — Callers never define or validate which transitions are legal. `StateGuardMiddleware` owns the `HashSet<(from, to)>` and rejects invalid edges before any business logic runs. Adding a new valid transition is a one-line change in one place.

2. **Per-edge conflict checker selection** — Which checkers run on which transition is configured once at startup via `OnTransition(Draft, Registered).Check<TitleConflictChecker>()`. Callers don't know that `Draft → Registered` runs title + release checks while `Registered → InProduction` runs schedule checks.

3. **Conflict checker DI resolution** — Checkers are stored as `Type` at startup and resolved from the request's scoped `IServiceProvider` at execution time. Callers never interact with `IServiceScopeFactory`, `GetRequiredService`, or any DI machinery.

4. **Hard vs soft conflict severity policy** — `OverrideValidationMiddleware` decides that `ConflictSeverity.Hard` blocks standard transitions while `ConflictSeverity.Warning` is advisory-only. This policy is centralized — checkers declare severity, middleware enforces it.

5. **Override authorization and reason extraction** — The visitor pattern (`TransitionModeVisitor`, `OverrideReasonVisitor`) extracts mode and reason from the command without casting. The middleware validates that overrides only bypass hard conflicts, never invalid transitions.

6. **Audit log writes** — `AuditLogMiddleware` wraps the entire pipeline and records every transition attempt (success AND failure) with full context: conflicts found, override reasons, timing. The caller never calls an audit API.

7. **Middleware execution order** — The pipeline's `UseAuditLog().UseStateGuard().UseConflictChecks().UseOverrideValidation()` chain defines execution order. Callers see one `TransitionAsync` call; the four-step orchestration is invisible.

8. **Short-circuit abort propagation** — When `StateGuardMiddleware` or `OverrideValidationMiddleware` calls `context.Abort()`, all downstream middleware is skipped. The abort result propagates back through the wrapping middleware (AuditLog still records it).

9. **Transaction management** — Movie entity load, conflict checker queries, status update, and audit log entry all share the same scoped `DbContext`. A single `SaveChangesAsync` call commits or rolls back the entire unit of work atomically.

10. **Visitor-based mode dispatch** — The pipeline determines `Standard` vs `Override` mode from the command's concrete type via `Accept<TResult>(visitor)`. No casting, no `is` checks, no `GetType()` anywhere in the pipeline.

11. **Cross-studio query filter bypass** — Conflict checkers call `IgnoreQueryFilters()` to bypass multi-tenant studio scoping for cross-studio conflict detection. The controller and service layer never deal with query filter concerns.

12. **Pipeline composition mechanics** — The delegate chain is built from back-to-front at request time (matching ASP.NET Core's model). Callers don't see the `Func<TransitionDelegate, TransitionDelegate>` wrapping, the terminal no-op delegate, or the composition loop.

13. **Error aggregation** — Multiple conflict results from different checkers are collected into `context.ConflictResults` and surfaced as a single `TransitionError.Conflicts` list. Callers get one error with all conflicts, not one error per checker.

14. **Command construction invariants** — The sealed class hierarchy with static factories ensures `Override` commands always have a non-empty reason and `Standard` commands never do. Invalid construction is impossible — the factory throws `ArgumentException` immediately.

---

## Section 4: Dependency Strategy

### Singleton Pipeline (built once at startup) vs Scoped Services (per request)

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Register EF Core with PostgreSQL (scoped by default)
builder.Services.AddDbContext<MovieDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Movies")));

// Register the lifecycle module — pipeline built once, services scoped
builder.Services.AddMovieStatusModule(pipeline =>
{
    // Middleware order = execution order (outermost to innermost)
    pipeline
        .UseAuditLog()              // 1. Wraps everything, logs before/after
        .UseStateGuard()            // 2. Validates transition edge
        .UseConflictChecks()        // 3. Runs per-edge conflict checkers
        .UseOverrideValidation();   // 4. Blocks on hard conflicts (unless Override)

    // Per-edge conflict checker registration
    // Draft → Registered: title uniqueness (hard) + release date overlap (warning)
    pipeline
        .OnTransition(MovieStatus.Draft, MovieStatus.Registered)
            .Check<TitleConflictChecker>()
            .Check<ReleaseConflictChecker>();

    // Registered → InProduction: person schedule overlap (hard)
    pipeline
        .OnTransition(MovieStatus.Registered, MovieStatus.InProduction)
            .Check<PersonScheduleConflictChecker>();

    // InProduction → Released: no conflict checks needed
    // Released → Archived: no conflict checks needed
});

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
```

### How EF Core DbContext Flows Through the Pipeline

```
HTTP Request
  └─► DI Scope created (one per request)
       ├─► MovieDbContext (scoped — created once for this request)
       ├─► MovieStatusService (scoped — receives MovieDbContext)
       │    └─► Calls pipeline.ExecuteAsync(context, serviceProvider, ct)
       │         └─► serviceProvider IS the request's scoped IServiceProvider
       │              ├─► ConflictCheckMiddleware resolves TitleConflictChecker (scoped)
       │              │    └─► TitleConflictChecker receives the SAME MovieDbContext
       │              ├─► ConflictCheckMiddleware resolves ReleaseConflictChecker (scoped)
       │              │    └─► ReleaseConflictChecker receives the SAME MovieDbContext
       │              └─► AuditLogMiddleware resolves MovieDbContext
       │                   └─► Adds AuditLogEntry to the SAME MovieDbContext
       └─► MovieStatusService calls SaveChangesAsync ONCE
            └─► Commits: status update + audit entry + all checker side-effects
```

### How Conflict Checkers Are Resolved Per-Request (Not Pre-instantiated)

```csharp
// At STARTUP (in TransitionPipelineBuilder):
// Only the Type is stored — no instance created
pipeline.OnTransition(MovieStatus.Draft, MovieStatus.Registered)
    .Check<TitleConflictChecker>();    // Stores typeof(TitleConflictChecker)
    .Check<ReleaseConflictChecker>();  // Stores typeof(ReleaseConflictChecker)

// At REQUEST TIME (in ConflictCheckMiddleware):
// Resolved from the request's scoped IServiceProvider
foreach (var checkerType in checkerTypes)
{
    // This calls: serviceProvider.GetRequiredService(typeof(TitleConflictChecker))
    // Which creates a new TitleConflictChecker with the request's scoped MovieDbContext
    var checker = (IConflictChecker)serviceProvider.GetRequiredService(checkerType);
    var result = await checker.CheckAsync(context, ct);
}
```

### The Full AddMovieStatusModule Extension Method

See `MovieLifecycle/Extensions/ServiceCollectionExtensions.cs` for the complete implementation with inline documentation.

Key registration lifetimes:
| Component | Lifetime | Why |
|-----------|----------|-----|
| `TransitionPipeline` | Singleton | Immutable definition, thread-safe, built once |
| `MovieDbContext` | Scoped | EF Core default; one context per request for UoW |
| `TitleConflictChecker` | Scoped | Shares request's DbContext for cross-studio queries |
| `ReleaseConflictChecker` | Scoped | Same as above |
| `PersonScheduleConflictChecker` | Scoped | Same as above |
| `IMovieStatusService` | Scoped | Aligns with DbContext; orchestrates one transition |

---

## Section 5: Trade-offs

### 1. Mutable TransitionContext Bag

**What it makes harder:**
- **Testability:** Tests must construct a full `TransitionContext`, run middleware, then assert on multiple mutable properties (`ConflictResults.Count`, `IsAborted`, `AbortResult`, `OverrideResult`). There's no compile-time guarantee that a middleware only reads/writes the properties it owns — any step can mutate anything.
- **Thread safety:** If middleware ever runs in parallel (e.g., parallel conflict checkers), the mutable `List<ConflictResult>` and nullable `OverrideResult` become data races. The current design assumes sequential execution, but nothing in the type system enforces this.
- **Reasoning about state:** At any point in the pipeline, the context's state depends on which middleware ran before. A middleware at position N must mentally reconstruct the context state left by positions 0 through N-1. With pure functions and immutable data, each step's input would be fully described by its parameters.

**What it buys:** Simple middleware signatures (`TransitionDelegate` takes one mutable object), no complex result-threading, and easy extensibility — adding a new property to the context doesn't change any middleware signatures.

### 2. Per-Edge DSL Registration Friction

**Where it adds friction:**
- **Refactoring status names:** Renaming `MovieStatus.Registered` to `MovieStatus.Approved` requires updating every `OnTransition(Draft, Registered)` call. With the current enum-based approach, the compiler catches this — but if edges were string-based, it would be a silent runtime failure.
- **Discovering which checks run on which edge:** The edge-checker mapping is defined imperatively in `Program.cs`, not declaratively on the checker classes. A developer reading `TitleConflictChecker` cannot see which edges it applies to without searching the startup configuration. This is the "where is it wired?" problem.
- **Runtime vs compile-time safety:** Nothing prevents registering a checker on an edge that `StateGuardMiddleware` considers invalid (e.g., `OnTransition(Released, Draft).Check<TitleConflictChecker>()`). This misconfiguration is only caught at runtime when the state guard rejects the transition before checkers run — the checkers silently never execute.

### 3. Visitor Pattern Cost

**What it costs:**
- **Open/closed violation for new commands:** Adding a third command type (e.g., `BulkTransitionCommand`) requires adding a `Visit(BulkTransitionCommand)` method to `ITransitionCommandVisitor<TResult>`, which is a breaking change on every visitor implementation (`TransitionModeVisitor`, `OverrideReasonVisitor`, `OverrideResultVisitor`). The Expression Problem: visitors are closed over data variants.
- **Ceremony:** Three visitor implementations exist solely to avoid one `if (command is OverrideTransitionCommand override)` check. For a two-case hierarchy, the visitor overhead (interface + 3 implementations) may exceed the casting it replaces. The pattern pays off at 4+ command types.
- **Discoverability:** New developers must understand double-dispatch to follow the code flow. `command.Accept(TransitionModeVisitor.Instance)` is more abstract than `command is OverrideTransitionCommand ? Override : Standard`.

### 4. Struct TransitionResult: Gains and Losses

**Gains:**
- Stack allocation avoids GC pressure on the success hot path (most transitions succeed).
- Value semantics: no null references, no reference equality surprises, no accidental aliasing.
- The `Match<T>` method is inlined by the JIT for simple lambdas, approaching zero overhead.

**Losses:**
- **Boxing in async contexts:** `Task<TransitionResult>` boxes the struct into a `Task` object. The allocation benefit evaporates for the primary use case (async service method). `ValueTask<TransitionResult>` would help but adds its own complexity (can't await twice, pooling semantics).
- **No inheritance:** Cannot create `TransitionResult` subtypes for richer success payloads (e.g., carrying the full `MovieSnapshot`). The struct is locked to `MovieStatus` as the success type.
- **Default value trap:** An uninitialized `TransitionResult` (default struct) has `_tag = Uninitialized`, which throws in `Match`. This is caught at runtime, not compile time. A class-based result with a factory method would avoid this.

### 5. OneOf/Match vs Exceptions vs Result<T,E>

| Approach | What it gives up |
|----------|-----------------|
| **OneOf/Match (this design)** | No stack trace on failure (errors are values, not exceptions). No `catch` integration — callers must use `Match` or check `IsSuccess`. IDE support is weaker: no "this method throws X" documentation in IntelliSense. |
| **Exceptions** | No exhaustive handling — callers can forget to catch. Performance cost for conflict-blocked transitions (exception allocation + stack unwinding on every business rule violation, not just bugs). Exceptions conflate bugs with expected domain outcomes. |
| **Result<T,E> class** | Heap allocation for every result (both success and failure). Richer than struct (supports inheritance, no boxing), but GC pressure on the hot path. Can implement `IEnumerable<T>` for LINQ integration (as in Design 3). |

The OneOf/Match struct is the tightest contract (forced exhaustive handling + minimal allocation), but it sacrifices debugging ergonomics (no stack traces) and async efficiency (boxing in Task<>).

### 6. Fluent Middleware Pipeline: Execution Order Obscurity

**Where it obscures:**
- Registration order determines execution order, but `UseAuditLog().UseStateGuard().UseConflictChecks().UseOverrideValidation()` reads top-to-bottom while execution wraps outside-in. `AuditLog` is registered first and executes first — but its post-processing runs LAST (after all inner middleware completes). This inside-out execution model is well-known in ASP.NET Core but surprises developers from sequential-code backgrounds.
- Custom `Use()` steps interleave with named middleware, making the full ordering harder to visualize. A developer must read the entire `configure` callback to understand when their custom step runs.
- Unlike explicit sequential code (`ValidateEdge(); RunCheckers(); ValidateOverride(); WriteAudit();`), the middleware pipeline cannot be stepped through linearly in a debugger — you step into delegate composition, closures, and back-to-front chain traversal.

### 7. Single Method on IMovieStatusService: Strength and Trap

**Strength:**
- One method means one code path. Every transition — regardless of mode, target status, or caller role — flows through the same pipeline. No method can bypass validation, skip audit logging, or forget conflict checks. This is the deep module principle: wide functionality behind a narrow interface.

**Trap:**
- **Feature accretion pressure:** When requirements diverge (e.g., "validate without persisting", "dry-run conflict check", "batch transition with partial failure"), the single method must either grow parameters (`dryRun: true`, `batchMode: true`) or the interface must add methods — breaking the single-method constraint. The narrow interface becomes a bottleneck when the domain grows beyond "transition a single movie."
- **Diagnostic opacity:** When a transition fails, the caller gets a `TransitionResult` with an error code and message. But which middleware failed? At what pipeline stage? The single method provides no hooks for callers to observe intermediate pipeline state. Debugging requires logging inside each middleware, not inspecting the result.
- **Testing indirection:** Testing a specific behavior (e.g., "TitleConflictChecker blocks Draft → Registered") requires invoking the entire pipeline through `TransitionAsync`, setting up a full `TransitionContext`, and asserting on the final result. You cannot test the checker in isolation through the service interface — you must either test the checker directly (bypassing the pipeline) or accept integration-level test granularity.
