# PLAN v25 - Host Capability Parity

## Goal

Make every supported ACT harness consume one Host-authoritative capability profile and expose the same executable capabilities wherever its protocol can preserve Host validation, approval, effect proof, and workspace confinement.

## Confirmed problem

- Native execution derives its tool surface from `ExecutionTurnToolPolicy` and executes through `LocalActionService`.
- Codex starts with its own built-in tools and receives only a small Host dynamic-tool subset.
- OpenCode and Qwen configure different native tool sets and translate their own permission events.
- The Spark harness work therefore made capability discovery, deletion, command execution, and approval behavior depend on the selected harness instead of Host policy.

## Constraints

- Preserve the selected specialist and the canonical conversation/project context.
- The Host remains the authority for paths, policy, approval, execution evidence, recovery, and terminal truth.
- Do not enable a native shell or process path unless the adapter can submit the exact operation to Host validation before execution.
- Treat the UI approval mode as authoritative for every mutation, including Git writes and mutating structured commands: `auto` executes an in-scope requested mutation after Host validation, while `ask` waits for approval before any mutation.
- Reject workspace-boundary or policy violations before approval evaluation; approval can never broaden the trusted scope.
- Preserve existing public contracts when a compatibility alias is sufficient.
- Do not invoke a real Ollama or cloud model without explicit permission.

## Ordered plan

1. Define one immutable Host capability profile for an ACT turn, including availability, approval semantics, and protocol-independent tool definitions.
2. Replace the file-only deletion contract with canonical `delete_paths(paths, recursive)`, retaining `delete_files` only as a compatibility alias; validate files and directories, confine every path, and prove every postcondition.
3. Centralize approval semantics after validation: read-only actions execute directly; `auto` executes requested in-scope mutations without a duplicate prompt; `ask` waits before every mutation. Invalid or out-of-workspace actions are denied rather than offered for approval.
4. Project the common profile into Native and Codex Host-owned tool surfaces, including structured process execution and structured Git tools where supported.
5. Audit OpenCode and Qwen native permission/guard protocols; enable only translations that provide enough pre-execution facts for the same Host validation. Report protocol limitations truthfully instead of advertising unusable tools.
6. Preserve context hydration and delta synchronization while removing harness-specific capability drift.
7. Add deterministic browser/API parity coverage for discovery, read/write/edit/delete, directory deletion, approval behavior, process policy, Git exposure, and truthful limitations.
8. Run formatting verification, Release build, full Playwright E2E, `git diff --check`, and inspect the complete intended diff. Keep real-model validation explicitly separate.

## Acceptance evidence

- One Host-derived capability profile is observable in every harness turn.
- `delete_paths` handles explicit files and directories, rejects unsafe scope, and records observed removal before completion.
- Requested in-scope filesystem, Git, and structured-process mutations do not cause a redundant approval under automatic policy.
- The same mutations remain pending until approval under ask policy.
- No adapter can bypass Host path/process/Git policy through a native permission shortcut.
- Capability discovery never claims an operation that the active adapter cannot safely execute.
- A denied native action produces a visible Host correction and leaves the harness turn alive for a materially different safe proposal; only exhausted or irrecoverable conditions terminate the objective.
- Deterministic parity tests pass through the real browser/API path.

## Implemented classification

The common profile is additive. A native implementation satisfies an AR capability only when its adapter preserves the same workspace, approval, scope, and truthful-effect invariants. Native tools that provide more than the common profile remain available unless they conflict with one of those invariants.

| Capability | Native | Codex | OpenCode | Qwen Code |
| --- | --- | --- | --- | --- |
| list/read/search/create/write/edit/patch | `AR_COMMON` Host tools | `AR_COMMON_IMPLEMENTED_BY_NATIVE_HARNESS_TOOL` | `AR_COMMON_IMPLEMENTED_BY_NATIVE_HARNESS_TOOL` | `AR_COMMON_IMPLEMENTED_BY_NATIVE_HARNESS_TOOL` |
| create directory | `AR_COMMON` Host tool | `AR_COMMON_IMPLEMENTED_BY_NATIVE_HARNESS_TOOL` | `MISSING_ADAPTER` | `MISSING_ADAPTER` |
| `delete_paths(paths, recursive)` | `AR_COMMON` Host tool | `AR_COMMON` Host dynamic bridge | `MISSING_ADAPTER` | `MISSING_ADAPTER` |
| structured command execution | `AR_COMMON` Host tool | `AR_COMMON` Host dynamic bridge; sandboxed Codex command support remains a `HARNESS_NATIVE_EXTRA` | raw bash permission lacks sufficient pre-execution boundary facts and remains a `SECURITY_CONFLICT` | built-in shell requires an external Host guard that is not yet connected, so it remains `MISSING_ADAPTER` and disabled |
| structured Git read/write | `AR_COMMON` Host tools | `AR_COMMON` Host dynamic bridge | `MISSING_ADAPTER` | `MISSING_ADAPTER` |
| harness session, reasoning, and native event features | not applicable | `HARNESS_NATIVE_EXTRA` | `HARNESS_NATIVE_EXTRA` | `HARNESS_NATIVE_EXTRA` |
| network/subagent facilities | not in the AR common ACT profile | retained only where separately permitted by Host policy | `SECURITY_CONFLICT` for this adapter configuration | `SECURITY_CONFLICT` for this adapter configuration |

OpenCode and Qwen therefore receive the same Host-derived profile and a truthful discovery projection, but this milestone does not advertise or claim executable delete/process/Git parity for those two adapters. Adding those operations requires a bidirectional Host-tool transport (or, for Qwen shell, a complete external-guard integration) so the Host can validate the exact action before the harness executes it.

## Validation result

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed outside the filesystem sandbox because the Roslyn build host requires a named pipe.
- `dotnet build AgenticRouter.slnx -c Release`: passed with zero warnings and zero errors.
- Full deterministic Playwright E2E: 248 passed, zero failed, zero skipped; providers and harness executables were fake.
- Real Ollama/manual real-UI smoke: not run because explicit permission is required.
