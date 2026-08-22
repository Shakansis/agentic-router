# AGENTS.md

## 1. Product Mission

Agentic Router is a local-first application that routes conversations and supervised software-development work to a selected **model + harness** combination using local Ollama models or explicitly configured cloud providers.

The product has two user-visible modes:

- **Chat** routes a turn to the selected specialist and streams one continuous answer.
- **Execute** lets the selected model + harness work toward the user's goal inside a trusted workspace while Agentic Router enforces product policy, approvals, security boundaries, validation, recovery, and reviewable evidence.

The objective is to deliver the requested outcome whenever a permitted recovery path exists.

The Host owns product policy, security boundaries, approval semantics, trusted-workspace authority, session state, validation, and execution evidence. Actions may be performed through Host-provided tools or harness-native capabilities. Using a different harness must not silently change Agentic Router policy.

## 2. Instruction and Decision Authority

When instructions conflict, follow this order:

1. the user's current request;
2. this `AGENTS.md`;
3. an approved feature specification in the repository;
4. existing implementation patterns that do not conflict with the items above.

Do not expand scope to solve hypothetical future needs.

Do not invent or change product policy. Choose implementation details autonomously only when they preserve existing behavior. Ask the user before changing:

- security or approval semantics;
- capability availability;
- Host/harness authority;
- trusted-workspace boundaries;
- failure or recovery behavior;
- user-visible product behavior;
- roadmap scope or architectural invariants.

Never resolve an unspecified case by silently choosing a stricter, safer, broader, or more restrictive product behavior.

When several implementation choices are behaviorally equivalent, choose the smallest coherent solution without asking.

### AGENTS.md scope

`AGENTS.md` contains only stable, project-wide invariants that should influence most development tasks.

Do not add feature-specific behavior, exact prompts, UI details, provider quirks, storage formats, endpoint-specific rules, test cases, temporary migrations, or milestone decisions to `AGENTS.md`. Put them in the relevant feature specification, design document, tests, or implementation documentation.

Do not update `AGENTS.md` merely because a feature was added or changed. Update it only when a project-wide invariant or architectural authority changes.

## 3. Technology and Scope

The application uses:

- .NET 10 and ASP.NET Core Web API;
- built-in dependency injection, options, logging, and `HttpClientFactory`;
- Ollama Local plus explicitly configured cloud providers;
- vanilla HTML, CSS, and JavaScript;
- Playwright for .NET with MSTest for browser-driven end-to-end tests;
- validated local settings and persistence;
- trusted workspace profiles and supervised local execution.

The application must work with one GPU, multiple GPUs, or provider-managed device selection. Multi-GPU hardware is optional and must never be required.

Do not introduce a frontend framework, Node.js build pipeline, or dependency when the platform already provides a simple adequate solution.

## 4. Architecture and Authority

Keep responsibilities explicit:

- Controllers translate HTTP input and output only.
- Chat services coordinate routing, streaming, model/harness selection, and conversation context.
- Execution services own Host policy, approvals, validation, recovery, session state, and execution evidence.
- Harness adapters translate between Agentic Router and harness-specific protocols or capabilities.
- Provider adapters own provider communication and provider-specific contracts.
- Settings and workspace stores own validated local configuration.
- The browser renders Host state and sends explicit user decisions; it is not an execution authority.

Keep business logic out of controllers and provider DTOs out of public application contracts.

Model and harness output is untrusted input. The Host must validate every action that crosses a Host-owned policy or security boundary.

Apply validators only to the authority they actually own. Filesystem rules validate filesystem targets; they must not be reused to invent policy for unrelated pathless capabilities.

Preserve context and user-visible continuity when switching compatible model/harness execution paths unless a documented product rule explicitly requires a reset.

## 5. Routing, Models, Harnesses, and Capabilities

Routing, specialist reasoning, harness execution, and Host policy are separate responsibilities.

The selected specialist owns the reasoning and recovery loop for the turn. Do not insert another model between a capable specialist and its runtime unless an explicit routing or recovery decision requires it.

A model + harness pair may use:

