# PRD: Movie Release Management System (MRM)

## Problem Statement

Studios, production companies, and industry administrators currently lack a centralized system to prevent conflicts when registering movie titles, scheduling release dates, and assigning actors and crew to productions. This leads to:

- Two movies registering identical or near-identical titles in the same release year, creating confusion and legal disputes.
- Multiple movies scheduling theatrical releases in the same distribution territory on the same date, cannibalizing box-office revenue.
- Actors and key crew members being double-booked across overlapping shoot periods on different productions, causing production delays and contract disputes.

There is no authoritative system of record that enforces these rules, detects conflicts early, and provides a clear audit trail of decisions and overrides.

---

## Solution

A web-based Movie Release Management system built on .NET 8 Web API + PostgreSQL that allows studios to register movies, schedule releases by territory, and assign cast and crew — with automated, real-time conflict detection enforced at meaningful lifecycle transitions. Conflicts are either hard-blocked (title duplicates, shoot schedule overlaps) or soft-warned (release date clashes), with System Admin override capability backed by a mandatory audit log. Cross-studio conflicts are detected globally while keeping each studio's data confidential.

---

## User Stories

### Movie Title Registration

1. As a Studio Admin, I want to register a new movie title, so that my studio can officially claim that title in the system.
2. As a Studio Admin, I want to be blocked from registering a title that already exists in the same release year, so that title conflicts are prevented before they become legal disputes.
3. As a Studio Admin, I want titles to be compared case-insensitively with punctuation stripped and Unicode-normalized, so that `"Spider-Man"` and `"spiderman"` are correctly detected as the same title.
4. As a Studio Admin, I want to see a clear, human-readable error message when a title registration is blocked, so that I understand exactly why the conflict occurred and what the conflicting movie is.
5. As a Studio Admin, I want to pre-flight validate a title before submitting the full registration form, so that I get instant feedback without losing my entered data.
6. As a Studio Admin, I want to edit a registered movie's title and have conflict checks re-run on save, so that conflicts cannot be introduced through edits that bypass registration checks.
7. As a Studio Admin, I want movies to start as `Draft` status so I can explore title options without triggering hard blocks, so that early creative planning is not impeded by enforcement rules.
8. As a Studio Admin, I want the title hard block to be enforced when I transition a movie from `Draft` to `Registered`, so that only committed titles enter the official registry.

### Release Date Management

9. As a Studio Admin, I want to set release dates per distribution territory for my movie, so that I can manage regional theatrical windows independently.
10. As a Studio Admin, I want to receive a soft warning when I set a release date that clashes with another movie in the same territory on the same date, so that I am aware of competitive scheduling without being blocked.
11. As a Studio Admin, I want to see which other movie(s) are releasing on the same territory/date when a warning is shown, so that I can make an informed scheduling decision.
12. As a Studio Admin, I want to pre-flight validate a release date before saving, so that I can check for conflicts before committing the change.
13. As a Studio Admin, I want to change a release date and have conflict checks automatically re-run, so that edits cannot silently introduce new territorial conflicts.
14. As a System Admin, I want to manage the list of distribution territories, so that the reference data is always accurate and up to date.
15. As a System Admin, I want to add, edit, and deactivate territories, so that I can reflect changes in the distribution landscape without deleting historical data.

### Person & Schedule Management

