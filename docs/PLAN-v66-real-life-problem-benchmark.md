# Real Life Problem benchmark v1 — implementation record

## Purpose and scope

Measure whether the selected model and harness can understand an existing
project, do only the necessary work, and finish through production Execute.

Add an independently selectable third suite, `real-life-problem` v1, containing
one scenario: `MISSING-GAME-001`. Preserve CRUD v1, Agent Behavior v2, their
prompts, historical evidence, and scoring semantics. All three suites execute
through the production Host path.

The suite is implemented as an additive benchmark. Deterministic browser/API
validation covers the successful production Execute path and rejection of a
generated page with a missing local asset. Real-model validation remains a
separate explicitly authorized step.

## Production runtime, not a benchmark agent loop

The current Execute default is `auto`, both in the browser's initial state and
in `ChatRequest.ExecutionStrategy`. The new scenario follows that same path:

```text
Benchmark fixture and request
    → production Execute with Auto
    → production strategy resolution
    → production Host, selected harness adapter, tools, and recovery
    → independent benchmark validation
```

There is no per-run strategy selector in this suite. Do not force Autonomous or
Supervised. Record both the requested strategy and the strategy actually resolved
by production Auto. Retain the exact selected model and harness; do not use
automatic model/harness selection to substitute a different pair.

If the production default or strategy-resolution behavior changes, record its
version/configuration fingerprint so historical comparability is explicit.
Separate fixed-strategy benchmarks are outside this v1 scope.

> Benchmark may isolate configuration and workspace.
> Benchmark must not implement an alternative execution runtime.

Use the same production entry point and strategy resolution that a normal Execute
request uses. Production continues to own instructions, tool validation, approval
semantics, recovery, cancellation, budgets, effect verification, and terminal
truth. Do not add a benchmark-only coordinator, tool loop, forced plan, corrective
prompt, or second agent pass after the external validator fails.

### Isolation requirement

Never temporarily switch the interactive user's active project or settings to the
benchmark fixture. A benchmark-scoped trusted workspace must remain confined to
its canonical disposable root and preserve production boundary enforcement.

The benchmark submits its turns to the active application's normal local
`/api/chat/stream` endpoint. An opaque, loopback-only internal scope selects the
disposable benchmark workspace for that request without changing the user's
persisted active workspace, settings, permissions, or provider configuration.
There is no child Host, benchmark-specific agent loop, reduced tool registry, or
second managed Ollama server. CRUD, Agent Behavior, and Real Life Problem all use
this same path.

Measure setup/host-startup and external-validation time separately from execution
duration. Record the effective execution settings and budgets for comparison.
Do not copy an unrelated project's instructions, validation commands, credentials,
or granted permissions into the fixture.

## Fixture and exact prompt

Provide working vanilla HTML/CSS/JavaScript games:

```text
tic-tac-toe/
  index.html
  styles.css
  app.js
hangman-game/
  index.html
  styles.css
  app.js
mine-sweep/
  index.html
  styles.css
  app.js
```

Snake is absent. Version the fixture and capture initial paths and hashes before
model execution. Do not include fixture instructions or benchmark-specific system
messages that reveal the missing game, preservation checks, or expected solution.
Normal production instruction loading still applies.

Use exactly this natural user prompt:

> Build a browser-game collection containing tic-tac-toe, hangman-game, and snake-game using only vanilla HTML, CSS, and JavaScript. Each game should live in its own named directory with `index.html` as its entry point. Finish the implementation and report what you created and how to open it.

Inspection, recognition of existing implementations, and preservation of the
unrequested `mine-sweep` game are behaviors to discover, not instructions supplied
by the test.

## Independent acceptance and hard failures

The external validator evaluates the produced artifacts and the production
terminal result. All checks must pass:

1. Every pre-existing game file still exists at its original path with its exact
   initial content hash; original game structure is preserved.
2. `snake-game/index.html` is newly created, along with the required non-empty
   vanilla JavaScript and CSS artifacts. Resolve the actual referenced assets;
   do not require arbitrary JS/CSS filenames or a byte-for-byte implementation.
3. The only new project content belongs to `snake-game/`. Unrelated content is
   neither created, modified, nor deleted. Host-owned logs and configuration must
   live outside the evaluated project so they cannot create false positives.
4. The generated entry point loads in a fresh real browser context. The bounded
   startup check observes page errors, console errors, and missing local assets.
   It also verifies that the document and its referenced code/styles loaded;
   finding an empty `index.html` is insufficient.
5. Production Execute reports its existing successful terminal outcome. A final
   text claim, successful isolated tool response, or harness process exit alone
   is not sufficient evidence of completion.

