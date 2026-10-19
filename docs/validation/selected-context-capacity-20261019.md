# Selected context authority — 2026-10-19

## Behavior

Local Ollama requests use the active role's target or exact-model override, capped by the declared model capacity. The global context defaults/provider ceiling no longer reduce local role windows. A model shared by multiple roles uses the active request's profile instead of the largest target of unrelated roles. Existing explicit escalation from target to the role maximum remains available when a request requires it.

Chat history, read-only workspace budgets and local Execute budgets use the same role/model capacity. Requested and effective values reach the context display; a model cap produces the visible warning. Cloud fallback budgets and explicitly bounded benchmark requests retain their existing behavior. Runtime profile measurement also honors the selected role and model maximum.

No 256k ceiling was introduced. Browser/API coverage saves and forwards 10,000,000 and 1,000,000,000 context tokens with declared fake-model support. The existing numeric contract is still a signed 32-bit integer (maximum 2,147,483,647), not arbitrary-precision/unlimited input. Neither these tests nor the UI estimates demonstrate real hardware capacity.

## Files

- `Runtime/OllamaRuntimeProfileResolver.cs`: active role target, model-cap resolution and requested-context provenance; no global cap for local models.
- `Runtime/OllamaRuntimeProfileService.cs`: role/model limits for explicit runtime measurements.
- `Chat/ChatStreamService.cs`: consistent history/read/Execute budgets and usage fields.
- `wwwroot/index.html`, `settings-runtime.js`: explain the role control and the remaining fallback/benchmark settings.
- `wwwroot/conversation-render.js`: cap explanation and compact M/B presentation.
- `wwwroot/story-editor.js`: suppress delayed programmatic scroll echoes, discovered during regression, so returning one comparison column to the top cannot be undone by the counterpart's old event.
- `LargeContextEndToEndTests.cs`, `BenchmarkAndHarnessEndToEndTests.cs`: role/override matrix, long-history retention, shared-role isolation and external harness context.
- `docs/model-runtime-performance.md`: current context semantics.

## Executed validation

- Release: `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers` passed, **zero warnings/errors**.
- Scoped `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include ...` passed for the five changed C# files listed above.
- `node --check` passed for `story-editor.js`, `conversation-render.js`, `settings-runtime.js`; `git diff --check` passed (existing Git newline notices only). Intended changes reviewed without reverting unrelated work.
- `.tmp/Test-RpgEditor.ps1` ran LargeContext and StoryEditor classes plus Qwen selected-context, runtime-profile, usage-popover, model-history trimming, coordinator compaction, native live-context and three cloud output-budget regression cases.
- Initial regression: **71 passed / 1 failed / 0 skipped**, 72 cases. The comparison scroll test exposed a delayed reciprocal scroll event that could undo returning to the top. Fixed event-echo handling without relaxing the assertion or timeout.
- Final rerun: **12 passed / 0 failed / 0 skipped** (complete LargeContext class, comparison scroll and context popover), 1m05s. All **72 distinct cases** have passing results. This rerun additionally verifies `10.0M` and `1.0B` display.

Observed at the fake Ollama wire, with original global settings unchanged:

| Requested | Declared model capacity | Effective |
| --- | --- | --- |
| 262,144 | 131,072 | 131,072 |
| 262,144 | 524,288 | 262,144 |
| 10,000,000 | 10,000,000 | 10,000,000 |
| 1,000,000,000 | 1,000,000,000 | 1,000,000,000 |

The 256k cases cover role defaults and exact-model overrides. Additional coverage retains 140k characters of prior user/assistant history beyond the old global budget, prevents another role's 1M target from increasing an explicit primary 256k request, verifies Qwen harness 256k requests with 128k/512k model capacities, and preserves atomic rejection of unordered settings and genuinely oversized payloads.

Evidence under `.artifacts/rpg-editor/`: `TestResults/selected-context-capacity-20261019.trx`, `TestResults/selected-context-capacity-final-20261019.trx`, and `visual-review/selected-context-*.png`. Inspected captures of the 256k→128k warning and final 1B context display.

## Validation boundary and cleanup

Real browser/Host API, fake providers only at the external boundary. No real GPU allocation or model inference was attempted. The running user AR was not replaced; activation requires the normal AR rebuild/restart, preserving unsaved drafts first.

Final process inspection found no testhost, isolated AR, fake CLI, headless browser or dotnet build server remaining. Preserved AR PID 16756 (normal Release executable, parent 16340; ports 5294/7155/7156), Ollama PID 24372 (11434), and AR-managed Ollama PID 27244 (parent 16756).
