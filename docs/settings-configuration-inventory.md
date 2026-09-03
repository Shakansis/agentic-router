# Settings configuration audit and status inventory

Current as of 2026-09-01. This inventory describes the version-1
`ApplicationSettings` contract, the Settings UI, portable YAML, and the runtime
consumers that exist in the current checkout.

## Status meanings

- **ACTIVE**: changes current routing, execution, provider, persistence, or UI behavior.
- **INTERNAL**: system-managed policy or metadata; validated and consumed, but not primary user intent.
- **COMPATIBILITY**: retained so existing JSON, YAML, runtime-profile, model-profile, or session data remains readable. It is not a current routing authority.

No setting is classified as broken or of unknown ownership in this audit. Some
compatibility names are misleading, but removing or renaming them requires a
versioned persisted-contract migration.

## Routing and model identities

| Config key | Status | Current behavior |
| --- | --- | --- |
| `ollamaUrl` | ACTIVE | Ollama transport and model-discovery endpoint. Must be an absolute HTTP(S) URL. |
| `defaultModel`, `defaultGpu` | ACTIVE | Default route when an intent does not select another model; `auto` device selection remains provider-managed. |
| `intentions.*.{model,fallbackModel,gpu,systemPrompt}` | ACTIVE | Deterministic intent profiles. An explicit request selection still wins. Cloud primaries require an exact eligible local fallback. |
| `coordinatorModel`, `coordinatorGpu` | COMPATIBILITY | Version-1 wire names retained by JSON/YAML, saved model profiles, runtime-profile grouping, and session evidence. The browser describes them as a legacy specialist-profile identity. They do not override the model + harness selected for the current Execute request. |
| `routerModel`, `routerGpu` | COMPATIBILITY | Round-tripped by version-1 JSON/YAML and old model profiles. Intent classification is local keyword logic; no router-model inference runs. |
| `actionModel`, `actionGpu` | COMPATIBILITY | Round-tripped for older settings/YAML. No resident model is preloaded, called, evicted, or used for takeover. |

When `coordinatorModel` is absent, `JsonSettingsStore` hydrates it from the old
`routerModel`; when `actionModel` is absent, it writes `none`. Those are read
migrations, not active routing behavior.

## Context and runtime

| Config group | Status | Current behavior |
| --- | --- | --- |
| `context.*` | ACTIVE | Default/provider context ceilings, reserved response budget, and bounded conversation history. `execution.maxToolOutputTokens` must remain below the provider context ceiling. |
| `runtime.generationTimeoutSeconds` | ACTIVE | Continuous-inactivity threshold (`T`), validated from 1 to 1,800 seconds. Meaningful Host-observed progress, including a completed tool action, restarts the window and clears the visible warning. The Host emits a stronger warning after `2T` without progress. Direct and ordinary Supervisor runs remain warning-only. In Autonomous, `2T` interrupts the active turn and permits one materially different retry only when no governed action is unresolved; repeated or unsafe replay blocks. Transient role-status updates use `min(30s, T/3)` and never reset inactivity. The internal 12-hour orphan ceiling is also based only on continuous inactivity. |
| `runtime.runtimeStatusIdleRefreshSeconds`, `runtime.runtimeStatusActiveRefreshSeconds` | ACTIVE | Browser runtime-status polling cadence. |
| `runtime.residentModelPolicy`, `runtime.residentModelVerificationIntervalSeconds` | COMPATIBILITY | Validated and portable so older configuration remains readable. There is no resident-model manager or verification loop in the current runtime. |
| `ollamaRuntime.profileSchemaVersion` | INTERNAL | Version marker for runtime-profile migrations; current default schema is 2. |
| `ollamaRuntime.contextEscalationLadder` | ACTIVE | Discrete candidates used to fit a request without exceeding provider/model ceilings. |
| `ollamaRuntime.roleDefaults.*` | ACTIVE with compatibility entries | Active roles are `specialist`, `primary`, `fallback`, `benchmark`, `modelTest`, `webSearchSynthesis`, and `visionRequest`. `router` and `residentCoordinator` remain readable defaults only. |
| `ollamaRuntime.modelOverrides[*]` | ACTIVE | Exact local model/digest role overrides. |
| `ollamaRuntime.memory.*` | ACTIVE | Global and per-device headroom, CPU-offload permission, and full-GPU preference used by profile analysis/request shaping. |

## Execute, workspace, and persistence

