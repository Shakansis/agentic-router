# PLAN v65 - Sequential benchmark repetitions

## Problem

A Benchmark Lab submission executes one selected Model × Harness matrix. Users
who need repeated measurements must currently start identical runs manually,
which is error-prone and makes collection of comparable samples unnecessarily
laborious.

## Decision

1. Add a `Sequential runs` control with a default of 1 and a bounded range of
   1–20.
2. Reuse the existing live benchmark endpoint for every repetition. Start the
   next run only after the current run reaches a terminal state.
3. Give every repetition a distinct run ID and preserve it as an independent,
   immutable history result. Do not combine raw evidence or change the benchmark
   result schema.
4. Keep the selected models, harnesses, suites, timeout, and scoring profile
   fixed for the complete batch.
5. Canceling the current run marks the batch canceled and prevents remaining
   repetitions from starting. A run-level startup or infrastructure failure
   also stops the batch rather than repeating an identical deterministic
   failure.
6. Persist bounded batch coordination in browser session storage so a page
   reload can reconnect to the active run and continue the remaining sequence.

## Validation boundary

Automated E2E validation uses the fake Ollama boundary and verifies that three
requested repetitions produce three distinct historical run IDs. Real model
execution remains explicitly user-authorized.
