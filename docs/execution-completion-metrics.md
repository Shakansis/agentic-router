# Execution completion footer metrics

The completion footer keeps the session status, routing identity, action/file
counts, planning/tool failure counters and end-to-end duration. Inference metrics
include always-visible output-token and throughput badges in the same footer.
The live header uses the same metric renderer, showing unavailable values until
the Host supplies measurements. Every harness uses this presentation rule.
Groups and badges wrap without independent separator elements.

The terminal footer uses three rows on the chat canvas, without an outer card:
the existing collapsible Host summary and recorded turn duration/copyable trace;
an inset runtime panel with status, routing identity and metric badges; then
activity counts and the existing Review changes, Copy and optional Completion
check controls. Rows wrap on narrow screens. Chat uses the same arrangement,
omitting the Host summary when unavailable. Technical details remain accessible
above the footer; metric/evidence semantics are unchanged.

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
  makes throughput unavailable. Output-token and throughput badges always remain
  visible, including historical
  turns without observations. Missing data shows `unavailable`; invalid numeric
  data shows `error`. Short tooltips identify missing token counts, missing
  generation time or invalid values. Unavailable TTFT remains omitted. A non-streaming
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
per-inference generation durations or dispatch timing, so throughput shows
unavailable and TTFT remains absent in that case. Qwen context-window occupancy
is not output usage. Native
protocol counts are never added again to already observed HTTP inference counts.

Existing histories without these observations remain readable and show unavailable
output-token and throughput badges. No inference is dispatched to fill missing
historical metrics.

## Chat presentation

Follow-up trace `0HNP7LF1O5VFE:00000063` retained exact `tool_call`/`ToolCall`
envelopes; the running server already served the matching exclusion. Its script
URLs still reused the release version and lacked explicit cache revalidation.
The loaded user's browser was unavailable for direct inspection. The user
subsequently reloaded the tab and confirmed that the ToolCall rows disappeared,
confirming stale loaded client code in this case.
HTML, JavaScript and CSS now use `Cache-Control: no-cache` with existing ETags,
and the HTML asset URLs include a presentation revision to invalidate previously
cached release URLs. This does not reload an already-open page or interrupt a
turn. Reloading the page once loads the corrected renderer; restarting only the
server does not replace JavaScript already executing in a browser tab.
Isolated cache/presentation validation passed 9/9 cases, covering HTTP 200/304
revalidation, browser asset revision, hidden ToolCall envelopes, concrete actions,
replay, and common badges across five harnesses. Release build had zero warnings
and errors; scoped formatting and diff checks passed. Evidence:
`.artifacts/presentation-cache-20261011/results/cache-and-presentation.trx`.

Generic harness invocation envelopes named `tool_call` or `ToolCall` do not render
work-action rows or replace the current activity during normal dispatch. Concrete
actions (Read, Search, Edit, Run and others) retain their normal presentation.
Protocol events remain in diagnostics and the persisted timeline; historical
replay applies the same display rule. Existing tool discovery remains confined to
technical details. Failed invocation diagnostics remain available.

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

The 2026-10-11 Qwen follow-up inspected the running Host read-only. Execution
`5298cdf2826d41689b1dec47c0a3a953`, trace `0HNP7JKBNSDLO:000001CD`, retained TTFT
but aggregate output tokens and generation time were null. The served JavaScript
already contained terminal unavailable badges; the actual loaded browser DOM was
not accessible, so the reported disappearance in that tab was not reproduced.
No real inference was launched and the active Host was not restarted.

The shared renderer now supplies both live and terminal metric badges. Terminal
errors without a session summary receive a fallback status panel with unavailable
metrics. Historical replay uses the same renderer. The common HTTP observer also
accepts null optional message/delta/tool-call fields without treating otherwise
valid usage as malformed; malformed JSON remains unavailable. This parser gap is
covered by a deterministic compatibility fixture; it is not established as the
cause of the real Qwen turn. Qwen occupancy-only notifications still cannot supply
output counts or generation duration. Missing observations remain unavailable,
never estimated from context occupancy or total execution duration.

Final isolated validation: 18/18 browser/API cases passed across Native, Codex,
Claude Code, OpenCode and Qwen Code, including replay, unavailable metrics, live
badges during approval, buffered responses, and missing usage. The previous
binary passed seven HTTP cases and failed the new null-field regression; the
updated observer passed all eight. Release build had zero warnings/errors;
scoped formatting, JavaScript syntax and diff checks passed. Evidence is under
`.artifacts/harness-metrics-20261011/`. The user Host PID 128976 remained running;
only isolated test Hosts were restarted, and those processes were disposed.
An additional early-provider-failure browser/API regression passed, verifying
visible unavailable badges without a normal completion summary: 19 targeted
passing cases in total.

The footer layout was verified by 10 deterministic browser/API E2E cases
(`TestResults/footer-layout.trx`): Chat completion and history, Execute metrics
with present/missing usage and history, trace copying, and on-demand completion
checks through all five harness fixtures. Captures at 1280, 760 and 390 px verify
wrapping; desktop/mobile captures were visually inspected. Release build passed
with zero warnings/errors; formatting verification, JavaScript syntax checks and
diff checks passed. No real inference was used.

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

## Final usage and rejected inference (2026-10-11)

Native Ollama final `eval_count` and `eval_duration` are authoritative. Tok/s is
their ratio; it is not an estimate from text length or total turn duration.
Stream first-to-last timing is used only when provider duration is absent and
separate streamed deltas supply a measurable interval. Output counts are never
invented from stream chunks, context occupancy or native harness notifications
when HTTP inference observation is authoritative.

The common observer distinguishes `/api/generate` text `response` from the
Responses API's object `response`. Valid final native usage supersedes malformed
optional earlier telemetry; metadata/start frames do not mark inference complete.
Turn disposal closes admission and prevents late requests attaching to a closed
observation. Bounded final logs record inference identity, endpoint, HTTP status,
count, generation duration, source and diagnostic, without provider payloads.

Known admission rejections (400/401/403/404/405/413/415/422/429), with no generated
delta or reported token usage, do not erase the aggregate of successful calls.
Server failures and interrupted generation without final usage remain an unknown
part of the aggregate, rather than an exact partial total. Optional
`TelemetryError` carries a short reason. The shared browser renderer keeps both
badges visible and shows `error` with that tooltip for unavailable measurements;
known measurements remain numeric. Native failed/cancelled inference records the
same explicit missing-final-usage diagnostic. Ordinary absence without a known
failure remains `unavailable`. Existing persisted contracts remain compatible.
The request-scoped Chat service retains its latest operational summary so that
error/cancellation events include metrics recorded while an inference unwinds.
Previously an exception could exit the stream after measurement but before the
updated summary reached the browser. Terminal events and their persisted replay
now carry that summary through the existing event contract.

Trace evidence, reproducible regressions and validation boundaries are recorded
in [the investigation report](validation/2026-10-11-inference-and-read-context.md).