- capabilities native to that harness;
- capabilities provided by Agentic Router;
- adapters that safely bridge the two.

Do not remove useful native capabilities merely to make harness capability lists identical.

Do not use an unrestricted Host shell, filesystem escape, or equivalent bypass to compensate for a missing adapter. Missing capability bridges must remain explicit.

Capability availability, routing decisions, fallbacks, retries, and takeovers must be deterministic and visible in activity evidence.

Tool and capability identifiers must use exact canonical names or explicitly reviewed aliases. Never use fuzzy or model-inferred tool-name normalization.

Protocol or transport failures must be typed. After a deterministic failure, change strategy instead of repeating an identical request.

## 6. Execute Mode

Execute is goal-driven and constrained to a user-approved trusted workspace.

A visible plan is optional and must never gate ordinary requested actions.

The normal execution loop is:

1. resolve and validate the active workspace;
2. inspect applicable repository instructions and project metadata;
3. let the selected model + harness work toward the user's objective;
4. validate each Host-governed action against its actual policy and boundary;
5. execute through the appropriate Host or native-harness capability;
6. independently verify required effects when Agentic Router claims an action succeeded;
7. return actionable failures to the active agent for recovery;
8. record bounded activity, changes, validation, recovery, and terminal state;
9. present a reviewable result based on Host facts.

All effective filesystem actions governed by the trusted workspace must remain confined to the canonical trusted root. Reject genuine external escapes, protected paths, ambiguous targets, unsafe reparse-point traversal, stale conflicting writes, and other violations defined by the relevant feature specification.

Absence of a filesystem path is not evidence of an external filesystem path. Do not manufacture boundary failures for capabilities that are not filesystem operations.

Preserve external user changes and surface conflicts instead of overwriting them silently.

A successful tool or capability response is not sufficient evidence of success when Agentic Router can independently verify the required effect. Do not report an operation as completed when its required effect was not observed.

## 7. Approval and Recovery

The user's selected approval mode is authoritative.

- In automatic approval mode, requested, validated, permitted mutations may execute without duplicate confirmation.
- In ask mode, mutations covered by that policy wait for user approval.
- Approval never authorizes crossing hard security or trusted-workspace boundaries.
- Do not invent new approval requirements for an unspecified capability.

A rejected, unavailable, malformed, or forbidden intermediate action is not automatically a failed objective.

Return a typed, actionable reason to the active agent that explains what failed and why. Keep the turn alive while a permitted materially different recovery path remains.

Stop the objective only when:

- the requested goal is genuinely impossible within current capabilities and policy;
- no permitted recovery path remains;
- the user denies required approval;
- the user cancels;
- a non-recoverable infrastructure failure prevents continuation;
- or the configured execution/recovery budget is exhausted.

Recovery must remain bounded and observable. Do not retry indefinitely, repeat a deterministic failure unchanged, hide fallbacks, or silently weaken policy to obtain success.

Security constraints limit execution; they do not authorize the implementation to redefine product behavior.

## 8. Configuration, Persistence, and Privacy

Configuration must be typed, validated, versionable, and atomically saved where practical. Invalid settings must not be partially applied.

Persist only data the user has enabled and only at the minimum fidelity required by the feature.

Never expose or persist secrets, hidden instructions, unrestricted local paths, raw provider payloads, full stack traces, or sensitive tool/process content unless an explicitly approved feature specification requires a safe bounded representation.

Cloud credentials must remain protected and browser-visible state must expose only sanitized or masked information.

Local Ollama inference has no provider token charge. Provider usage, estimates, measurements, diagnostics, and cost projections must distinguish exact, estimated, measured, configured, stale, and unavailable evidence when those distinctions matter.

Do not make hidden background provider calls or silently change providers, models, harnesses, device policy, persistence state, or user configuration.

Feature-specific persistence schemas, context ladders, backup formats, migration rules, provider metadata behavior, web search rules, image rules, and model-profile behavior belong in their respective specifications rather than this file.

## 9. Streaming and UI Contracts

The browser communicates only with the local Agentic Router API and must never call local or cloud model providers directly.

Stream typed events in order and terminate each turn exactly once.

