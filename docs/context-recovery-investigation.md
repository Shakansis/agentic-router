# Context recovery and empty compression summary

Investigation date: 2026-09-17. Incident: `0HNOKLICVDRCH:00000045`.

## Implemented: acknowledge delivered history independently of answer success

Qwen Code kept the native session after an empty answer, but advanced its canonical
history synchronization cursor only after a nonempty successful answer. The Host's
supervisor recovery then sent full conversation hydration into that same session.

Qwen Code now advances the cursor after a successful prompt submission with a valid
prompt ID. OpenCode does so after its successful `prompt_async` acknowledgement;
Codex does so after `turn/start` returns a valid turn ID. A generation failure or
broken event stream cannot undo input that the runtime already accepted. Rejected
submissions do not advance the cursor. Newly created native sessions still hydrate.
This does not mark an action or answer successful and does not replay mutations.

Claude Code already discards its cached native session after a failed turn; that
failure path is different and is unchanged. Supervisor progress text no longer
claims a native session reset that its recovery code did not perform.

## Observed incident evidence

Sources are the persisted Host checkpoint, Qwen daemon log, and native chat JSONL
for session `ba77493e-71da-434d-9946-909ea799b421`. Counts below are metadata, not
copies of user content or model reasoning.

- The checkpoint identifies `gpt-oss:20b` as supervisor, Qwen Code as harness, and
  `qwen3.8:27b-gpu0` as worker.
- Verification at 12:24:58 UTC sent 299,875 text characters. Its hydrated assistant
  history contained 239,179 characters and seven persisted-compaction markers.
- The provider reported 95,834 input tokens, 192 output tokens, and 178 thought
  tokens. The recorded assistant message contained only a thought part; the Host
  classified the visible answer as empty.
- Recovery at 12:25:59 UTC sent 300,369 text characters into the same native session.
  Its 239,179-character hydrated history was identical by ordinal string comparison.
  The two prompts totaled 600,244 characters.
- Native preflight subsequently estimated 171,119 tokens and failed compression
  with `COMPRESSION_FAILED_EMPTY_SUMMARY` at 12:28:22 UTC.
- The associated side-query telemetry reports HTTP 200, 88,401 input tokens,
  7,182 output tokens, and 6,999 thought tokens. These are different measurements
  from the 171,119-token native-context estimate; the compression side query uses
  a slimmed input.
- Current local `/api/show` confirms `gptoss.context_length = 131072`, thinking
  capability, and a custom parameter list containing only `temperature = 1`.
  No custom `stop` or `num_predict` parameter was present in that model definition.

## Why "empty summary" does not mean zero generated tokens

The installed Qwen Code compression service requests up to 20,000 output tokens.
Its thresholds reserve that amount plus a 3,000-token hard margin, producing
`131072 - 20000 - 3000 = 108072`. The automatic threshold with default settings is
98,072 tokens. This is not a smaller model context window.

`generateText` collects only text parts that are not marked as thoughts. The
compression service additionally removes closed or unfinished `<analysis>` blocks.
It returns `COMPRESSION_FAILED_EMPTY_SUMMARY` when no usable text remains. Thus
the recorded token counts are compatible with this status, and do not establish
that the provider returned an entirely empty response.

