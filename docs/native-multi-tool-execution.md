# Native multi-tool calling and parallel execution V1

Native accepts every tool call from a provider response. Parallel execution is
an independent Host setting; Off never truncates a response to its first call.
This feature does not add DAG scheduling, subagents, another inference loop,
or another provider family.

## Implementation and contracts

- `SpecialistToolingProfiles` declares `AcceptsMultipleCalls`, replacing the
  misleading `AllowsParallelCalls`. Normalization preserves call order, names,
  argument objects and provider IDs. Missing IDs receive Host IDs.
- `LocalActionPlanningResult` retains its single proposal for existing callers
  and adds an optional `ToolExecutionBatch`, batch ID, position and individual
  parsing failure. The batch identifies one inference, not a transaction.
- The Native Host drains the pending calls before requesting another inference.
  A response with one call follows the same validation and approval path.
  Invalid siblings receive explicit results; they do not discard valid siblings.
  Duplicate provider IDs make the response ambiguous: reject it before effects
  and return a bounded planning correction requesting distinct IDs.
- One assistant message contains the entire emitted call array. Each result is
  correlated by call ID and consolidated in emission order. Recovery can refine
  a rejection without adding a second result for the same call.
- Context compaction retains complete assistant/result groups. It cannot retain
  only the tail of a batch and leave tool results or calls without their peers.

## Host scheduling

Global `execution` settings, shown in General settings and included in JSON/YAML:

| Setting | Values | Default |
| --- | --- | --- |
| `parallelToolExecution` | `off`, `auto`, `on` | `off` |
| `maxConcurrentToolCalls` | 1 through 16 | 4 |

Off processes multiple calls sequentially. Auto permits overlap of eligible
reads. On also encourages the model to emit independent read groups; it grants
no additional authority. These controls apply to Native only.

The explicit V1 allowlist is `read_file`, `get_file_info`, `list_files`, and
`search_text`. These paths use local filesystem I/O and locked session
observation/refresh metadata. There is no concurrent conversation-history,
approval, instruction-discovery or journal write. The Host validates each call,
loads applicable instructions, checks scope, binds plan evidence and applies the
existing approval policy before admission. An eligible name alone cannot bypass
the validated action's read-only flag or explicit approval requirement.

Writes, edits, deletes, downloads, processes, Git operations, web search,
diagnostics, plans, toolset negotiation, user input and unknown capabilities are
sequential barriers. Native drains admitted reads before processing the next
barrier or issuing another inference. It does not move reads across a barrier.

`read_files` already batches paths and currently reads them sequentially.
`create_files` owns its existing per-call validation, verification and rollback.
Neither is split into individual calls or given nested concurrency by V1.

The FIFO scheduler bounds admission and propagates the turn/session cancellation
token. Eligible runtime calls have individual 60-second cancellation deadlines;
failures remain individual tool results. Existing process/validation deadlines
remain authoritative for sequential operations. Disposal cancels and joins every
remaining admitted read before the Host releases the turn. Read output reserves
are shared across a contiguous eligible group, rather than allowing every read
to consume the full remaining context window.

## Persistence, recovery and observability

`ExecutionSessionReview.NativeToolBatches` extends the existing execution
snapshot/history path. It retains the last 16 batches with inference sequence,
original call identity, position, argument hash, status, action ID, result code,
duration, admission wait, scheduling reason, peak concurrency and completion
time. It does not persist raw arguments, tool payloads or file contents.

Validated actions and the existing durable supervision action journal also carry
batch ID, call ID and position. Mutations retain the existing prepared/in-flight/
committed reconciliation and file-effect verification. A partial batch is never
treated as atomic or replayed as a whole. Restored unfinished calls are explicitly
interrupted. Completed action evidence remains available to existing recovery;
metadata hashes are not substituted for a persisted tool result body. No new
checkpoint store or automatic batch retry is introduced.

`tools.batch-created`, `tools.call-rejected`, `tools.parallel-running`,
`tools.call-finished` and `tools.call-recorded` extend the activity stream.
Typed runtime telemetry carries active/peak counts and the configured limit.
The browser shows a small live indicator when more than one tool is active.
Existing event-retention limits still apply. Logs distinguish received, parsed,
rejected and executed calls, with correlation IDs and runtime duration.
Capacity wait measures waiting for a slot while more calls are pending;
admission wait also includes validation and preceding barriers. Batch wall time
includes Host coordination and must not be confused with summed tool durations.

## Runtime compatibility

Installed Ollama CLI/server discovery reported version **0.34.2**. Its native
`/api/chat` arguments are structured JSON values. Ollama documents multiple
calls per response and returning every result in order:
[tool calling](https://docs.ollama.com/capabilities/tool-calling).
No additional runtime flag is required by this Native implementation; the
model/template must actually emit structured calls. Single-call models continue
to work without artificial batches or extra inference to fill a batch.

The OpenAI-compatible parser handles JSON-encoded argument strings separately;
malformed calls remain available for individual rejection. LM Studio documents
an array of calls with encoded arguments and IDs in its
[OpenAI-compatible tool API](https://lmstudio.ai/docs/developer/openai-compat/tools).
Its SDK's parallel tool execution setting is an SDK executor setting, not an AR
runtime requirement. This checkout has no first-class LM Studio Native provider;
`run-lmstudio-harness-workflows.py` explicitly excludes Native/AR. V1 does not
silently add or claim acceptance for that provider path.

## Validation and benchmark

Phase A was validated before scheduler implementation: Release build with zero
warnings and six deterministic browser/API E2E scenarios passed.

The final validation record and benchmark measurements belong in
`docs/validation/native-multi-tool-execution-v1.md`.

The opt-in launcher `scripts/run-native-multitool-benchmark.ps1` uses an installed
model, isolated Host and copied real source files. It does not restart an existing
Host or Ollama server. A different resident model prevents the launcher from
loading a competing model. It warms the model once, rotates A/B/C/D order across
three repetitions, and saves per-turn context/token, TTFT/load, inference, batch,
tool and answer-quality evidence. It unloads only the benchmark-loaded model.

A requests one call per inference as the controlled representation of the old
behavior on the same Host binary. B requests multiple sequential calls; C requests
the same calls with Auto scheduling; D requests `read_files`. Actual emitted
batch sizes and quality must be checked before attributing any timing difference
to fewer inferences or concurrent I/O. A is not a historical-binary comparison.

The batch identity, ordered positions, independent validation outcomes and
execution results are sufficient extension points for a later dependency-aware
scheduler. V1 adds no dependency graph, priority queue or speculative resource
protocol.
