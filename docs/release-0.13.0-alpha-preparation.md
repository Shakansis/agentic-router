# 0.13.0 alpha release preparation

## Version decision

The latest published prerelease is `v0.12.0_alpha` (14 September 2026); the
public release list and GitHub ref lookup showed no `v0.13.0_alpha` tag on
22 September 2026. The publisher's read-only preflight passed for that tag.
`0.13.0_alpha` is the next minor alpha because this candidate
adds user-visible custom Benchmark prompts, per-model GPU affinity, Chat
reattachment, paged history, Host context recovery, and runtime availability
controls in addition to resilience fixes. A `0.12.1_alpha` patch would understate
that scope. It is not a stable `1.0` release.

## Change inventory since 0.12.0 alpha

- The committed changes after source release commit `dc7f026` add Custom Prompt
  Benchmark mode and manual review, per-model GPU affinity and independent
  managed-server residency, more Qwen recovery, and clearer Benchmark results
  and local Help.
- The current working tree additionally contains Chat reconnection and paged
  lossless history, bounded context and session recovery, Execute/Supervisor and
  Benchmark failure controls, managed Ollama startup recovery, sidebar resource
  availability, and presentation/brand changes. Several of those files are
  shared with other active tasks; they are not yet a frozen source commit.
- `RELEASE_NOTES.md` is the detailed candidate note. The source and public
  READMEs now point to 0.13 assets for the authorized publication sequence;
  those links become live only after the new GitHub release is created.
- `AgenticRouter.Api/data/` is ignored as a whole. Three formerly tracked
  runtime JSON files (Benchmark scoring profile, model organization, pending
  user input) were removed from the Git index without deleting local copies.
  The developer-specific `Properties/launchSettings.json` was likewise
  untracked because it contained a local LAN URL. No runtime data path may be
  force-added to the release commit. This prevents future tracking;
  historical copies in older Git commits are unchanged.

## Release gate

The isolated Release build passed with zero warnings and errors. The complete
browser/API E2E suite passed 565/565 with the 0.13 version stamp after a
Benchmark test-history race was corrected (TRX:
`.artifacts/release-0.13.0-alpha/test-results/release013-full-after-benchmark-fix.trx`). Scoped
formatting, PowerShell parsing, JavaScript syntax, and diff checks passed.

Both portable candidate archives passed package inspection and independent
SHA-256 verification. The Windows package started in Production and returned
HTTP 200 from the page and settings API with file version `0.13.0.0`. The
Linux package did the same in WSL Ubuntu, with executable mode 755 for its
binary and launcher. The new brand image is present in both archives. Evidence
is under `.artifacts/release-0.13.0-alpha/`:

- Windows ZIP: `0f58f49977c63370f4cf6784985b7dbcf459ade893f697f898d40478d587286a`;
- Linux tar.gz: `78f01f783b8cd9a724da6b636f4a3e9d5f9f4e381f3332116477a9881d309bbe`.

Real model/harness tests and deterministic fault injection are recorded
separately in `docs/resilience-controls.md`. WSL smoke does not prove physical
Linux hardware compatibility. Source must be frozen and committed before the
final package build so embedded source metadata matches the published revision;
the current packages are candidates built from a dirty worktree and report
source revision `7e69123` in Windows product metadata.

The Benchmark screenshot was replaced with a browser capture of the 0.13
Custom Prompt controls. The other illustrations retain their described flows;
the isolated fresh setup did not enable Execute without configured runtime
settings, so the strategy illustration was not regenerated from a disabled
demo state. This visual review is separate from automated validation.

Publication is authorized only after the final full E2E gate passes. The
sequence is source commit and push, rebuild/verify archives from that commit,
then public documentation copy and immutable GitHub prerelease creation. The
GitHub script rejects an existing version tag and accepts only the reviewed
public-file allowlist.
