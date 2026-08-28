# Automatic Host web search

## Problem

Web availability is currently exposed as a manual per-conversation toggle. For
local models the enabled flag also performs an eager search using the complete
user message, even when the model does not need current external information.
Execute harnesses do not share one Host web-search contract.

## Product decision

- Web availability is automatic for the effective model/provider/harness route.
- Availability does not perform a search. The active model decides whether a
  search materially improves the answer and calls a tool only then.
- Provider-native and harness-native web capabilities remain available as
  observable native extras. The Host does not remove them to normalize harnesses.
- A harness-native web path makes Web automatically available for that effective
  Execute route even when the separately configured Host search integration is
  unavailable. The UI and turn activity identify the effective source.
- When the configured Ollama Web Search integration and model tool protocol are
  available, the Host offers canonical `web_search` through the same tool
  catalog used by Native and every registered external harness.
- `web_search` is pathless and read-only. It never passes through filesystem
  target validation or mutation approval.

## Host contract

Input is one focused `query` string of 1 to 1,000 characters. The existing
`IOllamaWebSearchService` owns the protected key, HTTPS request, timeout, result
limit, response-size bound, untrusted-data envelope, HTTPS citation validation,
usage evidence, and typed failures.

The Host advertises the tool only while that integration is available. Calls,
results, citations, failures, and their source (`host-mediated`,
`provider-native`, or `harness-native`) remain visible in turn activity.

## Compatibility

The existing `webSearchEnabled` request field remains readable for older
clients, but the browser and Host derive effective availability from current
capability evidence. No frontend framework, new provider family, model download,
or unrestricted network/filesystem capability is introduced.

## Validation

- Browser E2E: the Web control is automatically active/inactive with capability
  evidence and cannot be manually toggled.
- Native Chat and Native Execute: the fake model may call `web_search`, receives
  bounded untrusted results, and completes with rendered HTTPS citations.
- External harness: the canonical Host tool is projected through the shared
  bridge only when available.
- A capable route that does not call `web_search` causes no search request.
- Invalid query, timeout, cancellation, quota, and unsafe citation remain typed
  and observable.
