# Model throughput test and turn thinking

Test model runs one tool-free Native stream with an 8,192-output-token budget
(including reasoning). The prompt requests a complete C# task queue implementation,
integration tests and recovery discussion, rather than repetitive list output.
The model may stop early: the UI reports actual output separately from the budget.
It bypasses conversation routing and does not create a conversation message.
This measures throughput on a coding workload, not correctness or benchmark quality.

The browser collects the same settings draft used by the modal footer Save and
sends it to POST /api/models/test/stream together with the selected model and inference
profile. The API validates the whole draft before dispatch. It passes the draft
through a non-serialized per-call context to Ollama runtime resolution; it never
writes ISettingsStore or temporarily swaps process-wide settings. GPU affinity,
context, exact-model/digest MTP and batch, sampling and thinking therefore use the
selected values. The test sends the selected model-test keep-alive explicitly.
The fixed 8,192-token test ceiling replaces the normal output limit for this probe.
The selected context policy and generation timeout still apply. Small context limits
may reject the workload; the test never persists a larger context or timeout.
New Model test profiles use a 16,384-token target/maximum context and an
8,192-token output limit. Existing saved limits remain unchanged; insufficient
context reports the required and allowed sizes with instructions to adjust the draft.
POST /api/models/test remains available for clients requiring one JSON result.

The SSE stream reports preparation, loading/prefill, generation and collection.
Loading/prefill starts before runtime resolution and ends with the first content
or thinking delta. Ollama's HTTP API does not expose separate live load/prefill
stages. Chat/Execute can additionally observe owned runtime logs; see
[inference progress](execution-completion-metrics.md).
The asynchronous modal refresh preserves pending edits. Closing/reopening the
modal retains an active test and its Cancel control.
During generation the UI shows received characters (not estimated token counts),
plus elapsed time and Cancel test. Disconnecting cancels the provider request.
Results appear below the button with eight legible metrics: generation TPS,
provider output tokens, first-token latency excluding reported model loading, total duration, provider
input tokens, loading time, prefill time and generation time.
TPS uses provider generation time (or the existing measured stream fallback),
never total elapsed time. Missing metrics remain unavailable. Total duration
includes loading/discovery/prefill. The result identifies the profile and runtime
request values; configured values are not presented as proof of runner activation.
Changing runtime settings may reload an already loaded model. Repeating unchanged
settings measures the resident runner more directly. Draft tests do not Save.

The composer has Profile default, None, Low, Medium and High beside the harness.
Its maximum width is 1,040 px; harness and thinking use dedicated compact widths
to share the action row at full size. Controls wrap on narrower screens.
The override applies to the next Chat/Direct Execute and to supervised Workers;
Supervisor phases retain their separate effort settings, now also accepting None.
Profile default preserves existing behavior. None maps to disabled thinking where
supported. Boolean Ollama models map Low/Medium/High to enabled thinking; Execute
also provides the requested effort guidance and reports missing native levels.
Models with reviewed native levels retain those levels. Unsupported controls remain
explicit in activity. Claude Code has no reviewed None CLI effort control here;
None emits effort.unavailable and is not sent as an invalid --effort value.
Harness names omit the Experimental suffix; capability/availability checks remain.

See [completion checks](execution-completion-report.md) for the optional report.

## Validation — 2026-10-04

- Release solution build with `--no-restore -m:1 -nr:false
  -p:UseSharedCompilation=false -p:OutputPath=bin/performance-ux-final/Release/`:
  zero warnings/errors. The user's running binaries were not replaced.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`, JavaScript
  syntax checks for the four changed scripts, and `git diff --check`: passed.
- 62 browser/API E2Es passed in
  `tests/AgenticRouter.EndToEndTests/TestResults/performance-ux/final.trx`.
  Coverage includes unsaved sampling/MTP/batch, no settings persistence,
  model-test timeout behavior, all five harnesses with optional reports,
  history-disabled conversations, checks after Host restart, phase None,
  Worker thinking ownership, and existing runtime Save/YAML regressions.
- Real authorized Native/Qwen `qwen3.8:27b-q4_K_M` acceptance used an isolated AR
  and Ollama. Three 128-token probes sent unsaved MTP 4, batch 256 then 512,
  thinking false and 32K context. Runner commands confirmed batch replacement.
  The unchanged warm probe displayed 114.58 tok/s, 0.43 s TTFT and 1.60 s total;
  this simple repetitive probe is not comparable to coding throughput.
- Real Execute created/read the expected file and finished without a report
  inference. Clicking Completion check explicitly started the review. That review
  hit the configured 300 s timeout while a separate user runner was also active;
  the original execution stayed completed, the notice was shown, and a repeated
  click reused the result. Successful report generation is covered by fake-provider
  E2Es, not claimed for that real review.
- All work-started AR/Ollama/proxy services were stopped. User services and saved
  configuration were preserved. Reloading the frontend alone does not update the
  running backend; rebuild/restart the user's AR to use the new API contracts.

## Validation — progress and 8K output workload (2026-10-04)

- `dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false
  -p:UseSharedCompilation=false -p:OutputPath=bin/model-test-progress-final/Release/`: zero warnings/errors.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include`
  the changed C# files, `node --check AgenticRouter.Api/wwwroot/settings-runtime.js`,
  `git diff --check`, and review against a pre-edit snapshot passed.
- 19 browser/API E2Es passed in `TestResults/model-test-progress/final.trx`: ordered
  progress events, metrics, unsaved parameters, insufficient selected context,
  cancellation, cold start, thinking, composer layout, and runtime Save regressions.
  After the final width/lifecycle adjustment, both relevant E2Es passed again in
  `lifecycle-layout.trx`, including closing/reopening Settings during a running test.
- Real browser acceptance used Native, `qwen3.8:27b-q4_K_M`, isolated CUDA/Ollama
  0.34.2, 32K context, thinking disabled, Auto MTP/batch, seed 20261004.
  One coding request generated **8,192 output tokens**, with 116 input tokens:
  **37.48 tok/s**, **5.47 s TTFT**, **224.17 s total**, 5.24 s load, 0.21 s prefill,
  and 218.59 s generation. The provider tap independently confirmed `num_predict=8192`
  and final token/timing metrics. This validates 8K output, not 8K input context.
- The browser showed live generation progress; wide/narrow composer screenshots
  and real progress/result screenshots were inspected. Controls share one row at
  maximum width and wrap without overflow at a 600 px viewport.
- Validation services and browser tab were stopped; user AR/Ollama processes were
  preserved. The new backend must be loaded by restarting AR from the rebuilt version.
