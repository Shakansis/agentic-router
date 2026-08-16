# PLAN v1: Fork game with existing fireworks assets

## Goal

Make the exact user request complete in Execute mode with `Approve for me` while preserving Host authority:

> Create a fork game, generate a collection of 256 fixed substantive words to be used as words for the game. The code will need to be created using html, vanilla js and CSS only. It does not use Node and does not executes, it will test it manually. And integrate the fireworks effects that is in on fireworks folder into the code, to be triggered when the game ends.

The conventional interpretation of the slightly malformed phrase `fork game` is a browser word-guessing game because the request also requires a fixed word collection. The final Execute result must keep that assumption visible.

This is the authoritative functional acceptance target after the common native tool-calling core and the Qwen Code tooling profile are complete. Deterministic E2E selects exact `qwen3-coder:30b` so the scenario exercises `qwen-code-ollama@1`; the subsequent permission-gated real run uses the same exact request and model before any other model-family protocol work begins.

## Acceptance contract

1. Execute uses exact selected specialist `qwen3-coder:30b` directly through `qwen-code-ollama@1`; the resident router does not plan or supervise the work.
2. The Host omits process and validation tools because the user reserved testing for manual review and explicitly excluded Node/execution.
3. The specialist lists and reads the existing `fireworks` folder before integration, preserves its files, and uses the observed paths and public API.
4. The output is plain HTML, CSS, and vanilla JavaScript with no package manifest, dependency installation, server, or generated wrapper directory.
5. The game contains exactly 256 fixed, unique substantive words and implements a real letter-guess loop with progressive reveal, invalid-guess handling, and explicit win/loss endings.
6. The observed fireworks API is invoked when the game reaches a terminal state.
7. The specialist performs bounded static review of cross-file references, the word count, and game behavior before completing.
8. Deterministic fake-provider E2E and an explicitly authorized real Ollama run are reported as separate evidence.

## Ordered implementation

1. Strengthen generic specialist instructions for inspecting referenced existing assets and reviewing explicit request constraints without adding a new orchestration layer.
2. Add a deterministic fake Ollama scenario for the exact request and a substantive 256-word fixture.
3. Add browser/API E2E coverage for Host tool scope, inspection-before-integration, generated artifacts, exact word invariants, terminal fireworks behavior, and absence of process execution.
4. Run focused validation, formatter verification, Release build, full E2E, and diff checks.
5. Run the exact request against real Ollama from the `AgenticRouter.Api` content root and inspect the actual workspace artifacts.

## Non-goals

- Do not add Node.js, npm, a frontend framework, or a generic shell.
- Do not execute the generated game or replace the user's manual acceptance test.
- Do not modify, duplicate, or synthesize the supplied fireworks implementation.
- Do not add resident coordination, recursive delegation, or unbounded retries.
- Do not add production branches, parsers, prompts, or completion checks keyed to `fork`, `fireworks`, `256`, word collections, or any other acceptance-fixture wording. Scenario-specific invariants belong only in the E2E test.
- Recovery changes must apply to any typed malformed or unavailable proposal and remain bounded and observable.
