# Plan v57 - browser message buffer and harness steering

Status: implemented; real Codex/Qwen Code browser acceptance completed on
2026-08-26. OpenCode integration and shared floating tooltips added on 2026-10-02;
OpenCode validation uses deterministic browser/API tests, not real inference.

## Objective

Allow the user to compose a sequence of follow-up prompts while a response is
running without introducing persistent queue infrastructure. Connect true
same-turn steering only where the reviewed harness protocol supports it.

## Implemented contract

1. The browser owns an in-memory FIFO queue shared by Chat and Execute routes.
2. During an active response, the ordinary Send button adds the current draft
   to the queue. Send never becomes Cancel; cancellation is a detached circular
   control in the upper-right of the composer.
3. Queue items expose rounded icon actions in the order Edit, Delete, Steer.
   Editing opens a textarea at the item's current position. A draft survives UI
   refreshes and no item auto-submits while any queue item is being edited.
4. The next ready prompt uses the ordinary chat stream after the prior response
   reaches terminal state. User cancellation pauses the queue and exposes an
   explicit `Run next` action.
5. Queue state is cleared on page reload, workspace/conversation reset, or new
   conversation and is never sent to persistence APIs.
6. Steer is available only from an already queued item. Codex steering uses
   `turn/steer` with `expectedTurnId`, reads the protocol's returned `turnId`,
   and validates that it is the same active turn. The isolated Codex config opts
   into `features.instant_interrupt`; the request forwards `clientUserMessageId`.
   Admission is serialized and accepted IDs are idempotent within the active
   turn; conflicting payloads and completed turns are rejected.
7. Qwen Code steering requires the daemon
   `session_mid_turn_message_mutation` capability and posts an idempotent
   message ID to the active session's `mid-turn-message` endpoint. The returned
   ID must match. The Host waits for the reconciliation ring to confirm injection
   instead of treating daemon admission as consumption. A late promotion,
   missing receipt, terminal race, or timeout is surfaced and undrained ownership
   is released where the daemon permits it. Failed steering pauses the browser
   queue; promotion is never reported as successful same-turn steering.
8. The Host accepts steering only for a registered adapter that advertises the
   typed steering capability and still owns the requested active conversation.
9. OpenCode steering posts `noReply: true` to the active legacy session's
   synchronous `/session/{id}/message` endpoint, preserving model, variant,
   agent, and native permission rules. This appends context without starting
   another native loop. Validate the user-message receipt and native busy state;
   if the turn ended, accept only a message consumed by an assistant step or
   delete the unconsumed admission and reject it. Serialize admission against
   Host turn cleanup and deduplicate message IDs within the active turn.
10. UI eligibility comes from registered harness capabilities. Claude Code's
    sequential streaming input and Native's current Host loop have no integrated
    same-turn steering transport; both retain the follow-up queue. Disabled
    controls keep a keyboard-focusable wrapper with an explanatory tooltip.
    Queue tooltips use the shared manual-popover installer and browser top layer
    to escape the scrolling list's clipping, with bounded viewport positioning.
11. Context usage keeps its existing interactive details contract but renders
    as backgroundless text above and outside the prompt panel, right-aligned to
    the composer.

## Deterministic evidence

- JavaScript syntax validation.
- Zero-warning Debug solution build.
- Browser E2E for Send-driven queue creation, detached cancellation, inline
  editing, draft preservation across terminal UI refresh, and automatic
  dispatch only after Save.
- Browser/fake-App-Server E2E for exact Codex active-turn steering.
- Browser/fake-daemon E2E for Qwen mid-turn submission and client identity.
- OpenCode E2E for same-session/no-new-loop admission, user-part suppression,
  idempotence, stale-session rejection, and removal of unconsumed admissions.
- Shared inactivity recovery E2E for Codex and OpenCode, without extra turns.
- Capability/API coverage for Native and Claude Code's unsupported transport.
- Desktop/mobile tooltip coverage for hover, focus, Escape, actual painting
  outside the scroll list, and preservation of Settings tooltips.
- Browser layout E2E proving context usage is outside and above the composer,
  plus hover/focusable tooltip coverage for unsupported steering harnesses.

## Real evidence

- Codex with `qwen3.8:27b-gpu0`: Send queued a supplemental prompt and the
  queued Steer action was accepted by the exact active App Server turn.
- Qwen Code with `qwen3.8:27b-gpu0`: Send queued a supplemental prompt and the
  daemon accepted and reconciled it through `mid-turn-message`.
- The detached Cancel control terminated both validation turns. All temporary
  Host/harness processes and isolated validation data were removed afterward.

## 2026-10-02 validation

- Reviewed the installed OpenCode 1.18.18 legacy protocol in
  [SessionPrompt](https://github.com/anomalyco/opencode/blob/v1.18.18/packages/opencode/src/session/prompt.ts)
  and its synchronous session HTTP handler. `noReply` returns the persisted user
  message before entering `loop`; the existing loop rereads session history.
- Release solution build: zero errors and warnings, isolated output
  `bin/steer-tooltip/Release/`; user-owned application processes were not restarted.
- Scoped `dotnet format whitespace --verify-no-changes`, JavaScript syntax
  checks for `app.js`, `conversation.js`, and `i18n.js`, and `git diff --check` passed.
- Focused browser/API suite: 14/14. Broader applicable regression suite: 71/71,
  including all 16 session-isolation cases, early-write/output recovery, OpenCode
  transport, message buffering, Settings, and steering. The focused cases are
  included in the broader total, not additional cases.
- Reports: `steering-tooltip-final-focused.trx` and
  `steering-tooltip-broad-regression.trx` under the E2E project's `TestResults`.
- These are deterministic external-boundary fixtures. No real OpenCode/model
  inference was executed for this extension; the older real Codex/Qwen evidence
  above is not OpenCode acceptance evidence.

No cloud provider was used for this evidence.

## 2026-10-09 audit

See [steering and release audit](research/harness-steering-and-releases-2026-10-09.md)
for installed versions, upstream feature assessment and current validation.
Shared inactivity recovery continues consuming stream events while a steering
transport confirms delivery; the pending request is cancelled and observed when
the stream ends. This avoids waiting for consumption while blocking the tool
events a harness needs before it can consume the message.
