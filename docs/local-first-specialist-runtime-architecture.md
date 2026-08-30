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

Reintroduction requires independent routing evaluation against representative requests, zero user-facing protocol errors, exact closed-catalog behavior, and a deterministic fallback. It must remain optional and outside the stable Chat/Execute path.

## Fine-tuning audit

Decision: **DEFER**.

The current checkout contains no fine-tuning backend, dataset pipeline, trainer, LoRA/adapter store, or fine-tuning UI. The README lists Hugging Face training, fine-tuning, LoRA, dataset preparation, tokenizer management, RAG, and knowledge bases as non-goals or future ideas. Therefore there is no working subsystem to remove, preserve, or decouple in this refactor.

FunctionGemma routing fine-tuning remains a reasonable future standalone workflow because a small router can be trained against the user's exact specialist catalog without coupling training to Execute. That value is **PLAUSIBLE**, not proven product functionality. Any future implementation should keep datasets, training jobs, artifacts, and evaluation independent from chat routing and the Host runtime.

The requested review also found no implemented llama.cpp provider, general run/workflow builder, or knowledge/RAG subsystem in this checkout. Those are not silently removed; they are absent from the current product code.

## ACP evaluation

Decision: **DEFER ADOPTION; keep an external adapter seam**.

The Agent Client Protocol is a credible future interface between Agentic Router's desktop client and an external specialist agent. ACP defines a client/agent JSON-RPC interface, launches agents on demand over stdio, supports concurrent sessions, streams UI updates through notifications, and supports bidirectional permission requests. Its design explicitly reuses MCP types and allows agents to connect directly to configured MCP servers. The current stable wire protocol is version 1 and official libraries exist for Java, Kotlin, Python, Rust, and TypeScript. See the official [ACP architecture](https://agentclientprotocol.com/get-started/architecture) and [protocol repository](https://github.com/agentclientprotocol/agent-client-protocol).

ACP could eventually replace a proprietary external-agent/UI transport. It does not replace model routing, local provider adapters, trusted-workspace enforcement, effect proof, or the internal tool executor. Adopting it now would first require splitting the current in-process specialist loop into a genuine agent subprocess boundary, adding lifecycle/version/capability negotiation, and mapping the existing stream/session contracts. That is useful only when external ACP agents are a real product requirement, so adoption now would increase code and protocol surface rather than simplify it. Current assessment: **PLAUSIBLE** external interface, not a required internal architecture.

## MCP evaluation

Decision: **DEFER CORE REWRITE; consider an opt-in external tool adapter**.

MCP provides standardized tool discovery/calls with JSON Schema inputs and structured or unstructured results. The current standard transports are stdio and Streamable HTTP, and the July 2026 release moved the core to self-describing stateless requests with updated Tier 1 C# support. See the official [MCP tool contract](https://modelcontextprotocol.io/specification/draft/server/tools), [transport overview](https://modelcontextprotocol.io/specification/draft/basic/transports), and [2026-07-28 release](https://blog.modelcontextprotocol.io/posts/2026-07-28/).

MCP could replace custom adapters for optional third-party tools and LAN services. It should not replace the small built-in filesystem/process/Git runtime: those tools already have application-specific workspace confinement, conflict detection, rollback evidence, process policy, session review, and Host-owned completion facts. MCP schemas and annotations are untrusted inputs and would still need to pass the same Host policy. The protocol is also evolving through breaking revisions and extensions. Current assessment: **PLAUSIBLE** for opt-in external capabilities behind the closed Host registry; a wholesale rewrite is not justified.

## Local-first and future topology

Local-only operation remains the baseline. Provider-qualified model references already separate a model identity from the provider dispatch path, so optional cloud specialists do not have to become product dependencies. Extending that reference to configured Ollama or llama.cpp endpoints on another local process or LAN machine is **PLAUSIBLE**. Multi-agent delegation, peer review across GPUs, and multi-machine scheduling remain **SPECULATIVE** and are intentionally not implemented.

## Remaining technical debt and intentional deferrals

- Remove legacy router/action settings fields and inactive runtime role defaults in a future schema version after compatibility readers are no longer required.
- Version the legacy `coordinatorModel` session/JSON field to `specialistModel` in a future public-contract change. The current browser already labels it "Specialist" while retaining the wire name for compatibility.
- Rename `LOCAL_ACTION_PLANNER_V1` to a specialist-runtime contract and version the change once real-model compatibility evidence exists.
- Replace keyword-derived tool scope with explicit user/runtime capability constraints where a denial is actually needed; the current direct path already offers the normal trusted-workspace development tools unless the user explicitly forbids processes.
- Add a configured local/LAN endpoint abstraction before claiming llama.cpp or remote-local specialist support.
- Evaluate any future FunctionGemma weights outside the product path before considering an opt-in integration.
- Add ACP only when Agentic Router must host an external agent process; add MCP only when a concrete external tool integration avoids more custom code than it introduces.

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
- The complete retained Playwright browser/API suite passes: 169 passed, 0 failed, 0 skipped.

Obsolete tests that required resident takeover, live conformance gating, mandatory plans, FunctionGemma supervision, or multi-layer recovery checkpoints were removed rather than preserved as compatibility requirements. Retained browser tests continue to cover ask mode, undo, bounded processes, validation, provider/model management, persistence, and structured Git delivery.

With explicit permission, a real local Ollama `0.33.2` smoke classified a software-architecture request through keywords, selected `qwen3.8:27b-gpu0`, and completed without an error event. A second general-chat fallback returned `OK` from the same exact model. The exact digest `d1f9a27632f9cab927948254285394838a1f0e0f8b7e70e53d633624ed9e9169` failed the separate `coordination-conformance-v3` native-strict probe because it did not produce exactly one `benchmark_edit` call. Chat routing is therefore real-smoke proven for this model, while its Native Execute tool protocol remains not accepted. No cloud provider was invoked.
