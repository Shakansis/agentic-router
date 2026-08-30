# Real acceptance - 2026-08-30

## Scope and environment

- Product: current Release checkout, isolated data directory and disposable
  trusted workspace.
- Local provider/model: Ollama Local, exact tag `qwen3.8:27b-gpu0`, digest
  `d1f9a27632f9cab927948254285394838a1f0e0f8b7e70e53d633624ed9e9169`.
- Harnesses: Native `built-in`, Codex `0.151.0-alpha.7.1`, Claude Code
  `2.1.234`, OpenCode `1.18.18`, and Qwen Code `0.21.13`.
- Cloud scope: Groq only. The catalog was refreshed live at
  `2026-08-30T11:52:26-03:00`; Google AI Studio and Cerebras remained disabled.
- Browser: the production UI was loaded through the in-app browser after the
  Release Host was started with the correct content root.

Deterministic, real local-model, real Web, and real cloud evidence are kept
separate below. A terminal harness event was not accepted without the observed
Host effect.

## Chat, Web, and Groq

| Route | Result | Evidence |
| --- | --- | --- |
| Local Chat | PASS | UI returned exactly `CHAT LOCAL OK` in 1.8 s on `qwen3.8:27b-gpu0`; trace `0HNO6KOJ9MPM6:0000000F`. |
| Automatic Web | PASS | UI exposed Web automatically, the model called the configured Ollama Web Search path, returned the official .NET 10 download title, five sources, and an HTTPS citation; 6.8 s; trace `0HNO6KOJ9MPM3:0000000E`. |
| Groq Chat, first run | FAILED and diagnosed | Live `groq::openai/gpt-oss-120b` rejected a null `tool_calls` property on the first system message; trace `0HNO6KOJ9MPM2:00000021`. No fallback was used. |
| Groq Chat, corrected run | PASS | OpenAI-compatible serialization now omits null properties. The same exact Groq route returned exactly `GROQ OK` in 763 ms; trace `0HNO6KRDTMVNA:0000000B`. Successful UI output exposed no investigation button. |

The Groq correction is covered by a fake-provider regression that parses the
outbound JSON and rejects `tool_calls` or `tool_call_id` on incompatible
messages. The focused regression passed 1/1 before the live retry.

## Five-harness local matrix

| Harness | Request/effect | Host result | Trace |
| --- | --- | --- | --- |
| Native | Create and reread `native-check.txt` with exact bytes `native=ok`. | PASS: one verified creation, 9 bytes, direct-native terminal completion. | `0HNO6KM17SOG4:00000001` |
| Codex | Change only `mode=old` to `mode=codex` in the supplied fixture and preserve the remaining bytes. | PASS: one verified modified file, no additional file, `codex-experimental` terminal completion. The fixture had been prepared without a line break; Codex preserved that pre-existing byte layout and changed only the requested substring. | `0HNO6KM17SOG5:00000001` |
| Claude Code | Create and reread `claude-notes.md` with heading `Real acceptance` and bullet `harness=claude-code`. | PASS: one verified Markdown creation, 41 bytes, `claude-code-experimental` terminal completion. | `0HNO6KM17SOG6:00000001` |
| OpenCode | Create and reread `opencode-config.json` with `{"harness":"opencode"}` semantics. | PASS: one verified JSON creation, 28 bytes, 4 recorded actions, 13.970 s. | `0HNO6KRDTMVNE:00000001` |
| Qwen Code | Change only `mode=codex` to `mode=qwen-code` and preserve `external=preserve`. | PASS: one verified modification, 33 bytes, 5 recorded actions, 43.952 s. | `0HNO6KRDTMVNF:00000001` |

Final independently observed artifact hashes:

| File | SHA-256 |
| --- | --- |
| `native-check.txt` | `0AAD872B0DFC9C420730BA06FDEEFEA87648017F43BC8F14F48ADE2930709578` |
| `claude-notes.md` | `0BE3274875DEBE1A1082809F6889DC178C786A3F23684167931E21B242D25DD0` |
| `opencode-config.json` | `419AED84D4D01D56E50A8B55FA51253355A99BD8476CB32F3867E1EBA7BFDF0F` |
| `existing.txt` after Qwen Code | `D490B85E3BE328794360DD1BF89A3B98EB8D2C3A02B41F8774E07863FDD37269` |

All five routes kept the exact selected local model. No resident model,
cross-harness takeover, cloud fallback, or silent model substitution was
observed. Validation profiles were intentionally absent in the disposable
workspace, so mutation sessions truthfully ended
`implemented-validation-not-configured`; filesystem/effect verification still
passed.

## Final deterministic validation

- Focused Groq outbound-contract regression: 1 passed, 0 failed, 0 skipped.
- Complete suite after the real-found Groq fix:
  `cleanup-final-after-groq.trx` - 360 passed, 0 failed, 0 skipped in 4m29s.
- Release build: zero warnings and zero errors.
- `dotnet format --verify-no-changes`, JavaScript syntax, and `git diff --check`:
  passed.

## Limits

- This is a bounded smoke/acceptance matrix, not proof that every prompt will
  converge on every model/harness pair.
- The disposable workspace had no validation command profile; exact Host file
  effects and hashes were the acceptance authority.
- Historical benchmark results remain separate and are not rewritten by this
  run.
