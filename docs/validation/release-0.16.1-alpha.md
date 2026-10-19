# Agentic Router 0.16.1 alpha real acceptance

Date: 2026-10-19. Follow-up to the
[0.16.0 release validation](release-0.16.0-alpha.md), after explicit user
authorization to execute all 13 real-model/history-dependent cases.

## Isolation and actual provider

The user closed the existing AR. Acceptance uses a separate data directory,
tracked fixture workspace, API executable and managed Ollama endpoint. The
pre-existing external Ollama service remains running and had no resident model.
No model, CLI or runtime was downloaded or installed. User settings and histories
were not copied or modified.

The installed model is `qwen3.8:27b-q4_K_M`, digest
`25b843619e944cd0ae6069f94ff4e5e26a16e109ccbc0a66a0f05979ed70098e`,
with a 32,768-token context on an NVIDIA RTX 4090. Ollama reports version 0.34.2.
Installed harnesses: Codex `0.163.0-alpha.10.2`, Claude Code `2.1.295`,
OpenCode `1.18.35`, Qwen Code `0.25.0`. Real inference is serialized on the
task-owned endpoint; deterministic and installed-CLI contract tests use fake
providers in separate processes.

Local evidence is under `.artifacts/release-0.16.0-alpha/real-20261019/`.
It contains TRX files, bounded diagnostics and synthetic fixture artifacts;
these ignored files are excluded from portable archives and publication.

## Failure reproduced and corrected

The published 0.16.0 Windows executable passed 12 of the 13 cases. Codex accepted
the native steering request in 39 ms, but the model continued listing numbers
instead of producing `STEERING_APPLIED_7319`. The instruction was present in
the native conversation, so receipt alone was insufficient acceptance evidence.

Ollama's [version-pinned Responses conversion](https://github.com/ollama/ollama/blob/v0.34.2/openai/responses.go#L533-L677)
holds `encrypted_content` reasoning until a later assistant item. When native
steering interrupts reasoning and appends a user message, this conversion appends
the pending reasoning after that newer user message. It becomes stale assistant
prefill. A short direct synthetic request reproduced the numeric continuation;
the same request with an empty assistant message binding the interrupted
reasoning before the user instruction returned the exact requested marker.

AR now adds that binding only when pending reasoning would cross a subsequent
non-assistant input on the local Responses transport. It preserves reasoning
content, user instructions, tool IDs/results and terminal reasoning prefill.
The existing native `/api/chat` translation retains its own ordered messages.
Approval, trusted-workspace authority, steering receipts and output authority
are unchanged. There are no new retries, altered prompts or relaxed assertions.

The new browser/API regression fails against the original published executable
and passes against the correction. It checks two interruptions, a normal
reasoning/answer pair, a correlated tool call/result and a trailing prefill at
the external Ollama boundary. The real Codex and OpenCode generation-steering
cases then both passed against the corrected API.

## Test corrections and intermediate failures

A corrected-transport rerun exposed a separate model refusal: Codex stopped the
old task and understood the new instruction, but interpreted the literal
`STEERING_APPLIED_7319` as a status claim it should not certify. The fixture now
requests the neutral literal `BLUE_PEBBLE_7319`. It still requires output from
the original task before steering, HTTP 200 plus the native receipt, the exact
new literal in the answer and a terminal turn. The Qwen promotion case still
requires HTTP 409, a retained draft, a paused queue and absence of the literal.
No assertion or timeout was relaxed. Three predeclared generation-steering
rounds passed for both Codex and OpenCode with the neutral fixture.

The intermediate deterministic run exposed a cleanup race in
`FileMutationIsOneExpandableUserFacingAction`: an optional Cancel click raced
natural completion after closing review. Its file/activity/review checks remain
intact; it now additionally requires terminal activity and a hidden Cancel
button. The intermediate run was stopped and its owned process tree removed;
the final complete suite starts again from the frozen source.

An intermediate installed-CLI run passed 31/32. In Qwen's native-options case,
three inference requests supplied final usage, but a fourth was cancelled before
usage; the Host correctly retained unavailable aggregate timing rather than
inventing values. The test's strict timing assertion failed. All eight focused
native-options cases subsequently passed unchanged. Final complete installed-CLI
results are listed below; the intermediate cancellation remains in the local
evidence and does not establish that native SDK cancellation can never recur.

## Acceptance gates

All **13/13 real-model/history-dependent cases passed**, with zero failures or
skips, against the executable extracted from the final 0.16.1 Windows ZIP.