16. As a Production Manager, I want to add a person (actor or crew member) to a movie with a specific role type, so that the production roster is tracked in the system.
17. As a Production Manager, I want to add multiple non-contiguous shoot schedule blocks per person-per-movie, so that I can accurately represent principal photography, reshoots, and pickups.
18. As a Production Manager, I want to be hard-blocked when adding a shoot schedule block that overlaps with an existing block for the same person on another production, so that double-booking is prevented before it causes production disruption.
19. As a Production Manager, I want to see the full details of the conflicting block (dates and movie title of the other production) when a schedule overlap is detected, so that I can resolve the conflict with full context.
20. As a Production Manager, I want to pre-flight validate a schedule block before saving it, so that I can check for overlaps without committing the change.
21. As a Production Manager, I want to edit an existing schedule block and have conflict checks re-run, so that extending or shifting dates cannot silently create overlaps.
22. As a Production Manager, I want to assign a person to a movie production only for named roles (e.g., actor, director, cinematographer, editor, producer), so that only relevant persons are tracked and checked for conflicts.
23. As a Production Manager, I want a single person record to represent a real human across all studios and productions, so that cross-production conflict detection works correctly regardless of which studio assigned them.
24. As a Production Manager, I want the schedule hard block to be enforced when a movie transitions from `Registered` to `In Production`, so that all shoot schedules are confirmed conflict-free before filming begins.

### Lifecycle Management

25. As a Studio Admin, I want to transition my movie through a defined lifecycle (`Draft` → `Registered` → `In Production` → `Post Production` → `Released`), so that the system reflects the true state of my production.
26. As a Studio Admin, I want specific conflict checks to run automatically at lifecycle transitions, so that I never have to remember to run checks manually.
27. As a Studio Admin, I want to be blocked from advancing a movie's status if a hard conflict exists at that transition point, so that conflicted movies cannot proceed without resolution.
28. As a Studio Admin, I want to see a summary of all unresolved conflicts on a movie before attempting a status transition, so that I can address them all at once.

### System Admin & Override

29. As a System Admin, I want to force-override a hard block with a mandatory written reason, so that exceptional business decisions can still be recorded and enacted.
30. As a System Admin, I want every override to be recorded in the audit log with my user identity, timestamp, the conflict that was overridden, and my written reason, so that there is a full accountability trail.
31. As a System Admin, I want to view the complete audit log filtered by movie, person, studio, or date range, so that I can investigate any conflict history.
32. As a System Admin, I want all status transitions to be logged with actor, timestamp, and from/to status, so that the history of every movie's lifecycle is traceable.

### Notifications

33. As a Production Manager, I want to receive an in-app notification when a schedule conflict is introduced on my production by another studio's action, so that I am immediately aware of the impact without having to poll the system.
34. As a Studio Admin, I want in-app notifications to not reveal confidential details of the other studio's production, so that competitive information remains protected.
35. As a Production Manager, I want to be able to acknowledge and dismiss notifications, so that my notification feed stays manageable.

### Multi-Studio Data Scoping

36. As a Studio Admin, I want to only see my own studio's movies and schedules, so that competitor data is never exposed through my account.
37. As a System Admin, I want to see all studios' movies and schedules, so that I can manage conflicts and overrides across the platform.
38. As a Studio Admin, I want conflict detection to check across all studios' data even though I cannot see the other studios' details, so that global conflicts are caught without compromising confidentiality.

### API & Validation

39. As an API consumer, I want a pre-flight validation endpoint for movies, release dates, and schedule blocks, so that I can check for conflicts before submitting mutations.
40. As an API consumer, I want all conflict responses to use a consistent structured envelope `{ "blocked": bool, "conflicts": [{ "type", "severity", "detail" }] }`, so that I can programmatically handle both hard blocks and soft warnings with the same code.
41. As an API consumer, I want hard blocks to return HTTP `409 Conflict` and soft warnings to return HTTP `200` with a populated `warnings` array, so that I can distinguish enforcement from advisory responses without parsing the body.

---

## Implementation Decisions

### Data Model

