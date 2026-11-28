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

Fourteen commits on `arch-review`, on top of `022a1d1`. Two rounds: the defects and small wins
first, then five of the ten moves above.

### Round one: defects and small wins

| Area | Change | Defect |
|---|---|---|
| Deploy | Backup role and database are `lineops`; CI also runs on pushes to `LineX_Development`; `Microsoft.Extensions.Hosting` at 10.0.10; `ProjectShapeTests` pins the references of the seven non-Desk projects | D1, D8 |
| Data | `ForTier` in `BoxScoreAsync`, `MatchupCrossReference.GetAsync` and `SearchService.GamesAsync`; index `IX_IngestionRuns_JobKey_StartedAt` (migration `20261127201031_IngestionRunJobKeyIndex`, the index and nothing else); `MarketFirstClose.PerGame`, `FormOf` and `StatLineKeys.Read` each replace two copies; a board reads the clock once | D4, D7 |
| Ingestion | `RunUnattendedLinesAsync` gates the scheduler's paced and closing scans with `AffordsUnattended`, and the planner watches no league when no scan is left above the reserve; metered clients (The Odds API, odds-api.io, SportsGameOdds) keep the timeout and breaker and lose the retry; a Worker `--job` run cancels on Ctrl+C or SIGTERM | D2, D3 |
| Desk | `EnsureFocused` for a pointerdown inside a window; the row of columns is kept and checked against what it was built from; `WindowBar.ShouldRender` on a signature of what it draws | |
| Web | `CanOperate` at the top of ten write handlers in Ops and Incidents; `LatestRead` in Ops, Incidents, Runs and History; `OddsText` (Price, Line, Clv) is the one formatter | D5, D6 |
| Journal | `RepositoryGuard` no longer treats a linked worktree in an ignored folder as a private repository: a journal written into `.claude/worktrees/<name>/` was allowed, and is now refused as in the main checkout. Found because its test failed when run from a worktree | new |

### Round two: the moves

| Move | Done | Its check |
|---|---|---|
| 1. Every panel reads through a data service | Runs, History, Ops, Player, Team, Players, Market movement, Board and Journal each have a service; Odds resolves the registered `ILineMovementData`. Queries, ordering and tier handling moved as they were. One line in `Program.cs`: `AddXLinesPanelData()` | Razor files touching a DbContext **10 → 0**, and `PanelDataTests` fails if one returns. The services have no database-backed tests: the Web test project has no Postgres fixture |
| 2. Jobs are classes | Twelve classes under `Services/Jobs` behind `IIngestionJob`; `IngestionJobs` stays the catalogue and keeps the run's bookkeeping; its public members are unchanged | `IngestionJobs.cs` **1638 → 461** lines (under 500, not the 300 hoped for: the public constants and records stay on the facade). `JobCatalogueTests`: every described key resolves to exactly one handler |
| 3. The scheduler's loop | The tick polls live and scans lines before results and settlement; fighters then backport run on a task the tick does not await, never twice at once | `SchedulerLongPassTests`: a fighters pass that has not finished holds neither the live poll nor the closing scan, and is not started again |
| 4. One clock | `TimeProvider` registered and injected through the ingestion and the Data readers | `DateTimeOffset.UtcNow` **71 → 0** in Ingestion and **8 → 0** in the readers. Two reads remain in Data outside the readers (`JournalAuditInterceptor`, `DatabaseInitializer`), and Reliability and Observability were not touched. `BoardServiceTests` pins the stale-pull line at a fixed instant; `ResultsBackOffClockTests` pins the sweep's back-off |
| 5. `WindowManager` | `RowLayout` (pure), `DeskRow`, `ViewportFit`, `LayoutStore`, `DeskHistory`; the manager still raises every event in the order it did | `WindowManager.cs` **2185 → 1094** lines, every new file under 700 (not the 700 hoped for the manager: what is left is its public members and their comments). Its public declarations diff as identical and no existing test was edited. `RowLayout` is public, not internal: `InternalsVisibleTo` made `DeskLayout` ambiguous in a test |

