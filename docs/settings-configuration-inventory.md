# Settings configuration audit and status inventory

Current scope: Agentic Router application settings (`ApplicationSettings`) and settings-driven
features reachable from the Settings modal, Workspace/Validation/Cloud integrations, and runtime persistence.

## Status meanings

- **ACTIVE**: directly consumed by runtime behavior, routing, execution, or persisted UI workflows.
- **DERIVED**: computed or reconstructed from validated settings (defaults/corrective migration), not primary user intent.
- **DIAGNOSTIC**: used for observability, health, compatibility checks, summaries, or policy validation evidence.
- **INTERNAL**: metadata, security references, compatibility metadata, or system-managed values.
- **UNUSED**: present in schema/storage but not currently consumed by active code paths.
- **BROKEN**: known broken behavior requiring follow-up.
- **UNKNOWN**: usage is not yet fully mapped in this audit.

## Settings inventory

| Config key | UI / source | Section | Default | Status | Consumer | Runtime effect | Scope | Notes |
|---|---|---|---:|---|---|---|---|
| `schemaVersion` | `/api/settings` JSON; not editable | internal metadata | `1` | INTERNAL | `JsonSettingsStore` migration path | Controls schema validation compatibility and rewrite policy | Storage | Required for migration checks; not user-facing. |
| `ollamaUrl` | `ollama-url` | General | `http://localhost:11434` | ACTIVE | Chat provider dispatch, model discovery | Provider transport endpoint for Ollama | Runtime | Must be absolute HTTP(S); validated on save. |
| `routerModel` | `router-model` | Models & Routing | `configure-model` | ACTIVE | Router model selection | Directly affects chat classification routing | Chat | Must resolve to a discoverable selectable model. |
| `routerGpu` | `router-gpu` | Models & Routing | `default` | ACTIVE | Router dispatch | GPU routing selection for router model | Runtime | `default/auto/ollama:N` with policy validation. |
| `actionModel` | `action-model` | Models & Routing | `functiongemma:270m` | ACTIVE | Resident + coordinator action path | Provider call for action/model selection and resident swap | Execution | Used by resident model manager and execution behavior. |
| `actionGpu` | `action-gpu` | Models & Routing | `default` | ACTIVE | Resident model policy | GPU assignment for action model | Runtime | Required for resident lifecycle planning. |
| `coordinatorModel` | `coordinator-model` | Models & Routing | `configure-model` | ACTIVE | Chat execution coordinator | Coordinator behavior when planning and recoveries run | Execution | Required; fallback fill-ins are applied on load for legacy settings. |
| `coordinatorGpu` | `coordinator-gpu` | Models & Routing | `default` | ACTIVE | Coordinator model dispatch | GPU assignment for coordinator | Runtime | Validated against available devices. |
| `defaultModel` | `default-model` | General | `configure-model` | ACTIVE | Default request model | Default intent and fallback for conversation start | Chat | Used when an intent does not override. |
| `defaultGpu` | `default-gpu` | General | `auto` | ACTIVE | Default request dispatch | GPU assignment for default profile | Runtime | `auto` allowed for default profile only. |
| `trustedWorkspacePath` | Workspace panel | internal | `null` | ACTIVE | Execution sandboxing and workspace services | Defines repository root for workspace-scoped operations | Workspace | Preserved through settings update, not edited in the settings tab. |
| `intentions.*.model` | intention cards | Models & Routing | varies by intent profile | ACTIVE | Routing + execution intent execution | Model per intent, including fallback chaining | Chat/Execution | Supports all named intentions in defaults list. |
| `intentions.*.fallbackModel` | intention cards | Models & Routing | `none` | ACTIVE | Routing + failover | Fallback model for an intent | Chat/Execution | `none` explicitly disallowed in cloud-fallback validator. |
| `intentions.*.gpu` | intention cards | Models & Routing | `default` | ACTIVE | Intent execution dispatch | GPU selection per intent | Runtime | Supports same validation rules as models. |
| `intentions.*.systemPrompt` | intention cards | Models & Routing | `""` | ACTIVE | Specialized system context | Per-intent system prompt shaping | Chat/Execution | User-editable in each intent card. |
| `context.defaultContextTokens` | `default-context-tokens` | Harnesses | `32,768` | ACTIVE | Context fit calculation | Base context budget input | Request shaping | Used in `CreateContextUsage` calculations. |
| `context.providerContextTokens` | `provider-context-tokens` | Harnesses | `40,960` | ACTIVE | Chat request fit + validation | Runtime request context ceiling (non-negotiable) | Runtime/Chat | Must exceed max tool-output budget. |
| `context.reservedResponseTokens` | `reserved-response-tokens` | Harnesses | `4,096` | ACTIVE | Context and UI estimate UI | Reserved output budget in estimated token accounting | Runtime/Chat | Exposed as warning/fit metric in context UI. |
| `context.maxConversationMessages` | `max-conversation-messages` | Harnesses | `40` | ACTIVE | Conversation state sizing | Trims persisted message history and active context window | Chat | Affects chat replay boundary. |
| `runtime.residentModelPolicy` | internal | Harnesses | `adaptive` | INTERNAL | Resident model manager | Controls resident replacement policy | Runtime | Validated and currently constrained to `adaptive`. |
| `runtime.residentModelVerificationIntervalSeconds` | internal | Harnesses | `30` | DIAGNOSTIC | Runtime monitor | Periodic resident model validation cadence | Runtime | Not exposed as user-editable control. |
| `runtime.runtimeStatusIdleRefreshSeconds` | internal | Harnesses | `5` | DIAGNOSTIC | Runtime status widget timer | Poll frequency when idle | UI/Runtime | Controls status UX refresh pace. |
| `runtime.runtimeStatusActiveRefreshSeconds` | internal | Harnesses | `2` | DIAGNOSTIC | Runtime status widget timer | Poll frequency during active load | UI/Runtime | Controls status UX refresh pace. |
| `runtime.generationTimeoutSeconds` | `generation-timeout-seconds` | Harnesses | `300` | ACTIVE | Chat request execution | Hard timeout passed to providers | Chat/Execution | Bound by validator `[1..1800]`. |
| `ollamaRuntime.profileSchemaVersion` | `/api/settings` | Harnesses | `1` | INTERNAL | Legacy compatibility | Migration marker for Ollama runtime profile docs | Storage | Not editable in UI. |
| `ollamaRuntime.contextEscalationLadder` | `/api/settings` | Harnesses | `4,096..40,960` | ACTIVE | Coordinator/provisioning policy | Deterministic candidate context ladder | Runtime | Used when calculating exact context fits. |
| `ollamaRuntime.roleDefaults.*` | model runtime editor | Harnesses | role-specific | ACTIVE | Ollama request shaping | Per-role min/target/max context and keep-alive/output caps | Runtime | Includes roles `router`, `residentCoordinator`, `specialist`, `primary`, `fallback`, `benchmark`, `modelTest`, `webSearchSynthesis`, `visionRequest`. |
| `ollamaRuntime.modelOverrides[*]` | model runtime editor | Harnesses | `[]` | ACTIVE | runtime profile API | Per-model exact overrides for role profile and limits | Runtime | Enforced with exact-capability validation before save. |
| `ollamaRuntime.memory.targetMaximumGpuUsagePercent` | runtime memory controls | Harnesses | `90` | ACTIVE | Memory analyzer/selection | GPU memory guardrail | Runtime/Execution | Influences profile analysis and warning behavior. |
| `ollamaRuntime.memory.minimumFreeVramBytes` | runtime memory controls | Harnesses | `2,147,483,648` | ACTIVE | Memory analyzer | Minimum free VRAM threshold | Runtime | Passed to profile runtime logic and diagnostics. |
| `ollamaRuntime.memory.minimumFreeSystemRamBytes` | runtime memory controls | Harnesses | `4,294,967,296` | ACTIVE | Memory analyzer | Minimum free RAM threshold | Runtime | Runtime analyzer warning signal. |
| `ollamaRuntime.memory.allowCpuOffload` | runtime memory controls | Harnesses | `true` | ACTIVE | Runtime profile behavior | CPU offload policy toggle | Runtime | Exposed in settings memory section. |
| `ollamaRuntime.memory.preferFullGpuForActivePrimary` | runtime memory controls | Harnesses | `true` | ACTIVE | Runtime profile behavior | Selection preference for full-GPU primary models | Runtime | Active in profile recommendation logic. |
| `ollamaRuntime.memory.devices.*.targetMaximumUsagePercent` | runtime memory controls | Harnesses | `90` | ACTIVE | Device override memory policy | Per-device memory cap override | Runtime | Device-specific override map keyed by profiler-provided IDs. |
| `ollamaRuntime.memory.devices.*.minimumFreeVramBytes` | runtime memory controls | Harnesses | `2,147,483,648` | ACTIVE | Device override policy | Per-device memory minimum floor | Runtime | Device-specific override map. |
| `execution.directCoordinatorPlanningFailuresBeforeHandoff` | advanced execution save payload | Execution | `5` | ACTIVE | Coordinator strategy switch policy | Escape policy before specialist handoff | Execution | Read/write via settings save/update path. |
| `execution.residentCoordinatorPlanningFailuresBeforeFailure` | runtime settings payload | Execution | `5` | ACTIVE | Resident fallback behavior | Escalation threshold before execution hard-stop | Execution | Read/write via settings save/update path. |
| `execution.maxCoordinatorHandoffsPerTurn` | execution payload | Execution | `1` | ACTIVE | Execution policy | Limits coordinator transfers in a single turn | Execution | Enforcement point in execution policy. |
| `execution.maxToolCallsPerTurn` | execution payload | Execution | `20` | ACTIVE | Tool execution control | Hard cap on tool-call attempts per turn | Execution | Also reflected in backup summary and diagnostics. |
| `execution.maxConsecutiveToolFailures` | execution payload | Execution | `5` | ACTIVE | Recovery policy | Constrains repeated tool failure tolerance | Execution | Prevents runaway recovery loops. |
| `execution.maxRecoveryAttemptsPerTurn` | execution payload | Execution | `10` | ACTIVE | Recovery policy | Main recovery attempt cap per turn | Execution | Mirrors existing recovery UX expectations. |
| `execution.maxTrackedFilesPerSession` | execution payload | Execution | `50` | ACTIVE | Execution evidence lifecycle | Limits tracked files for plan/session history | Execution | Operational guardrail. |
| `execution.maxRollbackBytesPerFile` | execution payload | Execution | `1,048,576` | ACTIVE | Undo policy | Per-file rollback byte limit | Execution | Used by undo and review safety checks. |
| `execution.maxRollbackBytesPerSession` | execution payload | Execution | `10,485,760` | ACTIVE | Undo policy | Session rollback byte aggregate cap | Execution | Used by review/undo safety checks. |
| `projectAwareness.maxProjectMarkers` | internal runtime payload | Workspace/Execution | `100` | ACTIVE | Project awareness | Controls marker discovery cap | Workspace/Execution | Limits workspace instruction extraction. |
| `projectAwareness.maxInstructionBytes` | internal runtime payload | Workspace/Execution | `131,072` | ACTIVE | Project awareness | Caps marker byte budget in prompts | Workspace/Execution | Prevents oversized prompt injection. |
| `projectAwareness.maxPlanSteps` | internal runtime payload | Workspace/Execution | `8` | ACTIVE | Planned execution envelope | Limits max steps in execution plan | Execution | Used by execution planning. |
| `projectAwareness.maxPlanRevisions` | internal runtime payload | Workspace/Execution | `3` | ACTIVE | Plan mutation policy | Limits revisions per plan | Execution | Enforced in execution planning. |
| `validationProfile` | workspace/validation profile | Internal workspace contract | `{}` | ACTIVE | LocalActionPolicy and validation service | Active validation profile for local tool calls | Execution | Persisted separately in workspace context and carried back into settings payload. |
| `validationProfile.name` | validation profile | internal | `""` | DERIVED | UI+workspace profiles | Name of active profile for execution context | Execution | Reintroduced from active workspace profile. |
| `validationProfile.steps[]` | validation profile panel | internal | `[]` | ACTIVE | Validation execution | Structured validation command set | Execution | Saved to/from workspace profile service. |
| `sessionHistory.maxSessionsPerWorkspace` | summaries/restore behavior | Workspace | `50` | ACTIVE | Session persistence policy | Caps sessions retained per workspace | Storage | Enforced in persistent session service. |
| `sessionHistory.maxSessionBytes` | summaries/restore behavior | Workspace | `5,242,880` | ACTIVE | Session storage policy | Caps per-session persisted bytes | Storage | Active in restore/cleanup behavior. |
| `sessionHistory.maxStoredProcessOutputBytesPerTurn` | execution summary | Workspace | `65,536` | ACTIVE | Session audit policy | Limits persisted process output | Storage/Execution | Prevents runaway growth and sensitive payload retention. |
| `sessionHistory.maxStoredDiffBytesPerTurn` | execution summary | Workspace | `262,144` | ACTIVE | Diff persistence policy | Limits per-turn git diff persist in history | Execution/Storage | Used in auditability and review output. |
| `gitDelivery.enabled` | execution git panel | Execution | `true` | ACTIVE | Git delivery availability | Master switch for delivery actions | Execution/Git | Controls all git-delivery operations. |
| `gitDelivery.requireValidationBeforeCommit` | execution git panel | Execution | `true` | ACTIVE | Git safety policy | Enforces validation before commit in delivery flow | Execution | Explicitly used by GitDeliveryService. |
| `gitDelivery.allowExplicitCommitWithoutValidation` | execution git panel | Execution | `true` | ACTIVE | Git safety override | Explicit commit override behavior | Execution | Used to permit manual override in controlled flow. |
| `gitDelivery.maxDiffBytesPerFile` | execution summary | Execution | `262,144` | ACTIVE | Delivery safety policy | Diff size validation during review/undo | Execution/Git | Also shown in settings advanced summary. |
| `gitDelivery.maxLogEntries` | execution summary | Execution | `50` | ACTIVE | Delivery audit policy | Max change-log entries to keep | Execution | Read by Git delivery service. |
| `usage.retentionDays` | usage summary controls | Usage | `90` | ACTIVE | Usage retention policy | Controls usage ledger retention | Usage | Affects background retention checks and purge. |
| `usage.maxEventBytes` | usage summary controls | Usage | `16,384` | ACTIVE | Usage recorder policy | Maximum event payload size per usage record | Usage | Enforced by ledger validator. |
| `usage.selectedWindow` | usage summary controls | Usage | `rolling-hour` | ACTIVE | Usage rendering | Default usage aggregation window | Usage | Consumer by UsageController and dashboard. |
| `usage.pinnedWindows[]` | usage summary controls | Usage | standard list | ACTIVE | Usage panel widgets | Secondary pinned usage windows | Usage | Rendered in usage panel and API filters. |
| `usage.providerShortWindowMinutes` | usage summary controls | Usage | `300` | ACTIVE | Provider reconciliation | Short reconciliation window | Usage | Used in provider usage aggregation. |
| `usage.providerLongWindowMinutes` | usage summary controls | Usage | `10_080` | ACTIVE | Provider reconciliation | Long reconciliation window | Usage | Used in provider usage aggregation. |
| `usage.customRollingWindowMinutes` | usage summary controls | Usage | `1_440` | ACTIVE | Custom view rendering | Custom usage window size | Usage | Used in UI and usage query filters. |
| `usage.comparisonProvider` | usage compare selector | Usage | `google-ai-studio` | ACTIVE | Usage dashboard | Comparison baseline provider id | Usage | Used in compare chart and summary UI. |
| `usage.comparisonModel` | usage compare selector | Usage | `gemini-3.5-flash-lite` | ACTIVE | Usage dashboard | Comparison baseline model id | Usage | Used in projected cost views. |
| `usage.ollamaPlanReference` | usage plan selector | Usage | `Free` | ACTIVE | Usage dashboard | Local plan label for projected billing | Usage | Used in projected cost labels. |
| `usage.alertThresholds` | usage alerts | Usage | `[70,85,95]` | ACTIVE | Usage summary | Threshold alerts for budget/usage cards | Usage | Input parsed as comma-separated values, then validated. |
| `incidents.enabled` | internal | Diagnostics | `true` | DIAGNOSTIC | Incident journal | Enables/guards incident persistence | Observability | Runtime-only safety/audit subsystem. |
| `incidents.retentionDays` | internal | Diagnostics | `14` | DIAGNOSTIC | Incident cleanup | Retains journal entries | Observability | Cleanup policy for incident storage. |
| `incidents.maximumFileBytes` | internal | Diagnostics | `8,388,608` | DIAGNOSTIC | Incident persistence | Maximum bytes per incident record file | Observability | Enforced by incident storage service. |
| `incidents.maximumTotalBytes` | internal | Diagnostics | `67,108,864` | DIAGNOSTIC | Incident persistence | Total journal size cap | Observability | Prevents unbounded telemetry growth. |
| `incidents.maximumEventsPerTrace` | internal | Diagnostics | `500` | DIAGNOSTIC | Incident serialization | Per-trace event cap | Observability | Stability and memory guardrail. |
| `incidents.browserMaximumEvents` | internal | Diagnostics | `200` | DIAGNOSTIC | Browser telemetry redaction | UI-level incident event cap | Observability/UI | Used for surfaced browser summaries. |
| `incidents.browserMaximumBytes` | internal | Diagnostics | `262,144` | DIAGNOSTIC | Browser telemetry redaction | Browser-side incident byte cap | Observability/UI | Hard cap for surfaced output. |
| `cloudProviders.groq.enabled` | cloud provider card | Providers | `false` | ACTIVE | Provider selection dispatch | Enablement + auth/capability tests | Provider dispatch | Persisted in cloud provider contracts. |
| `cloudProviders.groq.expectedBillingMode` | providers card | Providers | `unknown` | ACTIVE | Usage projection | Billing projection assumptions | Usage/Provider | Defaulted but editable in provider card. |
| `cloudProviders.groq.modelQuotas` | providers card | Providers | `{}` | ACTIVE | Rate-limit guardrails | Short/long window quotas | Usage | Mapped through provider quota UI. |
| `cloudProviders.googleAiStudio.*` | cloud provider card | Providers | same as above | ACTIVE | Provider selection dispatch | `enabled/secret/billing/quotas` | Provider dispatch | Same behavior as Groq profile. |
| `cloudProviders.cerebras.*` | cloud provider card | Providers | same as above | ACTIVE | Provider selection dispatch | `enabled/secret/billing/quotas` | Provider dispatch | Same behavior as Groq profile. |
| `webSearch.ollamaEnabled` | providers card | Providers | `false` | ACTIVE | Capability/dispatch | Enablement switch for Ollama web search | Provider dispatch | Controls whether native web search is surfaced. |
| `webSearch.ollamaSecretReference` | providers card | Providers | `null` | INTERNAL | Secret provider | DPAPI-bound credential reference | Security | Never returns plaintext to clients. |
| `webSearch.maxResults` | providers card | Providers | `5` | ACTIVE | Web search service | Caps tool result count | Provider dispatch | Passed to web-search request. |
| `webSearch.timeoutSeconds` | providers card | Providers | `15` | ACTIVE | Web search service | Search request timeout | Provider dispatch | Bounded in validator. |
| `modelOrganization.maximumProfiles` | model organization panel | Models | `20` | ACTIVE | Persistence/UX policy | Cap on saved model profile count | Model catalog/UI | Enforced in model organization service. |

## Backward compatibility notes

- When `coordinatorModel` is absent in saved settings, it is hydrated from `routerModel`.
- When `actionModel` is absent, it is hydrated to `functiongemma:270m`.
- If legacy context defaults are detected, the store upgrades to current context defaults.
- If execution recovery fields are missing, legacy fields are inferred and rewritten.
- After read, if required top-level sections or critical validation fields are missing, the store rewrites `settings.json` with validated merged values.
- Legacy intention entries missing `fallbackModel` are rewritten to preserve compatibility and keep validation stable.
