# Benchmark Lab interface

## Scope

Presentation-only implementation of the reviewed HTML sketch. All interface text
is English. Benchmark requests, suites, execution order, repetitions, cancellation,
scoring, recommendation algorithms, persistence, and Host authority are unchanged.

## Organization

- Configuration uses the existing compact model, harness, and test switches in
  independent disclosure groups, with a calculated selection summary.
- Run progress and cancellation remain above the inspection tabs.
- Tests in progress uses a grouped Model × Harness selector with Previous, Next,
  and View active test controls. Only one combination's tests are mounted at a
  time; the selector supports the complete current matrix.
- Results keeps pair/model/harness rankings, the expandable matrix, score
  weights, scenario details, and original raw evidence.
- Recommendation distinguishes calculated score, confidence, and local evidence.
- History and comparison retains the existing filters and comparison controls.
- The workspace scrolls as one page. On narrow screens, results precede
  configuration without overlapping it.

## Outcome explanations and help

Live and saved test details show plain-English explanations only for failed
acceptance or incomplete execution. Missing recognized tool evidence is described
as unconfirmed, not proof that the action never happened. Unknown/legacy evidence
gets a neutral fallback. Passed tests add no explanatory text. A combination says
`All tests passed` only when every planned test is present and passed; custom
prompts remain manually reviewed and cannot trigger this banner.

Test cards follow the supplied `teste_benchmark1.html` visual reference: separate
name/ID, status pill, activity timeline, and a bordered advanced-evidence card.
Colors, compact switches, navigation and rounded controls use the existing theme.
Live activity is visible above Advanced details; saved results show the last eight
recorded tool events and retain original complete traces in raw evidence.

Advanced details includes expected/observed comparison tables when evidence
provides both values, grouped property/value tables, copyable hashes, errors,
canonical prompts and persisted turn evidence. Missing observations stay explicit;
changed-file lists are labeled Recorded rather than invented as measured output
paths. A Match describes that row, not overall acceptance. The Help link is in the
card header. Tables can be scrolled with the keyboard on narrow screens.

Score cards, rankings, recommendation cards and historical comparison tables share
the same spacing, headers and borders. Final narrative and workspace/manual review
remain directly accessible. Live updates retain disclosure nodes; saved-result
rescoring retains disclosure state by stable identity.

The header Help link opens the local, offline
[`benchmark-help.html`](../AgenticRouter.Api/wwwroot/benchmark-help.html) guide in a
separate tab. It is the canonical user guide for setup, all scenarios, result
interpretation, scoring, recommendations, automatic harness routing, history and
the evidence glossary. It requires no provider call, CDN or documentation server.

## Reading stability

The Run benchmark form contains only the Prepare run controls. Required review
scores and review notes in saved results do not participate in starting a new run;
Save review retains its own validation. Native validation reveals closed setup
groups before focusing invalid run inputs. Custom scoring weights are validated
and revealed only when the custom profile is selected for the new request.

Live test disclosure and summary nodes are retained during SSE and timer updates.
Switching combinations retains their detached inspection nodes within the current
matrix; entries absent from a subsequent matrix are removed. Updates do not move
the user's selection or switch tabs. View active test is an explicit navigation
action. Finishing or cancelling a run keeps the live inspection available, with
the persisted result separately accessible in Results.

Result rescoring retains the inspected combination and test disclosure state.
Recommendation refresh retains evidence, alternatives, and trace disclosure state
by stable identities. Static configuration, scoring, matrix, and raw-evidence
disclosures are not reset by information updates.

## Validation

- Release solution build: zero warnings and errors.
- Solution-wide `dotnet format --verify-no-changes --no-restore`: passed.
- `node --check AgenticRouter.Api/wwwroot/app.js`: passed.
- Ten focused deterministic browser/API E2E tests: passed. Coverage includes
  failure explanations, all-passed summaries, expected/observed comparisons,
  copy feedback and Help navigation without closing the evidence card, keyboard
  access to wide tables, and desktop/mobile layout. Also covers retained DOM
  identity across updates and cancellation with 30 combinations, rescoring,
  historical comparison, recommendations, Custom Prompt and Real Life Problem.
- No real local model, GPU inference, or cloud-provider validation was performed.
- Form-boundary regression: four browser/API E2E tests passed, covering an empty
  required review score outside the run form, repeat execution without reviewing
  the prior run, invalid run inputs in closed groups, custom-weight focus,
  preserved review validation, and the existing responsive layout.
