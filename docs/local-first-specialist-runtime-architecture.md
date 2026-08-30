# Local-first specialist runtime architecture

## Outcome

Agentic Router now uses the selected specialist as the owner of an Execute turn. The Host remains the deterministic runtime and security boundary, but a resident coordinator, behavioral-conformance benchmark, and model-authored execution plan are no longer prerequisites for ordinary trusted-workspace actions.

The target request flow is:

```text
User
  -> explicit selection or lightweight routing
  -> selected specialist
  -> Host tool runtime when tools are required
  -> trusted workspace / bounded process / structured Git service
  -> authoritative result returned to the same specialist
```

Chat remains a separate, non-agentic path. A conversational request never enters the tool runtime merely because routing was used.

## Previous request flow

The previous Chat path was already appropriately small:

```text
User -> router (unless manually selected) -> configured intent model -> streamed answer
```

Execute was materially more complex:

```text
User
  -> router or manual selection
  -> selected specialist
  -> capability inspection
  -> native-strict / structured conformance gate
  -> direct selected model only when evidence passed
  -> otherwise specialist guidance generation
  -> resident action/coordinator model
  -> LOCAL_ACTION_PLANNER_V1 translation
  -> mandatory model-authored visible plan
  -> semantic plan/action/effect binding
  -> approval policy
  -> deterministic Host validation and execution
  -> effect proof
  -> resident / specialist / FunctionGemma recovery layers
  -> Host terminal response
```

Intent could be lost or delayed at four transformations: specialist prose to structured guidance, guidance to resident actions, action titles to inferred effects, and actions to model-authored plan steps. A specialist that was capable of tool use could also be blocked before trying the real task because cached or benchmark conformance evidence was absent.

## New request flow

### Chat

1. Respect an explicit model selection or classify intent through ordered local keyword rules.
2. Resolve the configured specialist.
3. Stream one answer from that specialist.
4. Do not create an execution session or tool catalog.

### Execute

1. Respect an explicit model selection or classify intent through ordered local keyword rules and resolve its configured specialist.
2. When Auto Model × Harness is enabled, choose the best available ranked harness for that exact model; if it has no usable ranking, choose the best aggregate harness score, then deterministic Native as the final fallback.
3. Validate the active trusted workspace and load bounded project/repository instructions.
4. Start Host-owned session state for cancellation, history, review, undo, limits, and authoritative facts.
5. Select native tool calling when supported, otherwise the existing structured-action transport when available.
6. Offer the selected specialist the Host-owned closed tool catalog directly. Do not offer `create_execution_plan` or `revise_execution_plan` as gates.
7. Validate the proposed canonical tool, arguments, paths, process policy, and remaining limits deterministically.
8. Execute it, independently prove its effect, and return the result to the same specialist.
9. Repeat until the specialist stops or a typed safety/runtime limit blocks progress.
10. Generate the terminal status from Host facts.

## Component decisions

| Component | Decision | Status | Reason |
| --- | --- | --- | --- |
| Keyword router and intent profiles | Retain | PROVEN | Classification is deterministic, local, and covered by browser/API tests; explicit selection bypasses it. |
| Benchmark-ranked harness selection | Retain for Auto | PROVEN | The selected model remains authoritative; exact-model ranking wins, aggregate harness ranking is the fallback, and Native prevents missing evidence from blocking execution. |
| Explicit model selection and conversation lock | Retain | PROVEN | The router is bypassed and the selected technical identity is preserved. |
| Selected-specialist tool loop | Make primary Execute path | PROVEN with fake providers | The same selected model proposes successive tools and receives authoritative results without a resident bridge. Real-model compatibility remains model-specific. |
| `LOCAL_ACTION_PLANNER_V1` | Simplify to a specialist runtime prompt | PROVEN with fake providers | It now describes the selected specialist's direct loop; it no longer translates another model's guidance or requires a plan first. A future rename is desirable. |
| Native/structured conformance benchmark | Retain as diagnostics, remove as live prerequisite | PROVEN for the new fake-provider path | Capability and runtime failures are useful evidence, but a benchmark should not insert or require another model before every task. |
| Resident bridge, preload, eviction, and coordinator takeover | Remove | PROVEN for the deterministic path | The selected specialist is the effective executor and no resident model is loaded, restored, or consulted by Chat/Execute. |
| Model-authored execution plan and semantic plan/action binding | Remove as an ordinary action gate | PROVEN for direct file creation | Session facts, tool results, and effect verification provide the needed runtime authority without plan bureaucracy. Optional UI progress may be rebuilt later from Host actions. |
| Tool alias registry, typed schemas, limits, cancellation, session history, review, undo, effect proof | Retain | PROVEN | These protect deterministic execution or preserve useful product behavior without adding an interpreting model. |
| Trusted-workspace confinement and reparse-point rejection | Retain | PROVEN | They protect the actual local safety boundary. |
| Direct `.git` filesystem access | Reject | PROVEN | Direct `.git` mutation is rejected before execution; structured Git services remain separate. |
| Default approval policy | Change to automatic inside the trusted workspace | PROVEN for bounded file tools | Repeated approval is not a security boundary. Ask mode remains available; guarded Git writes and higher-risk processes retain explicit approval. |
| Provider/model management, Ollama runtime profiles, GPU/device metadata, optional cloud providers, usage, conversations, backup, and Git review | Retain | PROVEN existing functionality | These are useful independent product capabilities and were not rewritten. |

