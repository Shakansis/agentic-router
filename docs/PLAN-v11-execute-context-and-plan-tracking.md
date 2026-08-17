# PLAN v11: Execute context accuracy and visible plan tracking

## Goal

Restore optional Host-owned execution-plan tracking and make context usage reflect the final specialist payload sent on every Execute inference.

## Scope

- Keep plans optional and specialist-proposed: expose the existing structured plan tools through toolset discovery without making plans a universal execution gate or deriving semantic steps in the Host.
- Let the specialist propose objective, step titles, dependencies, and later bind each planned action to the accepted Host step ID; the Host owns only validation, IDs, transitions, and effect-backed progress.
- Render the existing Host plan chronologically in the main chat with current step, total steps, and changed-file count.
- Remove the browser-side fixed token estimate and show an honest pre-send state until a backend payload snapshot exists.
- Produce sanitized per-category context snapshots from the fully assembled specialist request immediately before provider dispatch.
- Replace estimates only with trustworthy provider-reported input usage for the same specialist call.
- Compact deterministically at the Host boundary, preserve authoritative current state and complete assistant/tool pairs, and apply the existing 70/85/95 thresholds.
- Add manual next-inference compaction through the styled application modal without changing visible or persisted conversation history.
- Preserve the dirty worktree and use only fake-provider E2E validation.

## Ordered work

1. Extend the context contract with sanitized category totals, effective-limit arithmetic, estimator identity, inference sequence, and compaction facts.
2. Build and estimate the final native and structured specialist payloads in one backend mechanism, then publish ordered snapshots before dispatch and exact replacements after usage.
3. Harden deterministic compaction and add a bounded per-conversation manual-compaction preference for subsequent inferences.
4. Restore the existing optional plan tools to discovery, add explicit specialist action-to-step binding, and render only accepted Host plan state in the main chat.
5. Remove the JavaScript `system = 128` path and render honest empty, estimated, exact, warning, and compacted states.
6. Add focused browser/API tests for payload composition, toolset expansion, auxiliary-call isolation, exact replacement, failures, thresholds, compaction preservation, manual compaction, and plan tracking.
7. Run format verification, Release build, focused E2E, full E2E, `git diff --check`, and inspect the complete intended diff.

## Completion evidence

- Context snapshots are measured from the assembled specialist request before each Execute inference and replaced by provider-reported input usage only when available.
- Deterministic automatic and manual compaction preserve the visible/persisted conversation and expose only bounded category totals.
- Plans remain optional. The specialist requests `create_execution_plan`, supplies objective/titles/dependencies, and binds actions to Host-assigned step IDs; the Host performs no title/path-based plan rewrite or action-step inference.
- The main chat renders plan progress only when an accepted plan exists; simple no-plan execution remains supported.
- Release build: zero warnings and errors.
- Focused effect tests: 16/16 passed.
- Focused context, compaction, and plan browser tests passed.
- Full fake-provider E2E suite: 192/192 passed.
- Real Ollama, GPU, and cloud validation intentionally not run without authorization.
