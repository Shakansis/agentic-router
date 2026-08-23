# PLAN v33: Sequential Benchmark Execution

## Goal

Ensure one benchmark suite executes exactly one model-facing test at a time.
Harnesses run in the normalized request order, and each harness completes its
ordered tests before the next harness starts.

## Confirmed cause

`BenchmarkEngine.RunSuiteCoreAsync` currently builds one `RunHarnessAsync` task
per selected harness and awaits them with `Task.WhenAll`. Tests are already
sequential inside each harness, but all selected harnesses compete for the same
local model concurrently.

## Smallest complete change

1. Replace the harness task fan-out with an awaited `foreach` that retains the
   normalized request order.
2. Preserve per-test timeouts, failure continuation, cancellation, progress,
   scoring, persistence, and final result ordering.
3. Update the live benchmark E2E contract to prove:
   - a harness starts only after the preceding harness completed;
   - a test starts only after the preceding test in that harness reached its
     terminal state;
   - all existing lifecycle, persistence, and ranking evidence remains intact.
4. Keep MSTest class-level parallelism independent from production benchmark
   scheduling. Deterministic E2E batches use isolated fake providers and do not
   authorize concurrent real-model benchmark execution.

## Validation

1. Isolated Release build with zero warnings/errors.
2. Focused automated and live benchmark E2E coverage.
3. Complete deterministic fake-provider E2E suite.
4. Formatter verification and intended-diff inspection.

## Boundary

Do not invoke a real local model, GPU workload, or cloud provider. Do not change
benchmark scoring, acceptance, timeouts, requested harness order, or failure
continuation semantics.

## Results

- Removed the per-harness `Task.WhenAll` fan-out and retained request order with
  one awaited harness loop; each harness retains its existing ordered test loop.
- Live lifecycle E2E passed and proves terminal-before-next ordering across four
  harnesses and all sixteen harness/test combinations.
- Non-live suite/persistence E2E passed with all twelve expected results.
- Complete deterministic fake-provider suite passed 277/277 in 165.5s.
- Isolated Release build passed with zero warnings and zero errors.

Status: implementation and deterministic validation complete. No real-model
execution was initiated as part of this validation.