- **`Movie`** — `id`, `studioId`, `originalTitle`, `normalizedTitle`, `releaseYear`, `status` (`Draft | Registered | InProduction | PostProduction | Released`), `createdAt`, `updatedAt`
- **`MovieTerritoryRelease`** — `id`, `movieId`, `territoryId`, `releaseDate`
- **`Territory`** — `id`, `name`, `code`, `isActive`
- **`Person`** — `id`, `fullName`, `dateOfBirth` (for disambiguation), `createdAt`
- **`ProductionRole`** — `id`, `movieId`, `personId`, `roleType` (`Actor | Director | Cinematographer | Editor | Producer | ...`), `createdAt`
- **`ScheduleBlock`** — `id`, `productionRoleId`, `startDate`, `endDate`
- **`AuditLog`** — `id`, `eventType` (`StatusTransition | ConflictDetected | OverrideApplied`), `actorUserId`, `movieId?`, `personId?`, `detail` (JSON), `timestamp`
- **`Notification`** — `id`, `recipientStudioId`, `message`, `isRead`, `createdAt`
- **`User`** — `id`, `studioId?` (null = System Admin), `role` (`StudioAdmin | ProductionManager | SystemAdmin`), `email`

### Title Normalization

- Normalization pipeline: Unicode NFKD decomposition → strip diacritics → lowercase → strip non-alphanumeric characters (except spaces) → collapse whitespace
- `normalizedTitle` is stored as a computed/derived column and indexed alongside `releaseYear`
- Unique constraint on `(normalizedTitle, releaseYear)`

### Conflict Detection Modules

- **`TitleConflictChecker`** — accepts `(title, releaseYear, excludeMovieId?)`, normalizes title, queries for duplicates; returns `ConflictResult`
- **`ReleaseConflictChecker`** — accepts `(movieId, territoryId, releaseDate, excludeMovieId?)`, queries `MovieTerritoryRelease` for same territory + date; returns `ConflictResult` with severity `Warning`
- **`PersonScheduleConflictChecker`** — accepts `(personId, startDate, endDate, excludeProductionRoleId?)`, queries `ScheduleBlock` using PostgreSQL `daterange` overlap operator (`&&`) across all `ProductionRole` records for that person; returns `ConflictResult`
- **`IConflictDetectionService`** — facade exposing `CheckMovieConflicts(...)` and `CheckScheduleBlockConflicts(...)`, orchestrating the above checkers and returning a unified `ConflictReport { Blocked: bool, Conflicts: ConflictItem[] }`

### Lifecycle & Enforcement

- **`MovieLifecycleService`** — owns all status transition logic; invokes `IConflictDetectionService` with the appropriate conflict types per transition:
  - `Draft → Registered`: runs `TitleConflictChecker` (hard block) + `ReleaseConflictChecker` (warning)
  - `Registered → InProduction`: runs `PersonScheduleConflictChecker` for all roles (hard block)
  - Later transitions: no conflict checks, informational only
- Hard blocks prevent the transition; soft warnings are attached to the response but do not prevent it
- System Admin can supply an `overrideReason` to bypass any hard block; the override is recorded in `AuditLog`

### API Design

- `POST /movies` — create movie (Draft)
- `POST /movies/validate` — pre-flight conflict check
- `PUT /movies/{id}` — update movie fields (re-runs applicable checks)
- `POST /movies/{id}/transition` — lifecycle transition with `{ targetStatus, overrideReason? }`
- `POST /movies/{id}/releases` — add territory release date
- `POST /movies/{id}/releases/validate` — pre-flight release date check
- `POST /movies/{id}/roles` — add person to production with role type
- `POST /movies/{id}/roles/{roleId}/schedule-blocks` — add schedule block
- `POST /movies/{id}/roles/{roleId}/schedule-blocks/validate` — pre-flight schedule block check
- `GET /territories` — list active territories
- `POST /audit-log/query` — query audit log (System Admin only)
- `GET /notifications` — get current user's notifications
- `PUT /notifications/{id}/read` — mark notification as read
- All endpoints return conflict envelope on relevant error/warning conditions

### Authentication & Authorization