## FunctionGemma status

FunctionGemma is disabled and has no production role. The application does not load it, call `route_to_teacher`, use it for failure review, or expose resident-model controls. Legacy router/action fields remain readable only so existing settings files can migrate without breaking; they are ignored by routing and runtime policy.

## Fine-tuning audit

Decision: **NOT IMPLEMENTED AND NOT ON THE CURRENT ROADMAP**.

The current checkout contains no fine-tuning backend, dataset pipeline, trainer,
LoRA/adapter store, or fine-tuning UI. There is therefore no working subsystem
to remove, preserve, or decouple. Historical research notes about training are
experiments, not product commitments.

The requested review also found no implemented llama.cpp provider, general run/workflow builder, or knowledge/RAG subsystem in this checkout. Those are not silently removed; they are absent from the current product code.

ACP, MCP, llama.cpp/LAN endpoints, multi-agent delegation, peer review across
GPUs, and multi-machine scheduling appear only in historical research/plans.
None is an implemented capability or current roadmap promise.

## Remaining technical debt and intentional deferrals

- Remove legacy router/action settings fields and inactive runtime role defaults in a future schema version after compatibility readers are no longer required.
- Version the legacy `coordinatorModel` session/JSON field to `specialistModel` in a future public-contract change. The current browser already labels it "Specialist" while retaining the wire name for compatibility.
- Rename `LOCAL_ACTION_PLANNER_V1` to a specialist-runtime contract and version the change once real-model compatibility evidence exists.
- Replace keyword-derived tool scope with explicit user/runtime capability constraints where a denial is actually needed; the current direct path already offers the normal trusted-workspace development tools unless the user explicitly forbids processes.

## Validation status

The following are **PROVEN** with the real browser/API path and deterministic fake providers:

- Chat does not enter the execution pipeline.
- Explicit model selection bypasses routing.
- A selected native-tool specialist creates a file directly without resident guidance or a visible-plan gate.
- A selected structured specialist inspects and edits an existing file, then runs the saved build/test profile in the same loop.
- The automatic trusted-workspace policy covers create, read, write, replace, patch, and explicit bounded deletion; ask mode remains available.
- A local specialist creates `README.md` without a cloud request.
- Parent traversal and decoded control-character paths/process arguments are rejected, returned to the same specialist, and safely replanned inside the workspace.
- Direct `.git` metadata mutation is rejected before execution.
- The Release solution builds with zero warnings.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passes.
- The complete retained Playwright browser/API suite passes: 360 passed, 0 failed, 0 skipped on 2026-08-30.

Obsolete tests that required resident takeover, live conformance gating, mandatory plans, FunctionGemma supervision, or multi-layer recovery checkpoints were removed rather than preserved as compatibility requirements. Retained browser tests continue to cover ask mode, undo, bounded processes, validation, provider/model management, persistence, and structured Git delivery.

Earlier explicit real-model evidence showed Chat completing on local Ollama
`qwen3.8:27b-gpu0`, while that digest failed the separate historical
`coordination-conformance-v3` native-strict probe. That evidence is not treated
as acceptance of the current five-harness Execute matrix; the fresh real matrix
is tracked separately in the cleanup audit.
