# PLAN v2: Native tool-calling contract for Execute

## Goal

Align the direct specialist Execute loop with the native function/tool-calling contract already used by OpenAI-style APIs, Ollama, and Qwen Code. Implement the provider/model-neutral core first, then implement the Qwen Code tooling protocol as the first thin profile over that core. Fix unnecessary process proposals and tool-call loops without adding another planner, resident intermediary, generic shell, or MCP layer.

The fork-game acceptance goal resumes only after the common core and Qwen profile pass deterministic validation. A specific real-model run still requires confirmation immediately before execution.

## Implementation status (2026-08-11)

- Common transport-neutral tool definitions, calls, results, completion proposals, and Host-owned completion checks: implemented.
- Tool availability separated from necessity; false process-request language and unconditional tool-call corrections: removed.
- Qwen Code over Ollama native tools profile with one-call sequencing, bounded JSON tool results, and completion-capable corrections: implemented.
- Deterministic browser/API validation: `dotnet format --verify-no-changes`, Release build with zero warnings, focused Qwen/core matrix 10/10, the authoritative exact fork-game E2E through `qwen-code-ollama@1`, full fake-provider E2E 177/177, and `git diff --check`: passed.
- Real Ollama Qwen matrix and resumed fork-game goal: pending the required immediate user permission; no real inference has been run for this plan yet.
- Other model-family tooling profiles: intentionally not started and remain gated on successful real Qwen/fork-game evidence.

### Real-run preflight (read-only)

- Exact installed target: `qwen3-coder:30b`, digest `06c1097efce0431c2045fe7b2e5108366e43bee1b4603a7aded8f21689e90bca`, Ollama capabilities `completion,tools`, declared context maximum 262,144 tokens.
- The current application data points at the pre-existing dirty `YpIaiYo` workspace. The acceptance run must not activate, mutate, clean, or reuse it as an output workspace.
- After permission, create one isolated data root and six isolated trusted workspaces: five fresh matrix cases plus one fork-game case. Copy the existing `YpIaiYo/fireworks` directory read-only into only the fork-game input workspace.
- Launch the rebuilt API from the `AgenticRouter.Api` content root, select the exact Qwen model explicitly, use the real browser/API Execute path, and keep cloud providers inactive.
- Verify resident state before the first request. The read-only preflight observed `functiongemma:270m` resident at 32,768 context; startup must either normalize it to the configured Host-owned context or report the mismatch before inference.
- Inspect every resulting changed file and validate the matrix stop conditions and fork-game invariants from Host/workspace facts. Report matrix, fork-game, process use, file review, and real-model outcome separately.

## Decision

There is no single formal industry standard named "Agent Runtime Contract." Adopt the interoperable common denominator instead:

```text
Host sends objective, relevant messages, and concrete tools with JSON Schema
  -> specialist returns a tool call when another action is necessary
  -> Host validates name, arguments, policy, approval, and limits
  -> Host executes and independently proves the effect
  -> Host returns a result correlated to that tool call
  -> the same specialist chooses another tool call or a final response
  -> Host derives terminal truth from verified session facts
```

Automatic tool choice is the ordinary mode. Tool availability means only that a capability may be proposed. It never means the user requested that capability or that the specialist must use it.

## Normative contract

### Tool catalog

- Expose concrete, stable tool names and concise descriptions that say what each tool does and when it is appropriate.
- Use a conservative JSON Schema subset with an object root, typed properties, every required field listed, and `additionalProperties: false`.
- Keep filesystem, process, validation, and Git operations as dedicated structured tools. Do not add `run_command(command)`, a shell interpreter, command chaining, or free-form command text.
- Keep the Host-owned canonical name and alias registry. Provider DTOs and wire details remain inside provider adapters.
- Offer only tools valid for the current phase, trusted workspace, configured capabilities, and policy. A shorter catalog reduces ambiguity, but catalog membership is not an instruction to call a tool.

### Specialist choice

- A valid native tool call is an untrusted proposal for another action.
- A response without a tool call is a completion or blocked-result proposal, not a protocol failure.
- Ordinary and corrective turns must permit either outcome. Do not emulate `tool_choice: required` with prose such as "return exactly one tool call" when completion may already be correct.
- Process execution is proposed only when it materially fulfills or validates the user's request. Static HTML, Markdown, text, JSON, configuration, and documentation are not launched merely because they were created.
- Prefer dedicated file and validation tools over a process. Keep `run_process` structured as `executable` plus bounded `arguments`; improve its description before considering a versioned rename.

### Host authority

- Resolve aliases, validate the canonical tool, validate every argument and path, apply process/Git policy, obtain required approval, enforce budgets, execute, and prove the required effect independently.
- Correlate every result to the originating call and return bounded authoritative facts to the same specialist.
- A rejected proposal remains unexecuted. Return its typed reason and the current Host facts; allow a materially different call or a final response when the objective is already satisfied or no safe action remains.
- Accept a completion proposal only when required Host effects are satisfied. If a known required effect is missing, return one bounded correction naming the missing fact while keeping automatic choice semantics. Do not force another call when the specialist can correctly report a blocker.
- Generate the visible terminal state from Host facts. Model prose cannot convert missing effects into success.

### Separate concepts

Keep these independent in code, activity, and tests:

| Concept | Meaning |
| --- | --- |
| Tool availability | The Host can safely consider this capability in the current phase. |
| Tool selection | The specialist proposed this capability as necessary. |
| User request/constraint | The user explicitly requested, reserved, or prohibited an effect. |
| Host policy | The proposed call is allowed after deterministic validation. |
| User approval | A boundary-crossing allowed call may now execute. |
| Completion | The specialist proposed no further call; the Host evaluates verified facts. |

