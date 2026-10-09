# Execution completion footer metrics

The completion footer keeps the session status, routing identity, action/file
counts, planning/tool failure counters and end-to-end duration. Inference metrics
are optional badges in the same footer. The live execution header is unchanged.
Groups and badges wrap without independent separator elements.

`ExecutionSessionSummary.InferenceMetrics` persists with the existing execution
snapshot and conversation events. Provider observations are recorded by the Host
usage recorder independently of usage-ledger persistence. Supervisory checkpoints
retain one summary per execution session; replacing a session summary is
idempotent, and aggregation covers supervisor and worker turns.

- Output tokens are provider-reported generation counts, including generated
  reasoning. They are summed across calls, including retries. Estimated counts
  from the usage ledger or visible text are never used by this footer.
- Throughput is the summed output tokens divided by summed generation durations.
  Ollama's `eval_duration` or OpenAI-compatible `timings.predicted_ms` supplies
  the generation duration when reported. Otherwise streaming inference uses
  the measured interval between first and last received generation deltas.
  Every frame in one HTTP read shares its receipt timestamp; a single buffered
  batch cannot provide a generation interval. `generationTimingSource`
  distinguishes `provider`, `stream` and `mixed` observations; the badge tooltip
  describes stream measurement. Dispatch latency,
  prompt evaluation, tools, approval waits and total execution duration do not
  supply this denominator.
- TTFT uses a monotonic clock started immediately before inference dispatch and
  stopped on the first received content, thinking or tool-call token. Empty
  metadata events do not count. Provider-reported `load_duration` is subtracted
  from this interval; prompt processing remains included. The raw interval and
  loading duration are retained as `DispatchToFirstTokenMilliseconds` and
  `ModelLoadMilliseconds`. `FirstTokenTimingSource` distinguishes `after-model-load`
  from `dispatch` when loading duration is unavailable. The tooltip makes that
  fallback explicit; older saved metrics are not reinterpreted. Invalid loading
  durations longer than the observed interval leave corrected TTFT unavailable.
  It belongs to the first inference, even when
  that inference has no observable first-token timing.
- Missing token usage makes the total unavailable. Missing generation timing
  makes throughput unavailable. When tokens are known but throughput is not,
  show a `tok/s unavailable` badge with an explanation instead of silently
  omitting it. Other unavailable badges are omitted. A non-streaming
  response does not provide TTFT or a stream generation interval.

External harness startup is not an inference dispatch. For Ollama, a transparent
loopback transport observer measures the actual requests from Codex, Claude Code,
OpenCode and Qwen Code. It forwards request/response bytes to the original selected
Ollama endpoint, observes Responses, Chat Completions and Messages streams, and
records provider-reported output usage per inference. It preserves reasoning and
tool-call generation, ignores metadata, and sums each call once. The observer is
bound to the existing harness turn and cancels its HTTP requests with that turn.
It persists metrics only, never inference payloads. A bounded telemetry parser
does not truncate forwarded responses. The selected model, provider, tools and
Host policy are unchanged.

If no inference requests are observable, Codex output usage is the
change in the thread's cumulative output counter from the turn baseline; repeated
notifications replace that snapshot. A missing baseline leaves the count
unavailable. Claude Code supplies total turn output usage. OpenCode counts completed
assistant message usage, replacing repeated message snapshots and adding its
separate reasoning count. These native-protocol fallbacks do not supply
per-inference generation durations or dispatch timing, so timing badges remain
absent in that case. Qwen context-window occupancy is not output usage. Native
protocol counts are never added again to already observed HTTP inference counts.

Existing histories without these observations remain readable and omit the
badges. No inference is dispatched to fill missing historical metrics.

## Chat presentation

Chat and Execute share `inference.progress` events. An owned Ollama runtime's
existing output supplies loading and prompt-processing stages and measured prompt
percentages. This passive observer does not poll the GPU or issue provider calls.
The brain starts white and its original color fills upward with prompt progress,
with the numeric percentage beside it. Loading without a reported percentage stays
indeterminate. First generated content, reasoning or tool output restores the normal
indicator. Missing runtime telemetry is explicit, including external servers and
cloud providers. Concurrent observed inferences suppress uncorrelatable percentages;
progress telemetry never changes execution authority or supplies token timings.
Progress updates are coalesced through a bounded channel while the provider waits
for headers. Native and observable external-harness requests use the same source.

Chat uses the same live identity header and terminal metrics footer without
creating an Execute session. `ChatStreamEvent.ChatSummary` carries Host-observed
read/search counts, tool failures, elapsed time and aggregated inference metrics.
The usage recorder reports each target-response inference to the current Chat
turn independently of usage-ledger persistence; the saved timeline retains the
summary for replay without new provider requests.
Older Chat timelines can render model, terminal status and recorded duration
from existing events; they do not fabricate absent aggregate counts or metrics.

Read/search calls use the existing visible action cards, including bounded tool
results. Non-thinking model text accompanying a tool call appears as separate
progress commentary, never in the final answer. Thinking remains separate.

## Validation

`HarnessHttpMetricsAggregateCallsWithoutNativeUsageDuplication` exercises the
browser, API, harness and provider boundary for all three external inference
protocols, multiple calls, metadata, missing usage, buffered output and native
notification deduplication. Ordinary E2Es use fake providers only.

Real inference is explicitly opt-in. Start an isolated API with an isolated
trusted test workspace, then set `AGENTIC_ROUTER_REAL_METRICS_URL` to that API and
`AGENTIC_ROUTER_REAL_METRICS_MODEL` to an installed Ollama model. Run
`InstalledOllamaHarnessShowsRealCompletionMetrics` for Native, Codex, Claude Code,
OpenCode and Qwen Code. It verifies the browser badges against API metrics and
captures each real footer at 1280, 760 and 390 px. With history enabled, restart
the isolated API and run `RecordedOllamaHarnessFootersKeepPersistedMetrics` to
check all five recorded histories without dispatching another inference.

Provider semantics: [Ollama chat API](https://docs.ollama.com/api/chat),
[Codex token usage](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs),
[OpenCode usage normalization](https://github.com/anomalyco/opencode/blob/dev/packages/opencode/src/session/session.ts),
[Gemini token usage](https://ai.google.dev/gemini-api/docs/tokens).
