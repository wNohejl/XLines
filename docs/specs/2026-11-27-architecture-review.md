# Architecture review, 2026-11-27

Five read-only reviews (Core and Data; Ingestion, Reliability, Observability and Worker; the
Desk library; the Web host and its panels; the build, test and deploy skeleton), each claim
checked against the code at `022a1d1`. This is what they found, what was changed on the
`arch-review` branch the same day, and what is left for later with a way to tell when it is done.

## What is already sound

Nothing below needs a decision reversed; no ADR is contradicted.

- **Dependency direction.** Core references nothing. Data references Core. The Desk family
  never touches Core or Data. No cycles.
- **DbContext lifetime.** No context is held for a circuit: every read is a factory context or
  a fresh scope per operation.
- **Change notification.** One shared feed (`DataChangeListener` to `DeskSignals`); no panel
  polls. One timer in Web (the Gamecast clock), disposed.
- **Render granularity.** `WindowChanged` is per window, `AppWindow` has a version-based
  `ShouldRender`, windows and dividers are keyed, every `+=` has its `-=`.
- **Ingestion.** Typed `IHttpClientFactory` clients, tokens flow through adapters, no catch
  swallows silently, a billed-but-unreadable response still records its cost, and
  `SchedulerPlacement` refuses a second scheduler against a remote database.
- **Containers.** Non-root, read-only, `cap_drop`, `no-new-privileges` already in place.

## Defects found

These are wrong today, not merely untidy.

| # | Defect | Evidence | Status |
|---|---|---|---|
| D1 | The hosted backup logs in as role and database `xlines`; Postgres is created as `lineops`. Every nightly backup fails and sleeps 24h. | `deploy/compose.yml:177-178` against `:142-143` | fixed on branch |
| D2 | Unattended line scans do not honour the monthly reserve: at zero usable credits `LinePacing.Budget` still returns a budget and the planner only warns. Bites only when Scanning is Automatic. | `LinePacing.cs:150-151`, `LinePollPlanner.cs:394-395`, `CreditBudgetGuard.cs:47` | see Results |
| D3 | Metered Odds API calls sit behind the standard retry handler; a re-sent call may be billed twice and is recorded once. | `IngestionServiceCollectionExtensions.cs:221-235` | see Results |
| D4 | Three readers return `Game` entities without the ADR 0024 tier scrub. Latent: no consumer renders those columns from these paths today. | `GameLogService.cs:242-285`, `MatchupCrossReference.cs:56-63`, `SearchService.cs:74-103` | see Results |
| D5 | Ops and Incidents write handlers are gated by markup only; one misplaced `@if` would expose them to a visitor. | `OpsPanel.razor:541…1247`, `IncidentsPanel.razor:533,555,598` | see Results |
| D6 | `GameResultSnippet` prints a scaled closing spread as "-3.00", the defect `PanelBase` documents as fixed. | `GameResultSnippet.razor:92` | see Results |
| D7 | `BoardService` reads the clock twice in one call. | `BoardService.cs:45`, `:358` | see Results |
| D8 | `docs/DESIGN.md` still calls `data/snapshots/` the committed snapshot. | `docs/DESIGN.md:558` | fixed on branch |

## The architecture to move toward

Ranked by what it buys. Each has a check that says when it is done.

### 1. Every panel reads through a data service (Web, L; M per panel)

Five windows already do (`IGameWindowData` and its siblings). Ten `.razor` files still hold EF
queries and view-model shaping in `@code`: Board, Journal, Ops, Runs, Player, Team and others,
with `@code` starting past line 500 in the three largest. One `I<Panel>Data` per panel returning
an immutable read model; the panel keeps `HandOver` and markup. The operator check (D5) then
lives in the write service, not the handler. Start with Ops or Runs.

- Check: razor files touching a DbContext go from **10 to 0**; each service has plain-C# tests
  with no bUnit.

### 2. Jobs are classes, not a string switch (Ingestion, L, mechanical)

A job's key is known in four places: `DescribeAsync`, the `RunAsync` switch with `StartsWith`
prefixes, the scheduler's due logic, and the Worker's special cases. Consistent with ADR 0012,
keep `IngestionJobs` as the catalogue facade and move each job into an
`IIngestionJob { Matches(key); Describe; Run }` registered in DI.

- Check: `IngestionJobs.cs` drops from **1588 to under 300 lines**; a test asserts every
  described key resolves to a handler.

### 3. The scheduler stops being one serial loop (Ingestion, M, medium risk)

`TickAsync` awaits slate, live, results, fighters (60 × 4 paced requests), backport, then lines.
A long fighters pass delays the 15-second live poll and can miss the closing-scan window. Run
lines and closing scans first or on their own loop; give long passes a per-tick budget.

