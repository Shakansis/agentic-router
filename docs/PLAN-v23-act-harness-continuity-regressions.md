# PLAN v23: ACT and Harness Continuity Regressions

## Goal

Correct the recent ACT regressions without reverting the recent harness and
Benchmark Lab work wholesale. Remove prompt-only blanket restrictions and the
generated inventory of pre-existing paths, preserve workspace confinement and
unrelated user work through Host enforcement, and keep the Agentic Router
conversation continuous while Execute harnesses change.

## Evidence at start

- Commit `3b4bd88` and the current working tree contain the recent harness
  unification and Benchmark Lab work. The checkout is intentionally dirty and
  those changes must be preserved unless a targeted correction is required.
- `OpenCodeHarness` and `QwenCodeHarness` currently inject blanket prohibitions
  plus every pre-existing changed path. `AgentHarness` carries related prompt
  guidance. These instructions consume context and incorrectly turn ordinary
  workspace state into a prompt-level mutation restriction.
- The Host already owns trusted-root validation, structured file actions,
  approvals, effect verification, and terminal truth. These remain the actual
  enforcement boundary.
- Harness adapters currently receive a turn prompt and maintain independent
  native session identifiers. Conversation history/handoff behavior must be
  traced through `ChatStreamService`, request contracts, and each adapter before
  choosing the smallest synchronization seam.

## Investigation and implementation

1. Diff `3b4bd88`, `d48a03c`, and the current working tree by concern: prompt
   construction, harness request/session contracts, file actions, approvals,
   event translation, completion state, and Benchmark Lab integration.
2. Document the canonical conversation data already available at the Host
   boundary and how Codex, OpenCode, Qwen Code, and Native currently create or
   resume sessions.
3. Remove the generated protected-path inventory and the named blanket prompt
   prohibitions. Replace only the preservation wording needed for task-scoped
   behavior; do not recreate capability policy as prose.
4. Add the smallest adapter-level context hydration/delta mechanism that keeps
   AR canonical, resumes native harness sessions where supported, and carries
   intervening canonical conversation facts across harness switches without
   blindly resending an unbounded transcript.
5. Keep CRUD authority in structured Host capabilities. Correct any confirmed
   path-existence mutation denial separately from harness context and report
   genuine per-harness capability gaps.
6. Add deterministic end-to-end regressions for existing-file update/delete,
   cross-turn created-file mutation, referential harness handoff and round-trip,
   and preservation of unrelated changes. Extend fake harness boundaries only
   where needed to prove the actual browser/API path.
7. Audit the remaining Spark-window changes and classify each relevant finding
   as fixed, intentionally retained, good retained, unchanged issue, or
   follow-up.

## Validation

1. Focused deterministic E2E tests for prompt construction, CRUD, harness
   handoff, and harness round-trip.
2. `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
3. `dotnet build AgenticRouter.slnx -c Release` with zero warnings and errors.
4. Full Playwright E2E suite within the existing per-test timeout.
5. `git diff --check`, complete intended-diff review, and byte-for-byte final
   fixture assertions where applicable.
6. No real Ollama/GPU or cloud inference without separate explicit permission.

## Scope boundary

Do not redesign benchmarking, add a generic session framework, change unrelated
UI, weaken trusted-workspace confinement, bypass approval policy, or overwrite
unrelated current work. Missing failure taxonomy may be reported without a
benchmark redesign.

## Completed evidence

- Removed `ProtectedPaths` from the harness turn contract and removed the
  generated path inventory plus blanket shell/Git/subagent/network/sharing
  prohibitions from Codex, OpenCode, and Qwen Code prompts.
- Added bounded canonical conversation hydration for a newly selected harness
  and sequence-based delta synchronization for a resumed harness session.
  Native continues to consume the same canonical `ConversationContextResult`.
- Codex continues to resume its App Server thread; OpenCode and Qwen Code
  continue to reuse their native sessions while recording the canonical AR
  history version synchronized into each session. A restarted adapter hydrates
  its newly created native session from the bounded canonical window.
- Native harness permissions and configured tool exposure remain executable
  capability boundaries. OpenCode/Qwen native approval paths are now
  canonicalized through `ITrustedWorkspaceService` before automatic or manual
  approval; missing, external, reparse-point, and `.git` paths are rejected.
  Destructive OpenCode permission kinds are no longer misclassified as
  non-destructive, and approval UI includes the exact normalized targets.
- Deterministic coverage proves all six requested Native/Codex/OpenCode switch
  directions, Codex -> OpenCode -> Codex delta round-trip, Qwen hydration,
  pre-existing-file modification, pre-existing-file deletion, unrelated work
  preservation, and rejection of an external approval path. The real browser/
  API round-trip created a file through Codex, deleted it through an explicitly
  approved OpenCode capability, preserved unrelated bytes, returned to the
  prior Codex thread, and verified the file remained absent.
- Spark audit retained the useful generic harness registry/Native adapter,
  exact model and workspace checks, Host workspace observation, Host dynamic
  create/delete tools, approval gates, effect verification, event identity,
  cancellation, and Benchmark Lab isolation. The actual disabled capabilities
  in OpenCode/Qwen configuration remain disabled at the capability layer; they
  are no longer duplicated as prompt theater.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
  Release build passed with zero warnings/errors. Focused regressions passed,
  and the full deterministic Playwright suite passed 238/238 with zero skips in
  6 minutes 58 seconds. `git diff --check` passed. No real Ollama, GPU, cloud,
  model download, or real benchmark execution was performed.

## Audit limitations and follow-up

- The current benchmark/result contracts still do not expose the complete
  requested failure taxonomy (`MODEL_FAILURE`, `HARNESS_FAILURE`,
  `HOST_FAILURE`, `CAPABILITY_UNAVAILABLE`, `POLICY_DENIAL`, `USER_DENIAL`).
  Existing typed error codes and stages preserve some distinctions, but a
  benchmark taxonomy redesign remains outside this correction.
- Canonical handoff uses the Host-bounded visible complete-turn window plus the
  authoritative live workspace. Execution reviews remain separately persisted
  Host records; this change does not add a new duplicated transcript or a new
  long-term execution-summary protocol.