## Defects confirmed in the starting checkout and addressed here

1. `ExecutionTurnToolPolicy` currently sets `processRequested = !processDenied`, so absence of a recognized prohibition becomes a positive request.
2. The resulting prompt says process execution was explicitly requested even when it was not.
3. General correction text still requires exactly one available native tool call and forbids prose, preventing a correct completion after rejected over-execution.
4. File-only completion currently requires every changed file to be reread after its latest mutation, even when the mutation's effect is already Host-proven and no objective-specific review remains.
5. Request scope relies on enumerated phrases such as manual-test and no-Node variants. Those phrases may remain a conservative temporary denial backstop, but they must not manufacture positive intent or become the acceptance mechanism.

## Ordered implementation

1. Add deterministic failing E2E coverage for the completion/process matrix below before changing production behavior.
2. Refactor turn scope so process availability, process selection, explicit user request, manual-validation reservation, and Host policy are distinct facts. Remove the `!processDenied => explicitly requested` inference and its false prompt text.
3. Make tool descriptions and the specialist system prompt state the normative contract once: tools must materially advance the objective; availability is not necessity; completing without another call is valid.
4. Make all recoverable retries completion-capable. Replace model-facing sentinel language and unconditional "exactly one tool call" corrections with bounded typed Host facts and automatic tool choice.
5. Replace the universal post-mutation reread gate with objective-related evidence. Preserve write-effect proof, stale/conflict checks, requested validation, integration/reference checks, and targeted inspection where an unverified constraint remains.
6. Keep one direct specialist loop and the smallest canonical transport-neutral records needed by provider adapters and tooling profiles. Core records and Host semantics must not mention Qwen or infer authority from model names.
7. Implement the first thin tooling profile for Qwen coding models over Ollama native `/api/chat` tools. Let Ollama own the raw chat template. The profile may adapt model-facing instructions, one-call sequencing, definition/result formatting, completion normalization, and typed protocol recovery; it may not vary Host tools, policy, approval, effect proof, budgets, or terminal truth.
8. Resolve the Qwen profile from exact provider/model identity plus observed capability/conformance facts. Do not fuzzy-match a model family or route ordinary Execute work through another model. Keep the common profile as the default for models without an implemented specialization.
9. Preserve the structured `run_process` public behavior and safe executable allowlist. Do not add a generic shell; any future rename such as `run_validation_process` requires a separate compatibility decision.
10. Run formatter verification, Release build, focused E2E, full E2E, `git diff --check`, and complete intended-diff inspection.
11. Report fake-provider validation separately. Ask for explicit permission before the five fresh-session cases and the resumed fork-game goal are run against the real Ollama Qwen specialist, then inspect every resulting file before reporting acceptance.

## Acceptance matrix

Run each case in a fresh session and isolated trusted workspace:

| Prompt | Expected specialist path |
| --- | --- |
| `Create hello.txt containing "hello".` | create/write -> final response; no process |
| `Create README.md describing this project.` | inspect as needed -> create/write -> final response; no process |
| `Create index.html with a Hello World page.` | create/write -> final response; no launch/server/process |
| `Create hello.py that prints "hello".` | create/write -> bounded allowed validation process when materially useful -> final response |
| `Create hello.py that prints "hello". Do not run it.` | create/write -> final response; no process |

Also cover:

- an unavailable or malformed process proposal after verified file creation can recover by completing without another tool;
- a mutation objective with no verified mutation remains blocked;
- a real cross-file reference or explicit validation requirement still triggers targeted inspection/validation;
- a rejected process call is not retried through another tool or shell indirection;
- activity distinguishes offered, proposed, rejected, executed, effect-verified, and terminal facts.

## Non-goals

- Do not resume the fork-game goal until the common core and Qwen profile pass deterministic validation. Do not run Ollama/cloud inference without the required specific permission.
- Do not change Router or FunctionGemma responsibilities.
- Do not add a finish tool, a second planner, resident supervision, recursive delegation, MCP, or ACP.
- Do not begin GPT-OSS/Harmony, Gemma, Llama, or other tooling profiles before the Qwen profile succeeds on the resumed real goal.
- Do not weaken trusted-workspace confinement, reparse-point rejection, process/Git policy, approval, effect proof, audit, cancellation, rollback, or recovery budgets.
- Do not add production behavior keyed to TXT, Markdown, HTML, Python, fork-game, fireworks, or other fixture wording/extensions. The matrix is test evidence for generic semantics.

## Definition of done

- No absence-of-prohibition path is described or recorded as an explicit process request.
- Every normal and corrective specialist turn can choose a valid call or a final response.
- A static artifact can complete after its required effect is Host-proven without a mechanical process or reread loop.
- Required objective-specific validation and integration evidence still block false completion.
- Fake-provider browser/API evidence passes independently of any real-model claim.
- Real Ollama evidence remains unclaimed until separately authorized and executed.

## Primary references

- [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling): tools use JSON Schema; the application executes calls and returns correlated outputs; automatic choice permits zero or more calls.
- [Ollama tool calling](https://docs.ollama.com/capabilities/tool-calling): the multi-turn agent loop ends when the model returns no more tool calls.
- [Qwen Code tools](https://github.com/QwenLM/qwen-code/blob/main/docs/developers/tools/introduction.md): the core presents schemas, validates and executes requested tools, returns results, and lets the model produce the final answer.
- [Qwen3-Coder](https://qwenlm.github.io/blog/qwen3-coder/): the model was trained for long-horizon, execution-driven tool interaction, so precise stopping and tool contracts are part of the runtime's responsibility.
