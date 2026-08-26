# Plan v57 - browser message buffer and harness steering

Status: implemented; deterministic automated validation and real Codex/Qwen
Code browser acceptance completed on 2026-08-26.

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
   and validates that it is the same active turn.
7. Qwen Code steering requires the daemon
   `session_mid_turn_message_mutation` capability and posts an idempotent
   message ID to the active session's `mid-turn-message` endpoint.
8. The Host accepts steering only for a registered adapter that advertises the
   typed steering capability and still owns the requested active conversation.
9. OpenCode, Claude Code, and Native do not implement steering. The visible
   disabled control has a wrapper-owned tooltip that names the selected harness
   and states that steering is available only for Codex and Qwen Code. The
   wrapper remains keyboard-focusable even though the action is disabled;
   queueing remains available for all routes.
10. Context usage keeps its existing interactive details contract but renders
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
- Capability/UI E2E proving OpenCode and Claude Code remain queue-only.
- Browser layout E2E proving context usage is outside and above the composer,
  plus hover/focusable tooltip coverage for unsupported steering harnesses.

## Real evidence

- Codex with `qwen3.8:27b-gpu0`: Send queued a supplemental prompt and the
  queued Steer action was accepted by the exact active App Server turn.
- Qwen Code with `qwen3.8:27b-gpu0`: Send queued a supplemental prompt and the
  daemon accepted and reconciled it through `mid-turn-message`.
- The detached Cancel control terminated both validation turns. All temporary
  Host/harness processes and isolated validation data were removed afterward.

No cloud provider was used for this evidence.
