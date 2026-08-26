# PLAN v50 - Host plan-step feedback

## Objective

Prevent external harnesses from losing the exact Host-owned `stepId` state
after an accepted plan advances, without relaxing plan binding or inferring a
step on the specialist's behalf.

## Observed failure

- Trace `0HNO2AL015SRP:00000319` ran through Codex Experimental and accepted an
  eight-step Host plan.
- The Host correctly rejected missing and terminal `stepId` values, but ordinary
  Host-bridge results did not return the updated plan state or the exact IDs
  still eligible for the next action.
- The specialist retried with completed IDs, attempted an unavailable plan
  replacement/revision path, exhausted the bounded failure budget, and failed
  after verified workspace changes already existed.

## Scope

- Keep exact plan-step binding mandatory while an accepted plan exists.
- Append compact authoritative plan state and currently actionable `stepId`
  values to every external Host-bridge plan, success, rejection, and failure
  result.
- When no actionable step remains, explicitly direct the harness to return its
  final answer if the objective is complete or revise the existing plan if more
  work is genuinely required.
- Add deterministic OpenCode and Qwen Code coverage that advances a two-step
  plan and verifies the returned `stepId` state after each Host action.
- Do not run a real model, GPU workload, cloud request, or user-workspace test.

## Validation

- Run the focused external Host-bridge plan regression.
- Run an isolated Release build, formatting verification, relevant deterministic
  E2E, and `git diff --check`.
- Preserve the user-started application and remove all test/build residuals.

## Completion evidence

- Isolated Release build passed with zero warnings and zero errors.
- The exact missing-`stepId` recovery passed for OpenCode and Qwen Code: the
  Host rejected the unbound action, returned `step-1`, advanced to `step-2`,
  then reported no remaining actionable IDs.
- Focused Host-bridge regressions passed 11/11.
- Formatting verification and `git diff --check` passed.
- Full deterministic E2E passed: 312 passed, 0 failed, 0 skipped.
- No real model, GPU workload, cloud request, user-workspace execution, or app
  restart was used.
- Post-validation cleanup removed the isolated v50 artifacts and all test/build
  processes. The user-started Release application, PID 43200, was preserved.