Left for round three, below: 6 (the grid's columns), 7 (pure analytics into Core), 8 (the build skeleton), 9 (tests
by category), 10 (CSS in layers, which waits on the ADR 0034 work), and the smaller items.

Behaviour that changed on purpose, beyond the defects: `BoardRow.Age` and `FighterCard.Age` are
measured to the moment the row was read, not to the moment the property is asked; a failed metered
call is the run's failure, with no retry; in Automatic, a scan held back at the reserve writes no run.

### Checklist (CLAUDE.md, "Verification before done"), run on the final commit

| Check | Result |
|---|---|
| `dotnet build XLines.slnx` | 0 errors, 72 warnings; `022a1d1` has 72 |
| `dotnet test XLines.slnx`, in the worktree | XLines.Tests 1277, XLines.Web.Tests 1465, XLines.Desk.Tests 2759, XLines.Desk.Hosting.Tests 26: 5527 passed, 0 failed, 0 skipped |
| `dotnet format --verify-no-changes --no-restore` (CI's step, Linux SDK container, LF files) | Exit 0. It exited 2 at `022a1d1`: whitespace in twelve files and two analyzer warnings, all since fixed |
| The Worker host starts | Against a throwaway Postgres: all 22 migrations applied, "Ingestion scheduler started", still running when stopped at 40 s. `--job settle` succeeds; `--job espn:range` with ESPN off refuses in one line and exits 5. `appsettings.json` comment keys unique |
| Docker images | Web and Worker both build from the committed tree; the Worker image refuses an `espn:` job with exit 5 |
| The hosted backup | `deploy/backup.sh` against a throwaway Postgres created as `lineops`: with the old role, "backup: FAILED"; with the fixed one, "backup: wrote" |
| Data coverage queries | Not applicable: no ingested data or parser changed |
| Not run | The desk in a browser. The scheduler against live providers: the new ordering and the reserve gate are proven by tests on fakes, not by a day of real polling |

### Metrics

| Metric | Before | After |
|---|---|---|
| Razor files touching a DbContext | 10 | 0 |
| Token-less `ToListAsync()` in `.razor` | 6 | 0 |
| `IngestionJobs.cs` | 1638 lines | 461 |
| `WindowManager.cs` | 2111 lines | 1094 |
| `DateTimeOffset.UtcNow` in Ingestion | 71 | 0 |
| `DateTimeOffset.UtcNow` in the Data readers | 8 | 0 |
| Write handlers in Ops and Incidents with no operator check | 10 | 0 |
| Readers returning an un-scrubbed `Game` | 3 | 0 |
| Copies of the `+` price format in Web | 4 | 1 |
| `CancellationToken.None` in the Worker | 1 | 0 |
| Indexes serving a `JobKey` filter on `IngestionRuns` | 0 | 1 |
| Projects whose references a test pins | 4 (Desk family) | 11 |
| CI format step | exit 2 | exit 0 |
| New test methods | | 60 |
| `BoardPanel.razor` / `JournalPanel.razor` / `OpsPanel.razor` | 1808 / 1567 / 1282 | 1742 / 1494 / 1250 |
| `BoardService.cs` | 716 lines | 718 (move 7, not started) |

The panels' own line counts barely moved: the queries left, but the view-model shaping and the
markup are still one file each. That, and `BoardService.cs`, are where the next round starts.

### After the merge

The branch met ten commits that landed on `LineX_Development` the same afternoon (the Gamecast
work and the first Desk primitives off MudBlazor) and was merged with them as `cfba81f`. Four files
were joined by hand: the ESPN adapter takes both the injected clock and the host's `EspnPacer`; a
provider client is not retried when metered, and otherwise not when the request is a gamecast read;
the gamecast feed keeps its optional `TimeProvider`; `Program.cs` registers both sides' services.
On the merged tree: build 0 errors; XLines.Tests 1308, XLines.Web.Tests 1516, XLines.Desk.Tests
2820, XLines.Desk.Hosting.Tests 26, 5670 passed and none failed; no pending model changes; CI's
format step exits 0. The Worker start and the Docker images were run on the branch before the
merge, not again after it.

## Round three: the rest of the moves

Eight commits on `arch-review-2`, on top of the merge above.

| Move | Done | Its check |
|---|---|---|
| 6. The grid's columns belong to the Desk | `DeskColumn` and `DeskTemplateColumn` forward to Mud's columns with the parameters the call site wrote; `DeskIcons` names each glyph. Every call site in the app, and the Desk's own Windowing and Layout, use them | `<PropertyColumn\|<TemplateColumn` in `src/XLines.Web` **126 → 0**; `Icons.Material` there **119 → 0**, and in the Desk's Windowing and Layout **52 → 0**. Two convention tests fail if either returns. Left: 26 glyph references in fifteen primitives the MudBlazor exit is working in (listed in a test that can only shrink), 2 in the template, which pins the published 0.5.0. `InitialDirection` still takes Mud's `SortDirection` |
| 7. Pure analytics live in Core | `BoardComposer` (Compose, ExplainGap, BestPair, Best), the board's records, `Starters`, `GameLogSplits` and `StatLineKeys` are in `XLines.Core.Analytics`. Four list readers project to row records; the three that hand a `Game` to a caller still load it, because `ForTier` needs it | `BoardService.cs` **718 → 342** lines; nine ranking tests with no Postgres; `Include(` in the two readers **16 → 5** |
| 8. A build skeleton | `Directory.Build.props`, `tests/Directory.Build.props`, `global.json` (10.0.4xx); both Dockerfiles copy them. No warnings-as-errors, no analyzers, no `.editorconfig` | Properties duplicated across three or more csprojs **7 → 0**; evaluated properties and items of all sixteen projects identical before and after; both images build |
| 9. Tests run by category | 46 Testcontainers classes carry `[Trait("Category", "Integration")]`; `IntegrationTraitTests` refuses a new one without it. CI runs unit then integration, each writing trx and coverage, uploaded always | `--filter Category!=Integration` on XLines.Tests: 824 tests in 11 s, no container started; integration 508 in 1 m 20 s; 824 + 508 = 1332 |
| 10. CSS | A file split only. `desk.css` is six files, `themes.css` three, `xlines.css` two, cut at their banners and linked in the order their contents had | Largest stylesheet **6282 → 1685** lines; each set concatenated in link order is byte for byte the original. `@layer` is not attempted: it changes precedence against MudBlazor's unlayered sheet and belongs with ADR 0034 step 8 |
| 2 and 5, their size targets | `IngestionJobs.cs` 461 → **288** (keys in `JobKeys`, records a partial). `WindowManager.cs` 1094 → **358**, seven partial files by concern | No file in `Desk/Windowing` over 664 lines; in Ingestion only `GamecastFeed.cs` (759) and `IngestionOptions.cs` (700) reach 700 |
| Smaller items | The ESPN adapter and `StatsIngestionService` are partial files by concern (1268 → 295, 1260 → 411; `PersistAsync` about 50 lines). `IngestionOptionsValidator` with `ValidateOnStart`. `PartsPanel.razor` 2028 → 88 plus thirteen group components, the largest 348. `X`, `Weight`, `Maximised`, `Pulse`, `StatusText` on `WindowInstance` are internal. `EspnPacer` arrived with the Gamecast work | Both shipped `appsettings.json` pass the validator; the gallery's markup is unchanged before and after |

Not done, and why:

- **Compose healthchecks.** The runtime image has no `curl`, `wget` or `nc`, and the container is
  read-only and non-root; a probe needs either a tool in the image or a `--health` mode in the
  app. Neither was added.
- **The season-start rule at startup.** It is a check the shipped files are held to by a test,
  not a startup failure: thirty tests configure sports with no seasons, and the backfill has a
  fallback.
- **`Width`, `Minimised`, `Sequence`** on `WindowInstance` stay public: tests write them and
  there is no `InternalsVisibleTo`.
- **Scoped CSS and layers**, the shared read cache, bUnit 2, measuring `PassingLinesAsync`, and
  the two clock reads in `JournalAuditInterceptor` and `DatabaseInitializer`.
- **The panels are still large**: `GamecastPanel.razor` 2079, `BoardPanel.razor` 1742,
  `BoardWager.razor` 1495, `JournalPanel.razor` 1494. Their queries are out; their view-model
  shaping and markup are one file each.

### Checklist, run on the round-three branch

| Check | Result |
|---|---|
| `dotnet build XLines.slnx` | 0 errors |
| `dotnet test XLines.slnx`, in the worktree | XLines.Tests 1332, XLines.Web.Tests 1518, XLines.Desk.Tests 2827, XLines.Desk.Hosting.Tests 26: 5703 passed, 0 failed |
| Flaky tests seen | Three, each failing once under the whole solution in parallel and passing on rerun, none in code these rounds changed in a way that explains it: `GameWindowCardsTests.An_opened_market_keeps_view_more_across_closing_and_reopening` (twice), `BoardWagerTests.While_the_row_loads_the_body_is_a_skeleton_not_an_empty_state` (once in ten runs), `DeskPanelBaseTests.A_refresh_the_panel_asked_for_itself_names_nothing` (once). All wait on a one-second bUnit assertion |
| CI's format step (Linux, LF) | Exit 0 |
| Pending model changes | None |
| The Worker host | Starts against a throwaway Postgres, 23 migrations, "Ingestion scheduler started", options validated; `--job settle` succeeds; `espn:` off exits 5 |
| Docker images | Web and Worker build with the new props and `global.json`; the Worker image refuses an `espn:` job with exit 5 |
| The desk in a browser | Started from the branch against an empty throwaway database with the scheduler off: all fifteen stylesheets load in order with status 200, no console errors, the Morning triage workspace opens Ops, Incidents and Runs with their grids, headers and icons drawn. Not seen with real games in it |
| Not run | CI itself: the new workflow steps were read, not executed, and their first run on GitHub is the test of them. The scheduler against live providers |

### Metrics, start of the day to now

| Metric | `022a1d1` | Now |
|---|---|---|
| Razor files touching a DbContext | 10 | 0 |
| Mud column tags in the app | 126 | 0 |
| `Icons.Material` in the app / in the Desk outside `DeskIcons` | 119 / 78 | 0 / 26 |
| `IngestionJobs.cs` | 1638 lines | 288 |
| `WindowManager.cs` | 2111 | 358 |
| `PartsPanel.razor` | 2028 | 88 |
| `BoardService.cs` | 716 | 342 |
| `EspnStatsAdapter.cs` / `StatsIngestionService.cs` | 1239 / 1259 | 295 / 411 |
| Largest stylesheet | 6282 | 1685 |
| Direct clock reads, Ingestion / readers | 71 / 8 | 0 / 0 |
| csproj properties duplicated across three or more projects | 7 | 0 |
| Tests that can run without Docker, by a filter | none | 824 of XLines.Tests, 11 s |
| Projects whose references a test pins | 4 | 11 |
| CI format step | exit 2 | exit 0 |
| New test methods | | 210 |

## Round four: what round three left

Nine commits on `arch-review-3`, written 2026-11-27 and 28.

| Left after round three | Done |
|---|---|
| Compose healthchecks (no curl or wget in the image) | `dotnet XLines.Web.dll --health` asks the app's own `/health` on loopback and exits 0 or 1, touching no database and writing nothing. Both compose files use it; Caddy waits for `service_healthy`. `/health`, not `/ready`: a database that drops must not restart the desk or keep Caddy down. Against the hardened image: healthy 13 s after a cold start that migrates, still healthy with Postgres stopped, exit 1 with nothing listening |
| The season-start rule at startup | `IngestionOptionsValidator` refuses a host that would walk a league's history with no `Ingestion:Backfill:Seasons` entry, naming the league and the key. The leagues asked about are the ones the backfill walks, worked out as it works them out; the switch being off exempts nothing, since the History window's press starts the same walk. 63 tests took the shipped starts from one helper. The Worker refuses `nhl` with no start |
| The four largest panels | Board 1742, wager 1495, Journal 1494, Odds 1425, Ops 1250, Game 1070 and History 754 lines are now `.razor` files of 378 to 612 lines, with code-behind partials by concern, display text in plain tested classes (`BoardCells`, `JournalDisplay`), and child components (`BoardScoreCell`, `BoardValueCell`, `BoardEmptyState`, `JournalParlays`, `OpsScanningCost`, `GameBetRows`). Rendered markup is unchanged in before/after snapshots, apart from the scope attribute on what moved. Source-reading tests read a panel's whole family through `PanelSource`; two hover-title checks had gone on passing by no longer looking at what moved |
| CSS `@layer` | Measured, not done. Every sheet put in a layer in its current link order changes 160 computed values on 59 of 2,381 elements in Ops, Incidents, Runs and the Parts bin (a no-change control moved 2): the grid's corners go from 7px to 4px and cell borders reappear. Layering is a redesign against MudBlazor's unlayered sheet, not a refactor, and stays with ADR 0034 step 8 |
| CI only read, never run | It has never run on GitHub: the API lists no workflow run on any branch, `main` included. Its commands were run verbatim in the Linux SDK container instead, which found three real problems below |

### What running CI's commands on Linux found

- **The Docker desk wrote money as `¤`.** `C0` and `C2` follow the process culture, and a Linux
  container has none: a stake key read `¤25` and a payout `¤520`. `OddsText.Dollars`,
  `DollarsAndCents` and `OddsMath.Money` write the dollar format out. Seventeen test assertions
  built their expected text with `{amount:C0}`, agreeing with the defect wherever it occurred;
  they expect the text a person reads now.
- **44 integration tests failed in the container recipe CLAUDE.md gives for Smart App Control.**
  Testcontainers publishes on `host.docker.internal` there, which ADR 0023's placement rule reads
  as another machine: a fresh test database was asked for its history before it existed, and the
  retired-source cleanup tests passed over the steps they test. `TestDatabases` creates a test's
  database and initialises it with remote migration allowed, as an operator would. On a runner,
  on localhost, nothing changes.
- **A junk file set at the main checkout's root.** The Journal split's agent wrote four two-line
  files into the wrong folder; they were untracked and deleted.

### Checklist, run on the round-four branch

| Check | Result |
|---|---|
| `dotnet build XLines.slnx` | 0 errors |
| `dotnet test XLines.slnx` on Windows | XLines.Tests 1337, XLines.Web.Tests 1553, XLines.Desk.Tests 2827, XLines.Desk.Hosting.Tests 26: 5743 passed |
| CI's commands verbatim on Linux (SDK container, container recipe) | Restore, Build (0 errors), Verify formatting (exit 0), Unit tests (5235 passed), Integration tests (508 passed), trx and coverage written, pending model changes none |
| The Worker host | Starts against a throwaway Postgres with its options validated; `--job settle` succeeds; `espn:` off exits 5; a league with no season start refuses with the message naming the key |
| Docker images | Web and Worker build; the probe exits 1 with nothing listening; the Worker image refuses an `espn:` job with exit 5 |
| The desk in a browser | From the branch, empty database, scheduler off: Line watch (Board, Journal, Line movement), Ops and History open with no error boundary and no failed panel |
| Flaky tests | Four, each failing once under a full parallel run and passing alone: the three named in round three, and `WagerWindowBooksTests.Choosing_a_book_reprices_the_window_and_logs_at_that_book`, whose assertion includes "Price captured 40 min ago". All wait on a one-second bUnit assertion |
| Not run | CI on GitHub itself. The scheduler against live providers. The desk with real games in it |

### What is left

- `GamecastPanel.razor`, 2079 lines, untouched: another session is working in it.
- `DeskPalette.cs` (1397), `GamecastFeed.cs` (759), `IngestionOptions.cs` (700) are the next
  largest files; none was in the review's list.
- 26 `Icons.Material` references in fifteen Desk primitives, pinned by a list that can only
  shrink, and 2 in the template, which pins the published 0.5.0.
- `@layer` and scoped CSS, with ADR 0034 step 8, measured above.
- Two clock reads (`JournalAuditInterceptor`, `DatabaseInitializer`), the shared read cache,
  bUnit 2, measuring `PassingLinesAsync`, and the four timing-flaky tests.
