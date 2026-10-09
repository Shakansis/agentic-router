# Inference progress and runtime performance

The completion footer TTFT excludes provider-reported model loading, while keeping
prompt processing and the first generated reasoning token. Raw dispatch latency,
load time and measurement basis remain available in the same metrics record.
The common Host progress contract covers Native and observable external harnesses.
See [completion metrics](execution-completion-metrics.md) for missing telemetry and
historical-record behavior.

`ollamaRuntime.managedKvCacheType` selects `auto`, `f16`, `q8_0` or `q4_0` for
AR-owned Ollama servers. Auto preserves inherited environment settings. Explicit
values set only the child process's `OLLAMA_KV_CACHE_TYPE`; they do not change
Windows environment variables or an externally managed Ollama server. The setting
uses the existing Settings footer Save and YAML import/export. It takes effect
when AR starts an owned server; existing servers require an AR restart. Unsaved
model-test drafts do not reconfigure an existing server. Model/digest-specific
draft-token settings remain independent of this server-wide cache setting.

F16 consumes more memory. Select cache format and draft depth from measured results
at the intended context window, and verify the resulting runner arguments. A saved
setting is not proof that a provider accepted it. These controls do not reduce the
context window or silently change generation settings for other installations.