There is a concrete compatibility weakness: cold compression sets
`thinkingConfig.includeThoughts = false`; the installed OpenAI-compatible adapter
removes `reasoning` and non-`none` `reasoning_effort`. For GPT-OSS this leaves the
model's default reasoning behavior rather than disabling it. The compression
directive also asks for an explicit analysis block before the state snapshot.
GPT-OSS requires a reasoning level and cannot disable reasoning, as documented by
[Ollama](https://docs.ollama.com/capabilities/thinking).

The side-query's actual final text and finish reason were not retained in these
artifacts. We cannot distinguish a reasoning-only provider answer from all text
being removed during summary extraction, nor establish why generation stopped.
The 7,182 reported output tokens do not show exhaustion of the configured 20,000
summary cap. No new inference was run to reproduce this incident.

## Proposed follow-up, not implemented

Update: the user chose an AR-owned reactive failsafe instead of modifying Qwen
Code. See [Host context failsafe](host-context-failsafe-impact.md) for its scope,
implementation and validation. The native-summarizer changes below remain proposals;
the failsafe makes no additional model call to summarize and leaves the runtime intact.

Implementation boundary: the three summarizer changes below are internal to the
installed Qwen Code dependency, not AR's provider calls. Its current configuration
does not expose the compression directive, extraction diagnostics, or bounded empty
summary retry. Implementing them requires a maintained Qwen source change, or an
explicitly chosen alternative Host recovery path. Editing hashed npm bundle files
in place would not be a maintainable repository fix. No runtime was modified.

Further model-free investigation reproduced another extraction failure. With a
complete state snapshot following `<analysis>scratch</think>`, the installed
`stripAnalysisBlock` logic produces zero characters; the same input using the
matching `</analysis>` retains the 54-character snapshot. This is a synthetic
reproduction, not proof that the incident produced those tags. Upstream
[issue #11969](https://github.com/QwenLM/qwen-code/issues/11969) reports the same
mismatched-tag behavior. A correction should preserve a valid explicit snapshot
without treating unfinished reasoning as a successful summary. The issue's
suggested fail-open fallback to raw reasoning is not appropriate here.

Upstream [issue #9455](https://github.com/QwenLM/qwen-code/issues/9455) also describes
the missing complete-input admission check for same-model compression. This agrees
with the installed source inspected here, but does not establish that this
incident's 88,401-token compression input exceeded its 131,072-token window.

1. For compression, map reasoning capabilities correctly: use a supported low
   effort for models with mandatory reasoning rather than treating omitted controls
   as disabled reasoning. Ask for the final state snapshot directly instead of a
   second explicit analysis section. Keep normal chat Thinking behavior unchanged.
2. Record bounded compression diagnostics: finish reason, final-text length before
   and after extraction, reasoning-token count, effective output cap, and input
   estimate. Do not persist raw reasoning or sensitive prompt contents.
3. If the first result is unusable, propose one bounded retry with a smaller,
   fact-based input and an explicit final-output instruction. Retain the previous
   valid context until a usable summary is validated. Do not accept thought text
   as a summary or silently change the model. This recovery change requires its
   own implementation and E2E coverage.

## Should persisted compaction switch from bytes to tokens?

Not as a replacement for the storage policy. Persistence and inference have
different budgets. The current 10 MiB trigger / approximately 5 MiB target controls
stored operational data, while the lossless transcript preserves presentation.
Changing that trigger would not by itself prevent replay into native history.

`ConversationContextBuilder` already budgets selected history in estimated tokens,
using the effective model limit, fixed prompt content, and reserved response.
However, its calculation does not encompass the complete native resident context,
native tool schemas, and all later harness additions. Native harnesses apply
another budget, which is why these layers can disagree.

Recommended next step: retain the byte threshold for storage, and evaluate a
separate per-invocation token budget over the complete outgoing envelope plus
known resident context and harness reserves. Reuse the existing token estimator
and context state rather than adding a new activity or persistence system.
If a continuity summary itself exceeds its allocated budget, produce a bounded
operational projection while preserving the original transcript. Flattening nested
prior summaries is worth evaluating; seven markers alone do not prove all their
facts were duplicates. The incident's first call fit the native hard threshold;
the observed replay is sufficient to explain why recovery crossed it.

This investigation does not change storage thresholds, model settings, runtime
installation, user sessions, or the visible transcript.

## Validation

- Release build: zero warnings and errors. Used isolated output to preserve the
  running application: `dotnet build AgenticRouter.slnx -c Release --no-restore
  -m:1 -nr:false -p:BaseOutputPath=bin/context-recovery/`.
- `dotnet format AgenticRouter.slnx --no-restore --include` the seven changed C#
  files with `--verify-no-changes`: passed.
- Focused `dotnet test` with the isolated API and fake harness binaries:
  10/10 E2E cases passed. Coverage includes accepted Qwen empty/malformed output,
  OpenCode/Codex malformed events, same-session retry without repeated hydration,
  rejected Qwen queue submissions, supervisor empty-response/tool-loop recovery,
  canonical handoff deltas, and fresh-session hydration.
- Result: `tests/AgenticRouter.EndToEndTests/TestResults/context-recovery-final.trx`.
- `git diff --check` and intended-diff review passed. Test-started processes exited.
  The existing application was not restarted; real-provider reproduction was not
  performed. The sandbox blocked HttpListener/named pipes, so E2E and format checks
  ran outside that sandbox using only local simulated providers.