- JWT bearer tokens with claims: `sub` (userId), `studioId` (absent for System Admins), `role`
- ASP.NET Core authorization policies: `StudioAdminPolicy`, `ProductionManagerPolicy`, `SystemAdminPolicy`
- All data queries automatically scoped to `studioId` from JWT; System Admin has unscoped access
- Conflict detection queries bypass studio scoping (must check all studios' data)

### Audit Log

- Events: `StatusTransition`, `ConflictDetected`, `OverrideApplied`
- Each event records: `actorUserId`, `timestamp`, `movieId?`, `personId?`, `detail` (JSON blob with event-specific fields)
- `OverrideApplied` events require non-empty `reason` field — enforced at application layer

### Notifications

- Written to `Notification` table targeting the affected studio when a cross-studio conflict is introduced
- Notification text exposes: affected person name, affected date range, affected movie title (own studio's movie only) — never the other studio's movie details
- No email in V1; in-app only

---

## Testing Decisions

### What makes a good test

- Tests verify **external behavior** of a module through its public interface, not implementation details or private methods.
- Tests are **arrange-act-assert** structured and named after the behavior being verified (e.g., `CheckTitle_ReturnHardBlock_WhenNormalizedTitleExistsInSameYear`).
- Tests do not assert on database query structure, EF Core internals, or HTTP routing details — only on the returned `ConflictResult` / HTTP response / domain state.
- Each test is independent; no shared mutable state between tests.

### Modules to be tested

| Module | Test type | Key behaviors to cover |
|---|---|---|
| `TitleConflictChecker` | Unit (mock DB) | Exact match, normalized match, different year = no conflict, same movie excluded, empty registry = no conflict |
| `ReleaseConflictChecker` | Unit (mock DB) | Same territory + date = warning, different territory = no conflict, different date = no conflict, same movie excluded |
| `PersonScheduleConflictChecker` | Unit (mock DB) | Overlapping blocks = hard block, adjacent blocks = no conflict, partial overlap = hard block, same role excluded, person with no other roles = no conflict |
| `ConflictDetectionService` | Unit | Aggregates results from all checkers correctly, `Blocked=true` when any checker returns hard block |
| `MovieLifecycleService` | Unit | Correct checkers invoked per transition, hard block prevents transition, warning allows transition, override bypasses hard block, override without reason is rejected |
| `ScheduleBlockService` | Unit (mock conflict service) | Calls conflict check before write, saves block when no conflict, rejects block on hard block, saves block with warning |
| `AuditLogService` | Unit (mock DB) | Logs status transitions with correct fields, logs conflict events, logs override with reason, rejects override with empty reason |
| `NotificationService` | Unit (mock DB) | Sends notification to affected studio, does not reveal other studio's movie details, does not send notification when conflict is within same studio |
| `TerritoryService` | Unit (mock DB) | CRUD operations, deactivation does not delete, inactive territories excluded from conflict checks |

---

## Out of Scope

- Email or push notifications (V1: in-app only)
- Self-service person availability management (actors/crew managing their own schedules)
- Fuzzy/similarity-based title matching (only exact normalized match)
- Article removal in title normalization (`"The Batman"` ≠ `"Batman"`)
- Genre-based release conflict rules
- External identity provider integration (Azure AD, Okta, Auth0)
- Analytics, reporting dashboards, or BI integrations
- Mobile applications
- Streaming release date management (theatrical only)
- Contract or deal management
- Budget or financial tracking

---

## Further Notes

- PostgreSQL's native `daterange` type and `&&` overlap operator should be used for `ScheduleBlock` conflict queries, with a GiST index on the range column for performance.
- The `normalizedTitle` column should have a unique index on `(normalizedTitle, releaseYear)` to serve as the database-level safety net for concurrent registrations in addition to the application-level pre-check.
- The `Person` entity is identified across studios by `(fullName, dateOfBirth)` — a person deduplication UI should be provided to System Admins for cases where the same real person has been entered with slightly different names.
- V2 candidates: email notifications, fuzzy title matching with configurable similarity threshold, self-service person availability, external IdP integration.
