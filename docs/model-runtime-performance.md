# Model runtime performance

The runtime settings editor retains all existing context, role override, memory,
analysis and measurement controls. Presentation follows the existing AR theme.
Help buttons use a manually controlled native popover above the Settings dialog,
escaping sidebar layering and scroll-container clipping. Hover and keyboard focus
open help; Escape, scroll, dialog close and resize dismiss it.

## Configuration

Context configuration accepts 1,024 through 2,147,483,647 tokens (the integer contract limit) for the global
default/provider ceiling, role minimum/target/maximum and exact-model overrides.
The escalation ladder accepts the same range, retaining its ordered/distinct
validation. Existing saved/default context sizes and ladder values are not raised
automatically. Role windows and exact overrides may be saved above the configured
provider ceiling and model-declared capacity; saving a preference does not
allocate the requested window. Local Ollama requests use the active role target
or exact-model override, capped by the declared model limit. Global default/provider
context settings do not cap local role profiles, history budgeting or workspace-read
budgeting. Other roles sharing the same model do not increase the active role target.
The same resolution applies to local Chat, Storytelling and Execute harnesses.
No 256k ceiling is imposed: 10,000,000 and 1,000,000,000 are valid profile values
within the existing integer contract. Request-specific bounded operations may still
explicitly supply a generation context cap; the Benchmark Lab retains its explicit
measurement limits. Runtime resolution retains the requested target separately. A capped
maximum between escalation ladder entries remains usable when the request fits.
Chat context usage exposes requested, effective and model-maximum values, including
a visible warning identifying the model maximum when the requested target was reduced. Effective values describe
the Host-selected window sent to the provider, not an independent VRAM measurement.
Output reservations must
remain below the role maximum. Benchmark context uses the configured provider
ceiling and accepts individual token values, including 250,000 and 1,000,000.

Draft tokens, input batch size and managed Ollama KV cache precision are configured. The typed performance object is
attached to the existing exact provider/model/digest identity, alongside role
overrides, and applies across roles. Sampling profiles, GPU placement, memory
policy, context limits, approval and recovery semantics are unchanged.

- Missing performance settings preserve the old request behavior.
- Draft tokens: null means Auto (omit); 0 means Off; positive integers are explicit.
- Batch size: null means Auto (omit); positive integers are explicit.
- KV cache: null inherits the global `managedKvCacheType`; explicit values are
  `f16`, `q8_0`, and `q4_0`. Global Auto inherits the server environment.
- Field edits remain pending per exact model, including when navigating models or
  sections. Only the modal footer's Save changes persists them, together with the
  other settings. There is no separate performance apply button.
- Save validates pending Custom values across all edited models before sending
  settings. Invalid input does not partially persist the modal. Cancel discards
  pending performance edits.
- Removing a context role override preserves model performance configuration.
- JSON persistence and portable YAML carry the optional performance object.
  YAML uses performance.draft_tokens and performance.batch_size with auto or an
  integer, and performance.kv_cache_type with inherit or an explicit cache type. Measured hardware records remain local.

## Provider capability and application evidence

