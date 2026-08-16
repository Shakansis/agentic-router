# Execute effect authority and lightweight action model

> Historical design note: the resident-supervisor and mandatory-plan portions of this document are superseded by [Local-first specialist runtime architecture](local-first-specialist-runtime-architecture.md). The current live path is `selected specialist -> Host runtime -> environment`; plans and resident translation are not ordinary execution gates.

## Decision

The existing closed tool-name registry and every reviewed alias remain intact.
The correction is deliberately downstream of name resolution:

`alias -> canonical tool -> execution -> proven effect`

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

`functiongemma:*` is integrated as the resident supervisor through the stable
`ollama-tooling-lab-schema-5|evaluator-prompt-4` application contract. It is not
an executor for the generic local action planner:

- `route_to_teacher` chooses one exact model/intent pair from the Host-offered
  Teacher catalog;
- `explain_teacher_trace` supplies only a bounded diagnostic reason for the
  Host-owned comparison facts and diagnosis;
- `recover_teacher_trace` must copy the Host-owned action, failure code, failed
  step and next tool, while its reason remains diagnostic.

The activation developer message is preserved exactly as trained. Instructions,
catalogs, comparison facts and required policies are user-message payloads. The
Host accepts exactly one native call with complete typed arguments. FunctionGemma
never receives `LOCAL_ACTION_PLANNER_V1`; an approved Teacher coordinates
directly or the separately configured on-demand `coordinatorModel` performs the
generic local actions. Future model generations and publication gates belong to
the tooling lab, while this Agentic Router contract remains stable.

The application mirrors the lab evaluator's boundary normalization: surrounding
whitespace is trimmed from required string arguments before exact catalog and
policy comparison. Internal spelling is never guessed or fuzzy-matched. An exact
offered Teacher with a cross-paired or unknown intent is repaired to the single
intent owned by that Teacher in the Host catalog and reported in activity. Other
typed routing failures receive one materially different correction attempt; if
that also fails, Execute continues with the configured specialist and exposes the
sanitized rejection reason. During Execute, a FunctionGemma configured as
`routerModel` bypasses the incompatible generic intention parser and uses
`route_to_teacher`. A FunctionGemma configured as the coordinator fallback remains
supervisory; the Host visibly substitutes a distinct installed Teacher for that
turn instead of sending generic action tools to the backup resident.

## Delivery-first recovery

A rejected proposal does not end the user objective by itself. The Host returns
bounded correction facts for malformed tool calls, invalid argument shapes,
stale state, and other recoverable defects, then asks for a materially different
proposal or changes coordination path. A resident native-protocol failure gets
one corrected retry before a user checkpoint; an independently selected target
may hand off immediately so the retry is not identical.

The Host derives a closed request-specific tool scope from the latest user
objective and authoritative project profile. File inspection and editing remain
the default surface. Process execution, validation profiles, Git operations,
deletion, and explicit empty-directory creation are offered only when the request
or saved Host configuration makes them relevant. Explicit manual-testing or
no-execution language removes process and validation tools even when words such
as "test" also appear. Plans containing effects with no compatible offered tool
are rejected before the first action. FunctionGemma evaluation is reserved for
an exhausted Host semantic correction or a real coordination-path change; it is
not inserted into every ordinary correctable proposal failure.

For a detected `vanilla-web` project, project context states that the trusted
workspace is already the project root and that HTML, CSS, and browser JavaScript
do not imply Node, npm, a build pipeline, or a development server. `create_file`
creates required parent directories, so an additional directory action is not
needed merely to prepare a file path.

The trusted workspace remains absolute authority. The Host never executes an
external target. When `create_file` or `create_directory` contains excess
relative parent traversal, the Host may clamp the creation to the trusted root,
replace the action argument with the effective relative path, and expose the
original/effective values in activity and review warnings. Absolute paths and
read, edit, delete, process, and Git targets are never silently rebased.
