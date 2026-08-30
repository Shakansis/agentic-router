# PLAN v61 - Trace diagnostic self-investigation

## Objective

Keep the canonical Host trace visible beside every completed or failed answer and
let the user explicitly ask Agentic Router to investigate an error through one
button, without exposing the incident directory or starting an automatic retry.

## Scope and decisions

- Add a typed diagnostic reference containing the canonical trace identifier,
  terminal state, and confirmed journal-persistence state to terminal stream
  events and persisted assistant messages.
- Render the full trace identifier in the terminal summary for completed and
  failed turns. Successful turns expose no investigation action.
- Show one `Investigate error` action only for a typed failed turn whose
  diagnostic was confirmed persisted.
- The action dispatches a hidden, Host-authored Chat turn immediately. It does
  not fill the composer, change the visible interaction-mode selection, retry
  the failed objective, or change routing and settings.
- Add the canonical `get_trace_diagnostic` read-only capability. It accepts one
  exact trace identifier and returns only the existing bounded, sanitized
  `IncidentTraceReport` contract through `IIncidentJournal.FindTraceAsync`.
- Never accept a filesystem path, enumerate traces, search approximately, read
  raw application logs, or return prompts, responses, file contents, tool
  arguments, provider payloads, process output, stack traces, or secrets.
- Persist the hidden investigation request as conversation context but omit it
  from the rendered timeline. Persist the visible investigation answer normally.
- Treat the diagnostic report as Host evidence. The specialist must distinguish
  observed facts from inference and may recommend a materially different next
  attempt, but it cannot execute that attempt as part of investigation.

## Implementation steps

1. Extend stream and conversation-message contracts with a typed diagnostic
   reference and hidden-message presentation metadata.
2. Decorate terminal events after the incident journal write and retain the
   reference when completed or failed turns are persisted.
3. Add `get_trace_diagnostic` to canonical tool registration, schema projection,
   read-only effect classification, validation, and execution.
4. Offer the capability only when a diagnostic trace was explicitly referenced
   by the current request; keep the investigation turn in Chat mode.
5. Render the trace in completed and failed terminal summaries, restore it from
   local history, and expose `Investigate error` only for persisted failures.
6. Dispatch the hidden investigation prompt directly and render only the
   specialist response.
7. Add deterministic browser/API coverage for exact lookup, no path or
   enumeration surface, success without a button, failure with one button,
   transparent dispatch, and history restoration.

## Validation

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- Release build with zero warnings.
- Focused deterministic Playwright E2E coverage, followed by the applicable
  complete deterministic suite if the focused run passes.
- `git diff --check` and intended-diff review that separates this feature from
  the pre-existing dirty worktree.
- No real model, cloud provider, GPU workload, download, or application restart.
