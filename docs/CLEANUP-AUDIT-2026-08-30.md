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
| Unreachable resident recovery branch | `ExecuteActionsAsync` had one caller and it always supplied `recoverySpecialistModel = null` and `fallbackToResident = false`. Resident guidance, takeover, tooling inspection, message compaction, and retry-accounting helpers were declaration-only or reachable only through those constant arguments. | Removed the dead parameters, branches, records, constant, and helpers. The active specialist loop and Host-owned explicit recovery checkpoints remain. |
| Failed assistant context | A terminal failed answer was both rendered as a failure and appended to `state.history`, causing later provider context to contain four messages instead of the expected clean two-message turn. | Failed output remains visible/persisted as terminal evidence but is excluded from later model context. |
| OpenAI-compatible null tool fields | Real Groq rejected `tool_calls: null` on a system message, a contract issue the permissive fake provider had not exposed. | OpenAI-compatible serialization now omits null fields; a body-inspection regression prevents recurrence and the same live Groq model passed on retry. |
| Stale README claims | README still named the deleted resident manager, claimed implemented cloud/persistence/process behavior was absent, and carried an unrelated future-vision promise. | Replaced with the current structure and project-level non-goals. |

## Confirmed active or intentionally retained

- FunctionGemma resident routing, preloading, eviction, takeover, and the final
  unreachable resident-recovery helpers are absent from the current runtime.
  Legacy router/action settings readers remain for data migration and must not
  be deleted until a versioned settings migration is chosen.
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

## Pending decisions

### 1. Historical validation corpus

`docs/validation/` contains 1,073 files after this pass (about 33.60 MB), mostly raw
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

There are 70 `PLAN-*.md` files (about 0.32 MB). They mix completed implementation
records, cancelled model/harness evaluations, manual-acceptance checklists, and
current specifications. Their size is negligible, but their status is hard to
discover and some read like open promises.

Decision still needed: keep them in place, move immutable history under
`docs/archive/`, or curate individual status metadata. A compact authority map
now exists in `docs/README.md`, so old plans are no longer presented as current
product promises. Deleting them wholesale would lose decision provenance
without meaningfully reducing the repository.

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

### 6. Frontend acceptance status

- PLAN v42 project/sidebar simplification still records the requested manual
  browser checklist as open.
- PLAN v58 local-resource onboarding and the previously failing initialization
  baselines pass on pushed commit `d4dc539`; complete manual acceptance remains
  open.
- PLAN v61 trace investigation passes its focused tests and the complete
  deterministic suite. Completed turns show the trace ID without an investigation
  action; failed turns expose one transparent `Investigate error` action. Real
  successful Chat/Groq turns also showed the trace without the error-only button.

The PLAN v42 and v58 manual checklists remain pending acceptance, not ghost
code. PLAN v61 is implemented and validated.

## Proposed order for the next review

1. Decide the disposition of plans, validation evidence, seed benchmark data,
   and ignored release artifacts.
2. Decide whether to keep, migrate, or remove the compatibility fields listed
   above; only then perform a versioned settings/session/YAML migration.
3. Complete or explicitly close the remaining PLAN v42 and v58 manual browser
   checklists.

## Completed real acceptance matrix

Real acceptance was the final gate, not a substitute for the deterministic
suite. Each scenario recorded the exact model, provider, harness, workspace,
trace identifier, observed Host effects, final answer, elapsed time, and any
recovery/fallback. A narrated success without the required effect does not pass.

The local matrix uses exact Ollama model `qwen3.8:27b-gpu0` with every current
harness: `native`, `codex`, `claude-code`, `opencode`, and `qwen-code`. Each pair
must be exercised independently; Auto routing is additional coverage and cannot
replace an exact pair.

