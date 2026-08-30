# PLAN v58: Local resource onboarding

## Objective

Make a new Windows installation operational without requiring the user to know
which runtime, harness package, or Ollama model command to run.

## Product contract

1. The empty conversation screen reports live availability for Ollama, local
   GPU-compatible models, the built-in Native harness, and optional external
   harnesses.
2. Ollama and external harness installation is always initiated by an explicit
   user action. The browser sends only a reviewed resource identifier; the Host
   owns the exact command allowlist.
3. Ollama, Codex, and Claude Code use exact WinGet package identifiers. OpenCode
   and Qwen Code use exact npm package identifiers matching their existing
   harness discovery paths.
4. External installers run in a separate PowerShell window. Agentic Router does
   not claim installation success from process launch; periodic normal discovery
   must observe the runtime or harness before the UI marks it available.
5. Model downloads accept only the recommendation set computed by the Host for
   the currently detected hardware. They run through Ollama `/api/pull`, report
   byte progress when Ollama provides it, and are cancelled during Host shutdown.
6. Recommendations use the largest individual detected GPU, not aggregate VRAM.
   This avoids promising that a model fits one device merely because multiple
   GPUs are present. Conservative headroom is retained for context and runtime
   buffers.
7. Harnesses remain optional and install on demand. Core local readiness requires
   reachable Ollama plus at least one installed model whose size is compatible
   with the detected GPU evidence. Sub-800 MB support/embedding models do not
   satisfy this readiness check. Unknown GPU memory falls back conservatively.
8. Safe mode keeps status discovery read-only and rejects installer/model pull
   actions.
9. The welcome/onboarding surface is always shown before a new conversation by
   default, including when Ollama and compatible models already exist. The user
   may dismiss it for the current conversation or persist an opt-out. Incomplete
   core readiness always keeps the surface visible. The complete validator,
   preference, and an explicit reopen action remain available under Settings >
   Local resources.
10. Codex is labeled as the recommended Execute harness because it is the
    current stability recommendation. This is advisory only; Native and every
    other available harness remain selectable.
11. A harness may be preselected while Chat is active and that selection is
    retained when switching to Execute. Auto Model x Harness remains an
    Execute-only choice.

## Reviewed package and model catalog

| Resource | Installation identity |
| --- | --- |
| Ollama | `Ollama.Ollama` via WinGet |
| Codex | `OpenAI.Codex` via WinGet |
| Claude Code | `Anthropic.ClaudeCode` via WinGet |
| OpenCode | `opencode-ai@latest` via npm |
| Qwen Code | `@qwen-code/qwen-code@latest` via npm |

The model catalog is intentionally small and deterministic:

- 24 GB class: `qwen3.8:27b-q4_K_M`;
- 12 GB class: `qwen2.5-coder:14b`;
- 8 GB class: `qwen2.5-coder:7b`;
- 4 GB class: `qwen2.5-coder:3b`;
- constrained or unknown memory: `qwen2.5-coder:1.5b`.

This is a download-fit recommendation, not a benchmark ranking or guarantee of
runtime speed. Exact provider allocation remains owned by Ollama.

## Validation boundary

Deterministic E2E covers status composition, legacy-settings first display,
per-conversation dismissal, persisted opt-out and re-enable, explicit reopen,
browser rendering, rejection of an unreviewed model, streamed fake-provider
pull, and post-pull discovery. Real package installation and real model downloads
are intentionally not performed by automated validation.