The neutral capability contract declares conceptual identifiers, transport
mechanism, scope, support, provenance and explanation. The Ollama adapter maps
draftTokens to options.draft_num_predict and batchSize to options.num_batch
for native streaming, non-streaming, tools and model preload requests. Both are
load options in the [Ollama API contract](https://github.com/ollama/ollama/blob/main/api/types.go).

Positive nextn_predict_layers metadata confirms that the model declares draft
layers, but not runtime activation. Missing metadata is unknown, never proof that
the model lacks support. Non-generative model metadata disables this LLM editor.
Ollama accepts num_batch as a runner load option; /api/ps does not expose its
effective value. Unknown support permits configuration and clearly warns that
the runtime may reject or ignore it. Option-specific provider errors retain their
cause and surface runtime-performance-rejected without rewriting saved settings.

Native activity reports explicit values sent and Auto omitted. When draft tokens
or batch size are configured, the existing Host inference transport translates
Anthropic Messages, OpenAI Chat Completions and Responses to native Ollama chat.
The Host supplies its resolved context, output budget, keep_alive and exact
provider/model/digest performance options; protocol adapters retain the harness
request's sampling and reasoning controls, tools, inline images, call identities
and streaming order. Off sends zero; Auto remains omitted. No MTP value is hardcoded,
no model alias/Modelfile is created, and no saved setting is changed.

Responses namespaces are mapped by their declared exact names. Client tool search
and the existing Codex custom apply_patch bridge retain their native protocol.
Unknown protocol shapes fail explicitly; they are not silently discarded.
Payloads remain transient. Native provider load, prompt evaluation and generation
measurements remain available through translation. Accepted options alone still do
not prove activation; the real validation checks server/runner PIDs, command-line
options and provider timings independently.

Native Execute and external harness startup use the same effective context
resolution, including the model ceiling and Execute output reserve. Merely changing
the harness does not request a different context or KV server configuration.

## Measurement identity

Measurements record configured performance beside the observed memory/context
facts. Their runtime signature includes explicit performance values, and changing
those values makes prior measurements stale. All-Auto preserves the legacy
signature. Measurement preload sends explicit options even when context already
matches. Actual activation remains unverified when the provider supplies no
independent evidence.

The benchmark configuration fingerprint includes exact model identity and its
configured performance values. History's existing partial-comparability assessment
therefore identifies different configurations without hiding prior results.

## Validation boundary

Browser/API E2E tests fake Ollama only at the provider boundary. Real model
generation, GPU performance acceptance, and cloud-provider calls require separate
explicit authorization under the existing testing policy.

## Changed components

- Configuration: ApplicationSettings.cs, SettingsValidator.cs and
  PortableYamlSettingsService.cs.
- Provider contract and mapping: ModelProviders.cs, IOllamaClient.cs and
  OllamaClient.cs.
- Runtime resolution and evidence: ModelRuntimePerformance.cs,
  OllamaRuntimeProfileContracts.cs, OllamaRuntimeProfileResolver.cs and
  OllamaRuntimeProfileService.cs.
- Activity and benchmark identity: ChatStreamService.cs and BenchmarkEngine.cs.
- Settings presentation: wwwroot/index.html, app.js, settings-runtime.js,
  model-runtime.css and benchmark.css (runtime rules moved to their own stylesheet).
- E2E coverage: RuntimePerformanceEndToEndTests.cs, FakeOllamaServer.cs,
  TestSettings.cs and ExecuteCoreEndToEndTests.cs (tooltip assertion now checks
  the actual native popover while preserving the above-input/menu-clearance check).

## Verification on this checkout

- Release build: `dotnet build AgenticRouter.slnx -c Release --no-restore
  -p:OutDir=<isolated output under tests/bin>`; zero warnings and errors.
  Isolated output avoids the executable held by the running user-owned app.
- Formatting: `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- JavaScript: `node --check` for app.js and settings-runtime.js.
- Browser/API: `dotnet test` with Release, --no-build, --no-restore, the same OutDir
  and explicit fake-provider executable paths. Six new tests and nine relevant
  existing tests passed across the final runs. Coverage includes JSON/YAML/restart,
  Off and Auto omission, validation atomicity, exact digest, capabilities/rejection,
  native structured and tool requests, external-harness unavailability, measurement
  staleness, benchmark comparison, desktop/mobile geometry, keyboard and menu layering.
- Existing unrelated failure: SettingsUseViewportNavigationDirtyProtectionAndResponsiveFocus
  expects an error toast on invalid save. It fails at ExecuteCoreEndToEndTests.cs:1492
  both with the changed UI and with a separate copy of the pre-change UI served via
  ASPNETCORE_WEBROOT. Field errors are present; the error toast is absent.
  No assertion was weakened and the existing save behavior was preserved.
- No real model generation, GPU workload or cloud-provider validation was performed.

## Footer save follow-up

Removed the separate performance apply button. The changed files are
wwwroot/index.html, app.js, settings-runtime.js,
RuntimePerformanceEndToEndTests.cs and this document.

Performance edits now join the modal's existing Save changes submission.
Per-model pending values survive model/section navigation. Invalid Custom
values in another model block the whole save and reveal the offending field;
Cancel discards pending edits without writing settings.

Verification: isolated Release build with zero warnings/errors, format
--verify-no-changes, node --check for both changed scripts and git diff --check
passed. Thirteen fake-provider browser/API E2E tests passed (eight runtime
performance tests and five existing settings/runtime/inference/YAML tests).
No real-model, GPU or cloud-provider workload was used.

## Per-model cache and resource layering (2026-10-09)

The exact provider/model/digest cache override is shared by roles. The global
setting remains an inherited default; existing saved F16 settings are preserved.
The managed server identity and lease now include GPU selection and cache type.
Compatible requests reuse a server; different precisions select separate endpoints
without stopping the existing cache configuration. Quantized cache also sets
OLLAMA_FLASH_ATTENTION=1 in that child only. Existing keep-alive and unload policy
still controls loaded models; concurrent servers do not imply free model memory.
Context-driven server replacement retains its existing behavior within the same
GPU/cache identity. This change does not add role-specific cache settings.

Native generation, preloads, measurement, external-harness endpoints and durable
supervision route identity resolve the same exact-model setting. External servers
remain unchanged and report cache application as unavailable. Activity and resource
placement evidence distinguish applied server environment from unverified effective
runner precision. Cache-only settings do not produce the compatibility warning
reserved for draft tokens and batch size. Resolved global cache defaults and model
overrides participate in measurement and benchmark signatures.

The header's backdrop filter creates a stacking context. Its explicit stacking
order now puts the resource popover above the live sticky Execute header. Browser
coverage checks actual overlapping pointer hits, preserving the sticky behavior,
at desktop and mobile widths, and records screenshots.

### Validation for the cache/layering follow-up

- Release: `dotnet build AgenticRouter.slnx -c Release --no-restore
  -p:BaseOutputPath=bin/kv-cache/ -p:UseSharedCompilation=false -nodeReuse:false
  -m:1` passed with zero warnings and errors. The isolated output preserves the
  normal app build and user processes.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include
  <changed C# files>`, `node --check` for app.js/settings-runtime.js and
  `git diff --check` passed.
- 36 distinct selected scenarios have passing evidence across the final applicable
  runs: 14 runtime performance/settings cases (`TestResults/kv-cache-performance-final.trx`),
  14 managed startup cases (`TestResults/kv-cache-startup-final.trx`), and the eight
  unaffected passing sticky UI, managed lifecycle and supervision cases in
  `TestResults/kv-cache-final.trx`.
- The managed-cache browser test covers Native Chat and Execute through Codex,
  Claude Code, Qwen Code and OpenCode, with fake external providers. It verifies
  the child process environment, quantized-cache Flash Attention, global F16
  inheritance, Q8 override, distinct live processes and PID reuse after switching
  models back. No real inference or throughput benchmark was run.
- An initial YAML assertion was corrected to match quoted YAML scalars. The native
  selection case uses Chat because its generic report prompt has no fake Execute
  completion fixture. The wider run exposed Windows socket access failures on
  derived cache ports; the test now probes the whole port set before starting
  processes and preserves failure logs before teardown. Assertions and timeouts
  were not weakened. Earlier failed TRX files remain available for diagnosis.
- Desktop/mobile resource layering screenshots are in
  `.artifacts/kv-cache-review/resources-desktop.png` and `resources-mobile.png`.
  Existing saved global F16 configuration was not changed. Release publication
  remains pending user review.

Changed components for this follow-up: ApplicationSettings, SettingsValidator,
PortableYamlSettingsService, ModelRuntimePerformance, OllamaManagedServerManager,
OllamaClient, OllamaRuntimeProfileService, RuntimeStatusService,
SupervisionRouteResolver and ChatStreamService; the Settings HTML/JS and shared
header CSS; inference help; RuntimePerformance, OllamaStartupRecovery and
ExecutionState browser tests plus FakeOllamaCli environment evidence.

An explicit turn Thinking=None takes precedence over compatibility-harness defaults. For a reasoning-capable Ollama model, the Host routes that inference through the native translator even when draft and batch options are Auto, and sends `think:false` on every generation, including tool continuations. This does not hide reasoning received from a provider; it controls generation. Installed-Claude browser/API coverage verifies the outgoing control with draft tokens 0 and 3.

## Gemma 4 MTP investigation, 2026-10-29/30

The user authorized local inference to investigate a slowdown. The retained trace `0HNPM3VGQMH8L:00000635` selected `gemma4-rpg:26b-mtp` with an effective 131,072-token window. Its retained usage has one 17,483-input/4,096-output call lasting 162.46 s including work other than decoding; it does not independently establish exact decoding speed or MTP acceptance for that trace.

Ollama 0.34.2 `/api/show` identifies the same main model and projector in `gemma4:26b-a4b-it-qat` and `gemma4-rpg:26b-mtp`. The latter adds one assistant model and `draft_num_predict 3`. On the AR-managed Vulkan/F16 server, direct Ollama requests omitting `draft_num_predict`, explicitly sending `3`, then omitting it again retained one runner PID and measured 71.16, 68.96 and 66.89 output tokens/s for a 71-token synthetic prompt and 512-token output. The effective runner had one `--spec-type draft-mtp --spec-draft-n-max 3`. Sending `0` removed those flags and the attached draft model. This demonstrates inheritance and replacement, not double MTP application.

An independently started, single-request Ollama server, same Vulkan two-GPU/F16/131,072/batch-512 setup, measured MTP=3 at 70.78 tokens/s for the short prompt and 41.60 tokens/s for 21,578 input tokens; MTP=0 on the same MTP model and longer input measured 99.78 tokens/s. Repeat penalty 1.05 gave 39.46 tokens/s on the longer input. On the RTX 4090 alone, the same longer prompt measured 59.49 with MTP=3 and 46.66 with MTP=0; its short MTP=3 run measured 45.83. These are Ollama `eval_count / eval_duration`, excluding model load and prompt prefill. The second longer MTP call reused prompt cache, so its prefill time is not directly comparable. Runner logs show 31/31 base and 5/5 draft layers offloaded to Vulkan devices, one speculative implementation, and 268/727 proposed draft tokens accepted on the two-GPU longer 1.0-penalty call. The one-GPU result suggests an interaction with GPU distribution for this workload, but individual runs do not establish the underlying mechanism. Acceptance and speed are workload-dependent; no causal conclusion about the user's hallucinations or the historical 120–140 tokens/s measurement follows from these synthetic tests.

Raw synthetic requests, timing responses, runner details and the diagnostic log are under `.artifacts/gemma-mtp-20261029/`. A first isolated-server attempt omitted `OLLAMA_VULKAN=1`, ran on CPU and was stopped; its results under `cpu-invalid/` are excluded from all GPU comparisons. User-owned AR and Ollama processes were not stopped or reconfigured; the initially resident model was unloaded as part of the authorized comparison. Diagnostic processes started for testing were stopped after use.
