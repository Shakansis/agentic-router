# Agentic Router cleanup audit - 2026-08-30

## Purpose

Separate proven dead material from active compatibility code and unresolved
product decisions. This audit intentionally avoids treating age, a low textual
reference count, or an old plan number as proof that something is obsolete.

## Removed in this pass

| Item | Evidence | Result |
| --- | --- | --- |
| `AgenticRouter.Api/data/claude-code-runtime/` | 102 tracked runtime files contained generated Claude session/project transcripts, local absolute paths, process IDs, and machine/user identifiers. The harness creates the directory when needed. | Removed from the source tree and ignored for future runs. Historical Git commits still contain the old files; purging history would be a separate destructive repository operation. |
| Root HTML prototypes | `layout-example.html`, `layout-example-upgrade.html`, and `benchmark_modal_example.html` were standalone mockups with inline code and no production import. Only README/one historical plan mentioned them. | Removed; surviving documentation no longer presents them as live assets. |
| Pre-M10 one-off scripts | Four scripts had dated output paths, no current references, and existed only to produce the preserved 2026-08-23 evidence campaign. | Removed. Current product benchmark and release scripts remain. |
| `shortenPath` | The frontend function had one occurrence: its declaration. | Removed. |
| Stale README claims | README still named the deleted resident manager, claimed implemented cloud/persistence/process behavior was absent, and carried an unrelated future-vision promise. | Replaced with the current structure and project-level non-goals. |

## Confirmed active or intentionally retained

- The current dirty changes that remove FunctionGemma resident routing,
  preloading, eviction, takeover, and parser code are coherent with the current
  architecture document. Legacy router/action settings readers remain for data
  migration and must not be deleted until a versioned settings migration is
  chosen.
- `coordinatorModel` remains on persisted/session contracts even though the UI
  calls it Specialist. Renaming it is a public-contract migration, not dead-code
  cleanup.
- The Host tool aliases, schemas, approval coordinator, execution session facts,
  review/undo, process policy, Git delivery, harness registry, provider registry,
  runtime profiles, and benchmark services are all reachable through DI,
  controllers, or browser/API tests.
- The C# declaration scan found no production type whose only occurrence was its
  declaration. Controllers and DI services commonly have two textual occurrences
  because ASP.NET/DI activates them indirectly; that is not evidence of dead code.

## Decisions for the next review

### 1. Historical validation corpus

`docs/validation/` contains 1,064 tracked files (about 32.88 MB), mostly raw
pre-M10 model/harness evidence. It is not used at runtime, but it is the audit
trail behind benchmark and compatibility claims.

Decision: keep the raw corpus in Git, move it to a release/archive repository,
or retain only signed summaries plus hashes. Do not simply delete it while
current documentation relies on that evidence.

### 2. Seed benchmark history

`AgenticRouter.Api/data/benchmark-results/` and
`AgenticRouter.Api/data/benchmark-recommendations/` contain 13 tracked files
(about 1.80 MB). Unlike ordinary runtime residue, these files can affect the
initial Auto model/harness ranking.

Decision: intentionally ship this baseline evidence, replace it with an explicit
seed catalog, or start installations with no historical recommendation. Removing
it changes user-visible routing behavior.

### 3. Plans and experiment reports

There are 69 `PLAN-*.md` files (about 0.31 MB). They mix completed implementation
records, cancelled model/harness evaluations, manual-acceptance checklists, and
current specifications. Their size is negligible, but their status is hard to
discover and some read like open promises.

Decision: keep them as immutable history under `docs/archive/`, add a compact
index with `implemented`, `superseded`, `cancelled`, and `open` states, then keep
only active specifications at the top level. Deleting them wholesale would lose
decision provenance without meaningfully reducing the repository.

### 4. Local ignored output

The checkout currently contains about 228.18 MB under `.tmp/` and 1,226.40 MB
under `artifacts/`. They are ignored and do not affect source or builds, but some
folders are manual-release workspaces and packaged releases rather than trivial
compiler caches.

Decision: delete all ignored output, retain only the latest release package, or
move release artifacts outside the source checkout. No local output was deleted
in this pass because recovery value cannot be inferred from Git.

### 5. Compatibility debt that is not yet removable

The current architecture document identifies three concrete migrations:

1. remove legacy router/action settings fields after a versioned schema migration;
2. rename persisted `coordinatorModel` to `specialistModel` through a versioned
   public-contract migration;
3. rename `LOCAL_ACTION_PLANNER_V1` after real-model compatibility evidence.

These are useful cleanup targets, but each changes a persisted or model-facing
contract and needs an explicit keep/migrate/kill decision.

### 6. Frontend work still awaiting acceptance

- PLAN v42 project/sidebar simplification still records the requested manual
  browser checklist as open.
- PLAN v58 local-resource onboarding has deterministic coverage described, but
  the current combined dirty worktree has not yet regained a clean full-suite
  baseline.
- PLAN v61 trace investigation passed its focused tests; the same full-suite
  baseline issue prevents treating the entire browser suite as green.

These are pending validation/acceptance, not ghost code.

## Next proposed gate

1. Diagnose and correct every reproducible code/test defect in the combined
   worktree, beginning with the aborted browser initialization.
2. Re-run focused regressions after each correction and then run the complete
   deterministic browser/API suite with no weakened assertions or hidden skips.
3. Review the complete intended diff for bad code, unreachable branches,
   duplicate mechanisms, stale compatibility paths, warnings, formatting, and
   repository residue.
4. Decide the disposition of plans, validation evidence, seed benchmark data,
   and ignored release artifacts.
5. Only then perform any approved versioned settings/session contract cleanup.
6. After the deterministic baseline is fully green, execute the real acceptance
   matrix below. Keep deterministic, manual, and real-provider evidence separate.

## Required real acceptance matrix

Real acceptance is the final gate, not a substitute for the deterministic
suite. Each scenario must record the exact model, provider, harness, workspace,
trace identifier, observed Host effects, final answer, elapsed time, and any
recovery/fallback. A narrated success without the required effect does not pass.

| Scenario | Required observation |
| --- | --- |
| Ordinary Chat | The selected real route returns one coherent streamed answer, terminates once, persists correctly, and remains resumable. |
| Web search | The effective route exposes Web automatically, obtains current external evidence, renders usable citations, and does not perform an eager or hidden search when the request does not need it. |
| Code creation | In an isolated trusted workspace, Execute creates a small requested artifact, the Host verifies the bytes/change set, and the final report matches the observed result. |
| Code editing | Execute modifies an existing supplied file without overwriting unrelated changes, validates the intended behavior, and reports the exact changed files. |
| Document editing | Validate the intended document contract in an isolated workspace. Before running, decide whether this means text/Markdown documents already supported by Host file tools or a richer Office/PDF format requiring a separately approved capability. |

Do not invoke a real Ollama/GPU workload or cloud provider before the user
confirms the exact model + provider + harness matrix for this final gate. Do not
silently fall back to a different route. Stop all work-started servers, fake
providers, browser processes, and test hosts after validation.

## Validation from this pass

- `dotnet build AgenticRouter.slnx -c Release --no-restore`: passed with zero
  warnings and zero errors.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed
  outside the sandbox after the sandboxed named-pipe build host was denied.
- `node --check AgenticRouter.Api/wwwroot/app.js`: passed.
- `git diff --check`: passed.
- The initial browser failure was traced to a missing required DOM element:
  `bindEvents` registered `#settings-show-onboarding-now`, but `index.html` did
  not contain that button. Adding the approved Settings > Local resources action
  restored initialization. `LoadsVersionModelsAndCleanGpuNames`,
  `InitialSetupAppearsForLegacyReadyInstallUntilUserOptsOut`, and
  `LocalSetupReportsActiveResourcesAndPullsOnlyRecommendedModels` now pass 3/3.
- The complete deterministic suite now reaches 354/360. The six remaining
  failures are no longer initialization failures: three assert partial Chat text
  before completion even though the new Chat read-only tool path buffers answer
  content; one expects three reasoning DOM blocks while the UI renders one; one
  expects failed-turn context to contain two provider messages but observes four;
  and one had a stale canonical-tool expectation after `get_trace_diagnostic`
  was added. The last expectation was corrected and its focused test passes 1/1;
  the five behavioral mismatches remain for an explicit keep/fix decision. The
  full suite was not rerun a second time after this test-only correction.