- Check: with a slow fake fighters job, the closing scan still runs within its lead.

### 4. One clock (Ingestion and Data, M)

90 `DateTimeOffset.UtcNow` in Ingestion and its neighbours, 8 in the Data readers;
`TimeProvider` appears in one file. Register `TimeProvider.System` and inject it.

- Check: the grep count goes to **0**; a board test pins "stale pull" at a fixed instant.

### 5. `WindowManager` keeps its surface and loses four jobs (Desk, L, medium risk)

2111 lines holding viewport fit, columns and tabs, layout maths, persistence and workspaces,
history and pages. Extract `RowLayout` (pure), `DeskHistory` and `LayoutStore` as internal
collaborators.

- Check: no file over **700 lines**; the `WindowManager*` and `WindowTab*` tests pass unmodified.

### 6. The grid's columns belong to the Desk (Desk and Web, M) — do before ADR 0034 step 7

`DeskGrid` exposes MudDataGrid's own columns, so leaving MudBlazor is a rewrite of 126 call
sites (84 `PropertyColumn`, 42 `TemplateColumn`) plus 197 `Icons.Material` references. Add
`DeskColumn`/`DeskTemplateColumn` and a `DeskIcons` alias that forward to Mud today, and migrate
the call sites now, so the exit later changes one file.

- Check: `<PropertyColumn|<TemplateColumn` in `src/XLines.Web` returns **0**.

### 7. Pure analytics live in Core (Data, M)

`BoardService.cs` (716 lines) mixes queries with ranking that touches no database (`Compose`,
`ExplainGap`, `BestPair`, `Best`) and its DTOs; `Starters`, `GameLogSplits` and `StatLineKeys`
are EF-free yet live in Data. Move the pure code to `XLines.Core.Analytics`.

- Check: `BoardService.cs` drops to about **300 lines**; the ranking is tested without Postgres.

### 8. A build skeleton (cross-cutting, M)

No `Directory.Build.props`, `.editorconfig`, `global.json` or analyzers: `TargetFramework`,
`Nullable` and `ImplicitUsings` are repeated in fourteen csprojs, and CI's
`dotnet format --verify-no-changes` checks defaults only. Both Dockerfiles copy only
`Directory.Packages.props` and must copy the new file too, or images lose `Nullable` silently.
Leave warnings-as-errors off until the ADR 0034 work lands.

- Check: csproj properties duplicated across three or more projects go from **7 to 0**;
  `docker compose build` passes.

### 9. Tests run by category (tests and CI, M)

No `[Trait]` anywhere; `XLines.Tests` mixes pure tests with about forty Testcontainers classes
serialised on one collection. Tag the Postgres classes, split CI into unit then integration,
collect the coverage coverlet is already referenced for.

- Check: `dotnet test --filter Category!=Integration` passes with Docker stopped.

### 10. CSS in layers (Desk and Web, M to L) — after the ADR 0034 work lands

`desk.css` is 6275 lines, `themes.css` 4142, `xlines.css` 2499, with no `@layer` and 156 `.mud-`
selectors between them. Split along the existing banners into layered files so `mud-bridge.css`
deletes cleanly at ADR 0034 step 8. Collides with uncommitted work today.

- Check: no stylesheet over **2000 lines**; `DeskCssConventionTests` green.

### Smaller, worth doing when nearby

- Compose healthchecks on web and worker (`/health` and `/ready` exist) and
  `condition: service_healthy` for Caddy.
- A singleton `EspnPacer`: pacing is per adapter instance today, so concurrent ESPN traffic adds up.
- `IValidateOptions<IngestionOptions>` with `ValidateOnStart`; the `Positive()` fallbacks go.
- `WindowInstance` setters (`X`, `Width`, `Weight`…) become `internal` before 0.6.0 is published.
- `PartsPanel.razor` (2028 lines) becomes one child component per banner group.
- List readers project to row DTOs, not whole `Game` entities with their jsonb columns.
- A shared short-TTL read cache keyed by sport, window and tier — only once a second concurrent
  user exists (ADR 0018); a wrong key leaks tiers (ADR 0024).
- `PassingLinesAsync` runs an unindexed jsonb key test over 240 days of NFL lines per board
  load: measure with `EXPLAIN ANALYZE` before acting.
- bUnit 2 (the pinned 1.40 carries a suppressed AngleSharp advisory).

### For the owner, not an agent

- `DeployLineOpstests.artifactsfr2/` at the root: 174 MB of bin/obj from a mangled
  `--artifacts-path`. `analytics-py/`: 155 MB, untracked.
- A second worktree `gamecast-steady` and a local `analytics-py-port` branch stand against the
  two-branch rule; they may belong to work in flight.

