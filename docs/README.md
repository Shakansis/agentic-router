# Documentation map and authority

This directory contains both current product documentation and historical
engineering evidence. Age or a `PLAN-` prefix does not make a file executable
product scope.

## Current behavior and maintainer contract

Use these sources for the current checkout, in this order:

1. [`../AGENTS.md`](../AGENTS.md) for stable product and engineering invariants;
2. [`../README.md`](../README.md) for current user-visible behavior, setup, and API overview;
3. the first/current release section in [`../RELEASE_NOTES.md`](../RELEASE_NOTES.md);
4. [`local-first-specialist-runtime-architecture.md`](local-first-specialist-runtime-architecture.md) for the current Chat/Execute authority split;
5. focused current references such as
   [`settings-configuration-inventory.md`](settings-configuration-inventory.md),
   [`harness-capability-matrix.md`](harness-capability-matrix.md), platform support,
   security, backup, and distribution documents.

When a historical document conflicts with those sources or current tests/code,
it is historical unless the user explicitly approves it as the active feature
specification.

## Plans and decisions

`PLAN-*.md`, versioned decision files, cumulative reports, and older release-note
sections are implementation records. Their own status/evidence sections describe
what was proposed, completed, cancelled, superseded, or left open at that time.
They are useful provenance, not promises that every described mechanism remains
in production.

In particular, documents describing resident coordinators, FunctionGemma action
routing, mandatory model-authored plans, or live behavioral-conformance gates
predate the direct selected-specialist runtime. The current replacement is
[`local-first-specialist-runtime-architecture.md`](local-first-specialist-runtime-architecture.md).

## Research, benchmarks, and validation evidence

- `research/` contains time-bounded external research and evaluations. A finding
  does not authorize product adoption.
- `benchmarks/` and cumulative reports summarize specific model/runtime evidence;
  results do not generalize to a different model digest, provider, or harness.
- `validation/` is generated/raw audit evidence. Preserve it as historical input;
  do not rewrite old outcomes to match current behavior.

## Open cleanup decisions

The current unresolved keep/migrate/remove choices and the fresh real-acceptance
matrix are recorded in
[`CLEANUP-AUDIT-2026-08-30.md`](CLEANUP-AUDIT-2026-08-30.md). Compatibility
fields are documented there and in the settings inventory; they must not be
presented as live capabilities while they await a versioned migration decision.