| Real gate | Cases passed | TRX under the local evidence root |
| --- | ---: | --- |
| Cold model loading, prompt progress and post-load TTFT | 1/1 | `TestResults/package-cold-progress.trx` |
| Completion metrics and responsive footer on all five harnesses | 5/5 | `TestResults/package-completion-metrics.trx` |
| Replay of the five persisted harness footers | 1/1 | `TestResults/package-metrics-replay.trx` |
| Codex/OpenCode generation steering, Qwen tool-boundary steering and rejected promotion | 4/4 | `TestResults/package-steering.trx` |
| Native single/sequential/parallel/batch-read benchmark | 1/1 | `TestResults/package-native-benchmark.trx` |
| Storytelling review, explicit Apply, Undo, synonyms and export with the resident model | 1/1 | `TestResults/package-story.trx` |

The Native case completed all 12 samples: three rotated repetitions of four
modes, verified reads, correct source explanations and no tool errors. Single
calls used four inferences; each multi-call or batch-read variant used two.
Observed peak concurrency in the real parallel variant was one. These small
cached reads do not establish real overlap or a general parallel speedup.

Storytelling kept the same resident model digest and 32,768-token context before
and after, without authorizing an automatic load. The test checked unchanged
draft before Apply, exact application, Undo, structured synonym alternatives,
rendered view and exact exported Markdown.

Installed CLI integration passed **32/32**, zero failures/skips, in 6.7070
minutes (`TestResults/installed-final0161.trx`). The focused native-options
rerun passed **8/8** (`TestResults/installed-options-final.trx`). These focused
cases overlap the 32-case suite and are not additional distinct tests.

The Release solution build completed with zero warnings/errors. Whole-solution
`dotnet format --verify-no-changes --no-restore`, syntax checks for 18 JavaScript
and five release scripts, and `git diff --check` passed. Build and formatter
logs are `build-final.log` and `format-final.log`.

The final complete deterministic suite passed **885/885**, zero failures/skips,
in 27.2059 minutes (`TestResults/full-final0161.trx`). The focused final-build
regressions passed **3/3** (`TestResults/focused-final0161.trx`), and the same
three browser/API cases passed against the extracted Windows package
(`TestResults/package-focused0161.trx`). Focused checks overlap the full suite.
The complete distinct matrix is **930/930**: 885 deterministic, 32 installed-CLI
and 13 real-model/history cases. Intermediate failures above remain recorded
separately from this final acceptance.

## Frozen source and portable artifacts

Application and test source are frozen at
`8fd53a8ce733148ac52f3f4d557fb5cabeadc5bf`. Subsequent changes are documentation
only. The Windows product version reports
`0.16.1_alpha+8fd53a8ce733148ac52f3f4d557fb5cabeadc5bf`.

| Archive | Bytes | SHA-256 |
| --- | ---: | --- |
| `AgenticRouter-0.16.1_alpha-win-x64.zip` | 122980255 | `2057ef8426069266e9c37a35b97107a8a345b534aaf30110d846570f3e1f34da` |
| `AgenticRouter-0.16.1_alpha-linux-x64.tar.gz` | 141836542 | `43b9a33e0de278165ef608419fe7b72182002bdbf17b56865ea63fcf77f02a71` |

Both archives passed checksum verification, extraction, forbidden-content
inspection and startup from another working directory with isolated data.
Root HTML and settings API returned HTTP 200 on Windows and Ubuntu/WSL. Linux
launcher, application and bundled Playwright node are executable. All 36 static
files match source in both packages. The Windows archive additionally passed
three focused browser/API cases: reasoning order, review termination and version
discovery. No test, fake-provider, secret, local data or raw diagnostic is shipped.

## Validation limits

The real acceptance matrix covers this installed model and these exact harness
versions on Windows/NVIDIA. It does not establish behavior for every model,
sampling outcome, SDK version or GPU. Linux acceptance is startup/API smoke in
WSL, not a physical Linux GPU/browser matrix. Installed-CLI contracts use an
external fake provider; they are reported separately from real inference.
Test counts do not imply a percentage of source-code line coverage.

## Real runtime cleanup

After all 13 real cases completed, the task-owned API, managed Ollama, runner
and native CLI process tree were stopped. Their API/runtime ports have no
remaining listener. External Ollama PID 24372 retains its original executable
and creation time, with zero resident models as before. The two pre-existing
Codex processes remain present. Evidence: `cleanup-real.json`.

After the complete deterministic suite, the captured test-process tree and all
release-artifact processes were absent, as were the known test API/runtime
listeners. No test/build server remained. Evidence: `cleanup-all.json`.

## Publication status

The 0.16.1 packages and documentation are prepared and validated locally.
Publication has not been performed; authorization for the source push and public
release upload is pending. Existing 0.16.0 assets remain unchanged.