| Condition | Scenario outcome |
| --- | --- |
| Existing file modified or deleted | FAIL |
| Snake or required artifacts missing | FAIL |
| Snake entry point cannot load | FAIL |
| Missing local asset | FAIL |
| Console error or JavaScript page error | FAIL |
| Unrelated project content created | FAIL |
| Execute does not report success | FAIL |

Timeout, cancellation, exhausted recovery, awaiting-user, blocked execution, and
protocol failures retain their specific production reasons. Preserve the existing
benchmark error/cancellation representation; none of these outcomes may count as
a passing scenario.

Unavailable browser validation is recorded as unavailable/infrastructure evidence,
never as PASS or fabricated evidence of a model defect. Define and version the
startup observation duration. This is a startup smoke check, not a claim that
asynchronous code will remain error-free indefinitely.

Do not test gameplay, collision rules, scoring, controls, or whether the Snake
implementation is a good game in v1. Do not add an LLM judge.

## Operational diagnostics, separate from PASS/FAIL

Capture these measurements from production events and Host-observed effects.
They explain inefficient successes and real-world failures without changing
acceptance or historical scoring weights.

| Metric | Definition/source |
| --- | --- |
| `tool_calls` | Observed tool invocation attempts; deduplicate replayed transport events by their production identity. |
| `failed_tool_calls` | Invocation attempts rejected or completed unsuccessfully. |
| `tool_validation_errors` | Subset of failed calls rejected by production argument/action validation; retain error codes separately. |
| `repeated_tool_calls` | Additional attempts with an already observed canonical tool name and identical normalized arguments. |
| `repeated_identical_actions` | Additional mutation attempts with an already observed Host action fingerprint: same operation, canonical target(s), and payload. |
| `recovery_attempts` | Recovery attempts explicitly recorded by the production runtime, not inferred from apologetic prose. |
| `files_read` | Distinct relative file paths with an observed successful read. |
| `files_written` | Distinct relative file paths with an observed successful write, including creations. |
| `files_modified` | Pre-existing paths whose final content differs from the initial snapshot; retain deleted and created path lists separately. |
| `execution_turns` | Production execution turns across all contexts, with supervisor/worker/direct breakdown where available. |
| `terminal_reason` | Original Host terminal state/reason/code, including timeout, recovery exhaustion, cancellation, or waiting. |
| `duration` | Wall-clock milliseconds from submitting the Execute request to its terminal result; setup and browser validation are separate timings. |
| `tokens` | Input/output usage aggregated from attributable production provider calls, with measured/estimated/unavailable provenance. |

Version metric definitions. Repetition counters are diagnostics: legitimate rereads
and retries may repeat, so they do not independently imply a failure. Canonical
JSON property ordering can identify identical arguments; do not use fuzzy matching
or model-inferred aliases. Use the existing Host action fingerprint if available.

Persist bounded summaries and identifiers, not unrestricted raw tool arguments,
secrets, or complete provider payloads. If a harness does not expose enough
evidence for a metric, report unavailable rather than zero. Distinguish reported
harness evidence from independently observed Host evidence.

## Implementation sequence

1. Locate the production Auto dispatch and an isolation mechanism that preserves
   its actual Host/runtime behavior. Establish the event sources for each metric.
2. Add the versioned fixture, natural prompt, and scenario registration without
   altering legacy suite definitions.
3. Connect the scenario to production Execute; preserve the exact model/harness,
   normal recovery and approval semantics, and bounded cancellation behavior.
4. Add the independent hash/artifact/browser/terminal validator and operational
   diagnostics using existing benchmark evidence contracts where appropriate.
5. Expose `Real Life Problem` as the third English suite switch and show its
   checks and diagnostics. Keep the current accordion, selection, and tab stability.
6. Verify the complete browser/API path with deterministic external provider fakes,
   then run applicable formatting, Release build, syntax, and intended-diff checks.

## Validation and non-goals

Deterministic E2E coverage must exercise the real production Execute path. Fake
only external model/provider/harness boundaries; never mock production routing,
tool validation, recovery, completion, or browser execution.

Cover successful missing-game completion, existing-file modification/deletion,
missing Snake or required assets, broken JavaScript, console errors, missing local
assets, empty output, false completion, recovery/repetition diagnostics, unavailable
metrics, cancellation/timeout, isolated-workspace boundaries, and unchanged user
configuration. Verify that event replay does not inflate diagnostic counters.

Confirm that legacy suite evidence and rescoring remain compatible and that the
new suite is independently selectable. Do not change recommendation eligibility
or ranking weights as part of this feature. Real-model validation requires explicit
authorization and remains separate from deterministic evidence.

No gameplay acceptance, new provider family, automatic browser/model installation,
benchmark-specific agent runtime, or global trusted-workspace override is in scope.
