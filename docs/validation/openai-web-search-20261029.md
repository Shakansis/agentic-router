# OpenAI web search, reasoning and health investigation — 2026-10-29

## Diagnosis and original trace

Agentic Router uses **Responses API**, `POST /v1/responses`, for OpenAI.
The original adapter did not advertise native web search, offer `web_search`,
read `web_search_call`/URL annotations, or record hosted search activity. Merely
using Responses does not enable a tool. This explains the unavailable web
capability and missing search evidence; the incorrect model claim itself is
model output, not proof of a disconnected provider or unsupported model.

Trace `0HNPM3VGQMH85:00000509` directly records:

- User-selected `openai::gpt-6.1-terra`; router bypassed at 26 ms.
- A separate `knowledge.retrieval-failed` milestone at 4,374 ms. This is the
  configured knowledge integration, not OpenAI hosted web search.
- `chat.read-only-tools-enabled` at 4,382 ms.
- One successful target response: 1,487 input, 188 output, 0 reasoning tokens;
  provider duration 6,595 ms; searches, grounded requests and citations all zero,
  with activity accuracy unavailable.
- First answer chunk at 10,983 ms; completed at 10,984 ms. The existing Host
  tool path delivered the answer after the full provider response.

Evidence was filtered from local incident and usage ledgers. There is no saved
original wire payload, so the following is a **code reconstruction**, not a
capture of that historical request. Unrelated options and input are omitted:

```json
{
  "model": "gpt-6.1-terra",
  "store": false,
  "tools": [
    { "type": "function", "name": "list_files" },
    { "type": "function", "name": "read_file" },
    { "type": "function", "name": "read_files" },
    { "type": "function", "name": "get_file_info" },
    { "type": "function", "name": "search_text" }
  ]
}
```

## Changes

- Explicit OpenAI model compatibility advertises supported hosted search and
  citations. Cached OpenAI discovery is normalized without background provider
  refresh. Unknown/unsupported models retain conservative discovery semantics.
- Requests offer `web_search` and request `web_search_call.action.sources`.
  Existing Host file reads remain available alongside hosted search; model tool
  choice remains automatic.
- Both generation paths parse terminal search items and URL annotations,
  propagate clickable sources, record `web.search-completed`, and persist
  hosted call count separately from disclosed query count and citation count.
  Intermediate SSE item events are not counted twice.
- Original output items are replayed in order during a function-tool round
  trip, preserving hosted searches, encrypted reasoning and message `phase`.
  Continuation stays in memory, excluded from logs/history/API serialization.
- `auto` omits explicit reasoning effort. `None`/`disabled` preserves `none`
  where supported. Unsupported disabled reasoning is reported unavailable and
  left to the provider default. `max` maps to the strongest supported level.
- The stale OpenAI warning now describes an old status observation rather than
  falling through to `Unavailable`. Local-provider paths and warning behavior
  remain unchanged.

