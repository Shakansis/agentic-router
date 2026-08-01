# Execute effect authority and lightweight action model

## Decision

The existing closed tool-name registry and every reviewed alias remain intact.
The correction is deliberately downstream of name resolution:

`alias -> canonical tool -> execution -> proven effect -> plan advancement`

Plan IDs remain Host-generated. The Host normalizes model-provided titles into
typed effects such as inspection, file creation, file modification, deletion,
directory creation, validation, process execution, and Git mutation. Explicitly
generic titles may acquire a type from the first canonical action bound to that
step; already typed steps never accept an incompatible action.

## Completion authority

A completed tool transport is not proof of work. File mutations require verified
filesystem records, directory creation requires an observed created directory,
and validation requires a passing validation record. A mutation objective with
no verified mutation is terminally blocked. The Host generates the final Execute
answer from the stored review and does not ask a model to restate those facts.

## Structured deletion

`delete_files` accepts only an explicit bounded path array. Every file must exist
and remain inside the trusted workspace; the Host independently inspects and
hashes it during validation. Protected instruction files require exact mention
in the user objective. Accepted text or binary files must fit the rollback
budget; otherwise validation rejects the whole batch. The pending list remains
editable until approval. Approve atomically revalidates the final list, verifies
every path is absent afterward, records one review entry per file, restores a
partially executed batch on error, and supports session undo.

## Inline approval revision

Pending process commands and structured file arguments are edited directly in
the approval card. The browser sends the current text with Approve; there is no
intermediate Save or Update state. The Host preserves the canonical tool and
action identity, reparses the edited surface, reruns ordinary validation, and
only then completes the decision. A rejected edit executes nothing and leaves
the approval available for another correction or explicit rejection.

## Model roles

- `routerModel`: lightweight intent classification only.
- `actionModel`: lightweight resident action selection and tool-result loop;
  default `functiongemma:270m`.
- `coordinatorModel`: on-demand fallback when the action model is unavailable or
  fails the approved path; it is not permanently resident.
- intention model: expert Chat response or specialist guidance.

FunctionGemma is used as supplied for now. Fine-tuning and a real Ollama
conformance benchmark remain separate follow-up work and require explicit GPU
availability approval before execution.
