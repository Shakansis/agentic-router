# PLAN v4: Execute recovery budget

## Goal

Let the active specialist recover from more bounded planning and execution failures before the Host requests a user recovery decision.

## Scope

- Raise consecutive direct-planning attempts from 2/4 to 5.
- Allow up to 5 unchanged specialist strategy responses before rejecting that recovery path.
- Raise automatic recovery attempts per turn from 5 to 10.
- Preserve tool-call, elapsed-time, approval, policy, security, and consecutive tool-failure limits.
- Keep persisted settings, defaults, migration fallback, and deterministic test settings aligned.

## Ordered work

1. Update the recovery defaults and the checked-in local settings profile.
2. Expand the bounded unchanged-strategy retry loop without accepting duplicate guidance.
3. Update deterministic test expectations.
4. Run formatting, build, focused fake-provider E2E validation, and diff checks.