Groq is the only online model provider in scope. It is a separate cloud-provider
acceptance path, not a five-harness cross product: Codex, Claude Code, OpenCode,
and Qwen Code currently reject non-Ollama providers by explicit contract. Use
the exact enabled Groq model observed from the live catalog (the current product
and deterministic fixtures use `groq::openai/gpt-oss-120b`) and do not test
Google AI Studio or Cerebras.

| Scenario | Required observation |
| --- | --- |
| Ordinary Chat | The selected real route returns one coherent streamed answer, terminates once, persists correctly, and remains resumable. |
| Web search | The effective route exposes Web automatically, obtains current external evidence, renders usable citations, and does not perform an eager or hidden search when the request does not need it. |
| Code creation | In an isolated trusted workspace, Execute creates a small requested artifact, the Host verifies the bytes/change set, and the final report matches the observed result. |
| Code editing | Execute modifies an existing supplied file without overwriting unrelated changes, validates the intended behavior, and reports the exact changed files. |
| Application documentation | Audit all authored application documentation by purpose rather than extension: user/maintainer guides, plans, architecture and decision records, release notes, research/benchmark reports, distribution instructions, and relevant inline configuration guidance. Cross-check claims against current code, routes, settings, UI, tests, and release state; correct obsolete, contradictory, missing, or still-promissory information. Raw generated validation evidence remains immutable evidence unless its provenance or classification is itself wrong. |

The user confirmed the local model, all five harnesses, and Groq-only online
provider scope on 2026-08-30. The run verified the exact live Groq model identity
and protected-key availability without exposing the key. No route silently fell
back, and all work-started servers, fake providers, browser tabs, build nodes,
and test hosts were stopped after validation.

The completed real results, exact traces, harness versions, artifact hashes,
the first Groq failure, correction, and successful retry are recorded in
[`real-acceptance-2026-08-30.md`](real-acceptance-2026-08-30.md).

## Validation from this pass

- `dotnet build AgenticRouter.slnx -c Release --no-restore`: passed with zero
  warnings and zero errors.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed
  outside the sandbox after the sandboxed named-pipe build host was denied.
- `node --check AgenticRouter.Api/wwwroot/app.js`: passed.
- `git diff --check`: passed.
- Post-push reassessment: `HEAD`, `origin/main`, and upstream are aligned at
  `d4dc539`; the worktree was clean before this audit update. Release build
  passed again with zero warnings/errors. The two formerly failing browser
  baselines `LoadsVersionModelsAndCleanGpuNames` and
  `InitialSetupAppearsForLegacyReadyInstallUntilUserOptsOut` passed together
  2/2 in 5 seconds outside the sandbox.
- The initial browser failure was traced to a stale required-DOM binding for
  `#settings-show-onboarding-now`. The final product decision removes that
  redundant action, its preview-only JavaScript state, and the stale binding;
  the onboarding regression now covers the retained automatic flow and settings
  preference while asserting that the removed button stays absent.
- The complete deterministic suite passed 360/360 with zero failures and zero
  skipped tests in 4m28s (`cleanup-after-fixes.trx`). Four stale tests were aligned
  with the approved buffered Chat read-only contract and consolidated reasoning
  rendering; one cancellation cleanup race was made deterministic; and the real
  failed-turn context leak was fixed in production code.
- A subsequent Release build after unreachable resident-recovery removal passed
  with zero warnings and zero errors; the complete suite was rerun before real
  acceptance began.
- The real matrix completed on exact `qwen3.8:27b-gpu0`: Native, Codex,
  Claude Code, OpenCode, and Qwen Code each produced one independently observed
  requested effect. Local Chat and automatic Web passed through the production
  UI. The live Groq run first exposed a null-field request-contract defect; after
  the focused fix and 1/1 regression, the same
  `groq::openai/gpt-oss-120b` route returned `GROQ OK` with no fallback.
- Final post-fix validation: Release build zero warnings/errors, formatter,
  JavaScript syntax, and diff check passed; `cleanup-final-after-groq.trx`
  passed 360/360 with zero skipped tests in 4m29s.