## Changed on `arch-review`

See Results. The set was chosen to be small, verifiable the same day, and clear of the files
another session has uncommitted (`DeskHost.cs`, the Desk primitives and css, `Program.cs`,
`xlines.css`).

## Results

Five commits on `arch-review`, `37a42de` to `28c4109`, on top of `022a1d1`. Not merged, not pushed.

### What changed

| Area | Change | Defect |
|---|---|---|
| Deploy | Backup role and database are `lineops`; CI also runs on pushes to `LineX_Development`; `Microsoft.Extensions.Hosting` at 10.0.10; `ProjectShapeTests` pins the references of the seven non-Desk projects | D1, D8 |
| Data | `ForTier` in `BoxScoreAsync`, `MatchupCrossReference.GetAsync` and `SearchService.GamesAsync`; index `IX_IngestionRuns_JobKey_StartedAt` (migration `20261127201031_IngestionRunJobKeyIndex`, the index and nothing else); `MarketFirstClose.PerGame`, `FormOf` and `StatLineKeys.Read` each replace two copies; a board reads the clock once | D4, D7 |
| Ingestion | `RunUnattendedLinesAsync` gates the scheduler's paced and closing scans with `AffordsUnattended`, and the planner watches no league when no scan is left above the reserve; metered clients (The Odds API, odds-api.io, SportsGameOdds) keep the timeout and breaker and lose the retry; a Worker `--job` run cancels on Ctrl+C or SIGTERM | D2, D3 |
| Desk | `EnsureFocused` for a pointerdown inside a window; the row of columns is kept and checked against what it was built from; `WindowBar.ShouldRender` on a signature of what it draws | |
| Web | `CanOperate` at the top of ten write handlers in Ops and Incidents; `LatestRead` in Ops, Incidents, Runs and History; `OddsText` (Price, Line, Clv) is the one formatter | D5, D6 |

Two things turned out differently from the review. The focus early-out is a new method, not a
change to `Focus`: `Open` on a re-aimed singleton and the Line watch change a window's parameters
and then focus it to have it redrawn. And the journal's `Describe` had the same "-3.00" defect as
the snippet, so its text changes too.

### Checklist (CLAUDE.md, "Verification before done")

| Check | Result |
|---|---|
| `dotnet build XLines.slnx` | 0 errors, 72 warnings; `022a1d1` built the same way has 72 |
| `dotnet test XLines.slnx` | Desk 2751, Web 1449, Desk.Hosting 26, all passed. XLines.Tests 1252 passed, 1 failed in the worktree: `JournalTransferTests.A_journal_file_git_would_commit_is_refused`, because the worktree sits under `.claude/`, which the repository around it ignores, so `RepositoryGuard` rightly sees the path as ignored. In a copy of the branch outside any repository, all 11 `JournalTransferTests` pass |
| The Worker host starts | Against a throwaway Postgres: all 22 migrations applied, the scheduler ran until stopped at 30 s. `--job espn:range` with ESPN off refuses in one line and exits 5. `appsettings.json` comment keys unique |
| Data coverage queries | Not applicable: no ingested data or parser changed |
| `dotnet format --verify-no-changes` (CI's step, in the Linux SDK container) | Fails on 37 files, and failed the same way at `022a1d1` (exit 2): the branch adds no file to the list. With CI now running on `LineX_Development`, the format step will be red on the first push until those 37 are formatted |
| Not run | The desk in a browser; the Docker images; the hosted compose file (the backup fix is by reading, not by a backup taken) |

### Metrics

| Metric | Before | After |
|---|---|---|
| Tests in the solution | not run at the base | 5479 run (41 new test methods in 9 new files) |
| `CancellationToken.None` in the Worker | 1 | 0 |
| Token-less `ToListAsync()` in `.razor` | 6 | 1 |
| Copies of the `+` price format in Web | 4 | 1 |
| Write handlers in Ops and Incidents with no operator check | 10 | 0 |
| Readers returning an un-scrubbed `Game` | 3 | 0 |
| `Kind == SourceKind.Odds` in the readers (the close rule, inline) | 3 | 0 (one helper) |
| `MatchupCrossReference.cs` | 406 lines | 384 |
| Indexes serving a `JobKey` filter on `IngestionRuns` | 0 | 1 |
| Projects whose references a test pins | 4 (Desk family) | 11 |
| Razor files touching a DbContext | 10 | 10 (item 1 above, not started) |
| `IngestionJobs.cs` | 1588 lines | 1638 (item 2, not started; the gate added lines) |
| `WindowManager.cs` | 2111 lines | 2185 (item 5, not started; the cache added lines) |

The last three rows are the honest ones: the large moves in "The architecture to move toward"
are untouched, and two of the files they are about grew.