Only response content intended for the visible assistant answer contributes to that answer. Routing, model/harness selection, tool activity, approvals, validation, retries, recovery, diagnostics, and timing belong in activity or review surfaces.

Chat remains the primary interaction surface. Execute must expose enough Host-owned state for the user to understand:

- the active workspace;
- the active model + harness;
- pending approvals;
- meaningful execution progress;
- changed files and review evidence;
- validation results;
- failures, recoveries, conflicts, and terminal state.

Keep keyboard navigation and accessibility intact.

Never use browser-native `alert`, `confirm`, or `prompt` for application workflows.

Exact UI layouts, labels, thresholds, dialog sizes, toast durations, and feature-specific controls belong in their UI specifications and tests.

## 10. Error Handling

Use stable typed errors with enough sanitized context to identify:

- what failed;
- where it failed;
- whether retry or recovery is possible;
- the relevant provider, model, harness, intent, action, or trace identity when applicable.

Preserve the original exception as the logged cause.

Distinguish policy/security rejection, approval rejection, protocol incompatibility, provider failure, cancellation, timeout, stale/conflicting state, validation failure, and exhausted recovery where applicable.

Do not convert a recoverable intermediate action failure into a generic terminal user error.

Any fallback or takeover must be explicit and observable.

## 11. Testing and Validation

The automated suite contains end-to-end tests only.

Use Playwright for .NET with MSTest to exercise the browser and running API together. Tests may fake a provider only at its external boundary; do not mock internal controllers, routing, execution, persistence, harness policy, or browser code.

Before invoking a real local model, GPU workload, or cloud provider for validation, obtain the user's explicit permission when required by the current testing policy. Deterministic fake-provider E2E tests and read-only discovery do not require such permission.

Do not solve slow or unstable tests by weakening assertions, hiding failures, or arbitrarily increasing limits.

Before completing a change, run the applicable repository validation, including formatting, Release build, relevant E2E coverage, and intended-diff inspection. Run real-model or real-provider validation only when authorized and applicable.

Never claim validation that was not executed. Report unavailable validation as a limitation, not as passing evidence.

## 12. Code Quality and Change Discipline

- Keep nullable reference types enabled and the build at zero warnings.
- Use asynchronous APIs and propagate `CancellationToken` through I/O.
- Never block asynchronous work with `.Result`, `.Wait()`, or thread sleeps.
- Prefer cohesive classes, immutable records, explicit result types, and I/O at the edges.
- Avoid static mutable application state.
- Do not add speculative abstractions, dead scaffolding, or unnecessary dependencies.
- Preserve public contracts unless the requested change explicitly versions them.
- Preserve unrelated work and external file changes.
- Do not report success for an operation the Host did not perform or verify.

Before editing, inspect the relevant implementation, repository instructions, specifications, and tests.

Make the smallest coherent vertical change that satisfies the request.

After editing, report the files changed, commands run, validation results, and real limitations.

If implementation uncovers a product-policy ambiguity, stop before choosing the policy and ask the user. Continue autonomous work on behaviorally equivalent implementation details that do not depend on that decision.

## 13. Non-Goals

Do not implement these without an explicit request or approved specification:

- new provider families;
- unrestricted filesystem or operating-system control;
- security-boundary bypasses;
- recursive autonomous delegation systems;
- background schedulers or distributed execution;
- authentication, billing, telemetry platforms, installers, or auto-update;
- automatic model downloads;
- frontend frameworks or JavaScript build pipelines;
- destructive Git history rewriting.

A capability is not a non-goal merely because one harness does not yet expose an adapter for it.

## 14. Definition of Done

A change is complete only when:

- the requested behavior works through the applicable real browser/API path;
- Host policy and trusted-workspace boundaries remain intact;
- model + harness behavior remains consistent with documented Agentic Router semantics;
- recoverable failures remain recoverable;
- user-visible behavior was not silently redefined;
- applicable validation passes;
- the intended diff was reviewed;
- and any unexecuted validation or real limitation is explicitly reported.

Passing tests does not authorize an undocumented product-policy change.