| Config group | Status | Current behavior |
| --- | --- | --- |
| `trustedWorkspacePath` | COMPATIBILITY | Preserved in the global payload for old clients. Active authority comes from the selected workspace profile. |
| `execution.maxToolCallsPerTurn`, `maxConsecutiveToolFailures`, `maxRecoveryAttemptsPerTurn` | ACTIVE | Bounded specialist tool/recovery loop. |
| `execution.maxTrackedFilesPerSession`, `maxRollbackBytesPerFile`, `maxRollbackBytesPerSession` | ACTIVE | Review and rollback evidence limits. |
| `execution.maxSearchFiles`, `maxSearchMatches`, `maxToolOutputTokens` | ACTIVE | Bounded Host search/tool-output limits. |
| `execution.maxDirectPlanSteps` | ACTIVE | Maximum accepted structured steps for Auto to remain in direct Execute. Above this limit, the Host starts or takes over with durable supervision; default `5`, configurable in Settings > General. |
| `execution.phaseEffort.{plan,work,verify,complete,recovery}` | ACTIVE | Host-owned effort target for supervised phases. Values are limited to `low`, `medium`, or `high`; defaults are `high`, `medium`, `medium`, `low`, and `high`. Adapters translate the target when their protocol supports it and report prompt-guided fallback otherwise. |
| `execution.directCoordinatorPlanningFailuresBeforeHandoff` | ACTIVE under a legacy name | Bounds same-route planning/protocol recovery attempts. It no longer hands work to a resident or a different model. |
| `execution.residentCoordinatorPlanningFailuresBeforeFailure`, `maxCoordinatorHandoffsPerTurn` | COMPATIBILITY | Validated and round-tripped by JSON/YAML; no current resident handoff consumes them. |
| `projectAwareness.maxProjectMarkers`, `maxInstructionBytes` | ACTIVE | Bounds repository-instruction discovery. |
| `projectAwareness.maxPlanSteps`, `maxPlanRevisions`, `planLimitsSchemaVersion` | ACTIVE | Bounds optional/recovered plans and `/supervisor` work queues; ordinary Execute actions do not require a plan. |
| `validationProfile` | ACTIVE | Workspace validation profile used by Host validation actions. |
| `sessionHistory.*` | ACTIVE | Conversation/evidence retention and per-turn process/diff storage limits. |
| `gitDelivery.*` | ACTIVE | Structured Git availability, validation policy, explicit override, diff, and log bounds. |

Execute strategy is selected per composer request rather than persisted as a global
setting. Auto remains the default; Direct, Supervisor, and Autonomous are explicit
overrides. Autonomous delegates user-approvable decisions for that run only and does
not weaken any hard Host boundary.

## Providers, search, usage, and diagnostics

| Config group | Status | Current behavior |
| --- | --- | --- |
| `cloudProviders.{groq,googleAiStudio,cerebras}` | ACTIVE | Explicit enablement, opaque secret reference, billing label, and optional per-model quota windows. Secrets never return to the browser. |
| `webSearch.*` | ACTIVE | Optional Ollama Web Search credential, result cap, and timeout. Route capability determines whether Web is shown; availability alone never starts a search. |
| `knowledgeProviders.anythingLlm.*`, `knowledgeProviders.maxContextCharacters` | ACTIVE | Protected AnythingLLM connection plus bounded retrieval limits. Project enablement and selected libraries remain workspace-local under `workspaces[].knowledge`; configuration alone never activates retrieval. |
| `usage.*` | ACTIVE | Local ledger retention, windows, comparison model, display plan reference, provider reconciliation windows, and alert thresholds. These are labels/estimates where exact provider evidence is unavailable. |
| `incidents.*` | ACTIVE/INTERNAL | Opt-in bounded sanitized incident journal, retention/size/event limits, and browser lookup limits. |
| `modelOrganization.maximumProfiles` | ACTIVE | Maximum saved model configuration profiles. |
| `onboarding.showBeforeNewConversation` | ACTIVE | Controls whether Local resources opens before a new conversation. |
| `schemaVersion` | INTERNAL | Must be `1`; incompatible persisted contracts require an explicit migration. |

## Known migration decisions

These items are intentionally retained, not forgotten implementations:

1. remove `router*`, `action*`, resident runtime settings, and inactive runtime
   role defaults in a versioned settings/YAML/model-profile migration;
2. rename `coordinatorModel`/`coordinatorGpu` and session evidence to
   `specialistModel`/`specialistGpu` without breaking stored conversations;
3. rename the remaining planning/recovery fields whose names describe the old
   resident handoff, preserving their current bounded same-route semantics where
   still used.

Until those decisions are approved, compatibility fields must be preserved but
must not be documented as current product capabilities.
