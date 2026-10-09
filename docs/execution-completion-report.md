# Completion report

A Direct, Supervisor or Autonomous Execute attempt finishes without requesting a
model review. Its final footer offers **Completion check**. Only an explicit
click requests one advisory review using the selected execution model and provider.
The report is a separate, tool-free Host summary call; it does not start another
harness turn, change the execution route, or give the model any mutation capability.
Chat, cancelled attempts and failed streams do not initiate this review.

The original objective, bounded assistant claims, and recorded Host evidence
(file changes/diff excerpts and validation summaries) supply the review. The model
must distinguish Done, Missing and Not verified, in the user's language. Omitted
or insufficient evidence must remain unverified. The report is model assessment,
not independent proof of functional acceptance; it never overrides Host status.

There is one review request, with the configured generation timeout, no report
repair loop, automatic correction, new work item, or retry of the objective.
Failure leaves an unavailable-report notice and preserves execution results.
The user may use the listed omissions to write a new request voluntarily.

The button opens a separate Completion report panel. History restores it collapsed.
Its content is rendered as text, outside the assistant answer. The button displays
Checking while the optional request is pending. Checking never changes the original
terminal state or adds to its execution duration. Usage is recorded as a summary call with purpose
`execution-completion-report`; it is separate from execution inference metrics.
History retains the requested report in the existing terminal event when enabled.
Supervised checkpoints retain the check identity, not a mandatory review phase.
Reopening/replaying a result does not dispatch another review. Older records omit the panel unless they already contain a report.
The Host retains at most 32 idle recent check contexts in memory for volatile
turns; saved turns can reconstruct their review from the existing history after
restart. The endpoint accepts identities, not browser-supplied claims/evidence.
Concurrent clicks serialize per check and reuse its result. No tools or task
retry are authorized by clicking Completion check.

# Auto routing speed preference

Router v3 no longer treats generic performance, speed, efficiency or their
Portuguese equivalents as an efficiency-first preference. These may describe the
software being examined. Existing task category ordering otherwise stays intact.

An affirmative response/execution speed request at the beginning of a clause can
select efficiency-first: for example, `Respond quickly`, `Prioritize execution
speed`, `Use the fastest harness`, `Responda rapidamente`, `Priorize a velocidade
da resposta`, or `Use o harness mais rápido`. Explicit speed preferences precede
task categories. Negated directives such as `Do not respond quickly` and `Não
priorize a velocidade da resposta` do not match. This remains bounded keyword
routing, not unrestricted natural-language intent understanding. Manual benchmark
category/profile choices and retained active routes are unchanged.

## Original automatic-report validation on 2026-10-04 (superseded behavior)

- Release solution build using `--no-restore -m:1 -nr:false
  -p:UseSharedCompilation=false -p:OutputPath=bin/product-review/Release/`:
  zero warnings/errors; the running user's binaries were not replaced.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`, `node
  --check` on the three changed scripts, and `git diff --check`: passed.
- 36 distinct browser/API E2E cases passed across the final results for each
  case: 17 new report/routing cases and 19 existing routing, supervised,
  autonomous, history and completion-metrics regressions. All five harnesses
  have direct-report coverage; supervised Native and external harness paths are
  also exercised. The six initial rendering-fixture failures were corrected by
  supplying the current conversation revision to historical replay; application
  assertions were retained.
- Results: `tests/AgenticRouter.EndToEndTests/TestResults/product-review/` contains
  `product-rules.trx` (19 passing regressions plus the earlier new-test results)
  and `completion-report-final.trx` (17/17 new cases). Visual inspection of the
  expanded panel passed, including literal rendering of an HTML injection fixture.
- No real-model/GPU/cloud inference was run. The tests prove orchestration,
  persistence, report isolation and routing rules, not model assessment quality.