The official [Terra model documentation](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
lists `none`, `low`, `medium` (default), `high`, `xhigh`, and `max`, and requires
Responses for tools. GPT-6.1 Sol and GPT-6 Astra do not support `none`.
See also [web search output](https://developers.openai.com/api/docs/guides/tools-web-search)
and [reasoning/phase continuity](https://developers.openai.com/api/docs/guides/reasoning).

## Real browser/API acceptance

Three Playwright tests exercised the browser and an isolated running API, with
the real OpenAI endpoint behind a transparent external-boundary recorder.
The existing protected credential was copied in encrypted form into temporary
test settings and decrypted by the ordinary application secret store. Only
synthetic prompts and a temporary marker file were used. Ollama remained a fake
external boundary; no real local model or GPU inference was invoked.

The recorder forwarded requests unchanged and saved only allowlisted protocol
metadata, token usage, request IDs, timings and output types. It did not save
credentials, input contents, tool arguments/results or encrypted reasoning.
All four final inference requests returned HTTP 200 and `status: completed`.

| Browser scenario | Effort sent | Responses calls | Hosted calls | Disclosed queries | Citations | Input / output tokens |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Text + hosted search | `none` | 1 | 1 | 3 | 2 | 12,879 / 215 |
| Text + hosted search | `low` | 1 | 1 | 3 | 2 | 12,840 / 189 |
| Host file read + hosted search | `low` | 2 | 1 | 2 | 1 | 15,134 / 206 |

All three returned an answer identifying Terra as official, listing supported
efforts and linking official documentation. The mixed case also returned
`TERRA_TEST_MARKER_29`, independently supplied by the Host file tool. Its first
output was `message, function_call`; the next was `web_search_call, message`.

Actual sanitized fields from the accepted `none` request, with input omitted:

```json
{
  "model": "gpt-6.1-terra",
  "store": false,
  "stream": true,
  "reasoning": { "effort": "none" },
  "max_output_tokens": 4096,
  "service_tier": "default",
  "include": ["reasoning.encrypted_content", "web_search_call.action.sources"],
  "tools": [{ "type": "web_search" }]
}
```

The accepted mixed request used `reasoning: {"effort":"low"}`, omitted `stream`
and offered both `web_search` and the five Host functions shown above.
The provider returned `web_search_call` and citation annotations; the usage
ledger recorded one hosted call, two disclosed queries, one grounded request,
one citation, accuracy `exact`, and `providerSearchCost: null` on its second call.

Representative provider request IDs:

- `none`: `req_171e63ae7b3843438c854e800405f756`.
- `low` streaming: `req_a540d37245984464a799a22629a781bd`.
- Mixed tool continuation: `req_d74e23ed384a43e6850a6b778770e564`,
  `req_367f33fca5c6439db2c5dd0f8d62397d`.

Safe JSON reports/screenshots are retained locally under
`.artifacts/openai-web-search/real/`; final TRX is
`.artifacts/openai-web-search/results/openai-real.trx`.

The first acceptance attempt completed search successfully but failed all three
effort assertions: it configured only one inference profile and did not select
the intended Thinking override in the composer. Those results are retained in
`real-initial/` and `openai-real-initial.trx`. After correcting the test setup,
the repeated browser run passed **3/3** with the intended wire values. Across
both runs there were eight successful real inference calls and six model-list
requests; no claim of measured billing is made.

## Streaming and compatibility limits

- Text plus hosted search uses real SSE: first text arrived at 4,177 ms with
  `none` (total 5,780 ms), and 3,193 ms with `low` (total 4,521 ms), measured at
  the forwarding boundary.
- The Host function-tool path remains buffered JSON. The two real provider
  calls took 2,747 and 4,906 ms. It emits final text after the full response;
  this is a separate existing limitation, not a lack of web-search support.
  Token streaming for that path was evaluated, not implemented in this change.
- OpenAI accepted `none` with hosted search but reported **77 reasoning tokens**.
  `low` streaming also reported 77, and the mixed case reported 0 then 57.
  This is observed provider accounting; it does not prove which internal search
  component produced those tokens. AR neither rewrote `none` nor inferred zero.
- Disclosed queries are not identical to hosted output items or billable search
  actions. OpenAI may omit query details. Missing details remain unavailable,
  and hosted call observations do not become an exact monetary charge.
- Only GPT-6.1 Terra received real inference acceptance. Other mapped models,
  Execute mode and external native harnesses were not credential-tested here.
  This fix and its acceptance cover Chat. Native Execute's `LocalActionPlanner`
  does not currently forward these Chat web-search options; this record does
  not claim hosted-search support through that planner. Its existing Host
  web-search mechanism was not changed.
  The full repository suite was not run. Availability does not guarantee that
  an automatic tool choice searches for every recent-information prompt.

## Screenshot warning

Read-only inspection of the running user's API showed OpenAI enabled with a
saved key and 130 discovered models. Connection discovery still said connected.
Health reported `degraded`, `stale: true`, last success
`2026-10-29T22:04:35.9006954-03:00`, last diagnostic HTTP 200, no error category,
and `request-completed`. After 900 seconds without a new observation the health
monitor marks that evidence stale. The sidebar passed the empty diagnostic to
a helper whose fallback was `Unavailable`.

The fix scopes the stale explanation to OpenAI and preserves the health
threshold, configured connection and all other providers. No background probe
or inference was added. Browser E2E verifies successful inference followed by
API staleness produces `Status is stale`, not `Unavailable`.

## Validation and operational state

- Release build in `.artifacts/openai-web-search`: **0 warnings, 0 errors**.
- Focused fake-boundary browser/API regression: **49/49 passed**, 1m 56s.
  Covers both search paths, exact tool/citation counts, reasoning choices,
  ordered native continuation and secrecy, stale warning, settings/key lifecycle,
  cloud retries/privacy/fallback, existing availability and Native multi-tool
  behavior. This is deterministic coverage, distinct from real acceptance.
- Real browser/API acceptance: **3/3 passed**, 38s.
- Final sidebar/availability regression after scoping the UI change to OpenAI:
  **8 passed**, plus **3 real tests skipped** with opt-in absent, 50s. No new
  paid inference was made in this check. The guard follows the repository's
  opt-in test convention instead of failing an ordinary deterministic run.
- `dotnet format --verify-no-changes` passed for all changed C# files;
  `git diff --check` passed, and the intended source/test diff was inspected.
  Final Release build also passed with zero warnings/errors. No package or
  dependency was added.

Commands use isolated apphosts and data to avoid locking normal Release output:

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/openai-web-search --no-restore -m:1 -nr:false
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include <changed C# files>
& .artifacts/openai-web-search/validate.ps1
& .artifacts/openai-web-search/validate.ps1 -Real
& .artifacts/openai-web-search/validate.ps1 -Filter 'Name=OpenAiStaleHealthIsNotDisplayedAsDisconnectedAfterSuccessfulInference|FullyQualifiedName~ExternalAvailabilityEndToEndTests|FullyQualifiedName~RealOpenAiWebSearchEndToEndTests' -Log 'openai-final-ui-optin.trx'
git diff --check
```

The local runner sets matching `AGENTIC_ROUTER_E2E_*` apphost paths, uses
`playwright.runsettings`, Release, `--no-build`, `--no-restore`, and the isolated
artifacts path. Real tests require explicit `AR_OPENAI_REAL_ALLOWED=1`.
The existing user's AR process (PID 36728, ports 5294/7155/7156) was preserved;
it must be rebuilt/restarted separately to load the adapter change.
Final process/listener inspection found no remaining isolated test hosts,
Playwright browsers/drivers or .NET build servers from this work.

Principal follow-up files: `OpenAiCloudProvider.cs` and
`CloudProviderRegistry.cs` (protocol/capabilities), `CloudProviderContracts.cs`,
`ModelProviders.cs`, `IOllamaClient.cs` and `ProviderDispatchClient.cs` (metadata
transport), `ChatStreamService.cs` (Chat coordination/evidence),
`UsageContracts.cs`, `PricingCatalog.cs`, `UsageValidation.cs` (ledger), and
`projects-sessions.js` (OpenAI warning). Other adapters only accept the optional
interface parameter and keep their existing request behavior. Coverage lives
in `OpenAiEndToEndTests.cs`, `FakeCloudProviderServer.cs`,
`RealOpenAiRecordingProxy.cs`, `RealOpenAiWebSearchEndToEndTests.cs`, and
`TestEnvironment.cs`. The provider contract and this evidence record document
the resulting scope and limitations.
