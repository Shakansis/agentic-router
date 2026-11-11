# OpenAI API provider

OpenAI is an optional provider in Settings > Providers. Saving a key enables
the connection using the existing protected-secret store. The key can be replaced,
tested, or removed; it is never returned to the browser or exported in settings.
Connection tests and model refreshes call `GET /v1/models`, without inference.

The connection uses provider ID `openai` and qualified identities such as
`openai::gpt-6-sol`. Existing cloud approval, mandatory local fallback, usage,
Host tools, and workspace boundaries apply unchanged.

## Menu settings

The provider card exposes expected billing mode plus optional organization and
project IDs, service tier (`auto`, `default`, `flex`, `priority`), output token
limit, and response verbosity (`auto`, `low`, `medium`, `high`). These options
use the existing Save settings workflow. IDs are validated before HTTP headers
are constructed; invalid settings are rejected atomically.

An empty output limit preserves the provider default. When Host execution also
sets an output budget, the lower limit applies. Output includes reasoning tokens.
Service tiers are requests to the provider, subject to account and model support;
they can affect latency and billing. Verbosity is sent only to supported GPT-5
and GPT-6 models. Organization/project options apply to discovery and generation.

Inference profiles retain ownership of reasoning effort and sampling. Supported
reasoning modes use Responses `reasoning.effort`; unsupported profile controls
remain unavailable in Host inference evidence. Sampling is supported for the
non-reasoning GPT-4.1/GPT-4o entries. This adapter does not send sampling or seed
to reasoning models. It does not expose every OpenAI endpoint or parameter.
The existing `max` profile choice maps to the model's strongest documented effort
(`max`, `xhigh`, or `high`), without adding new global effort levels.
`auto` omits `reasoning.effort`, preserving the model default. `disabled`/the
composer's `None` sends `none` only for models that support it, including
GPT-6.1 Terra. Unsupported controls remain unavailable in Host evidence; the
adapter does not replace them with `medium`. Terra supports `none`, `low`,
`medium`, `high`, `xhigh`, and `max`; GPT-6.1 Sol and GPT-6 Astra omit `none`.

## Protocol and capability evidence

Generation uses `POST /v1/responses`, including text streaming, structured JSON,
image inputs, function calls and function results. Requests set `store: false`.
Host conversation and tool messages supply subsequent input. Original output items, including
replayable reasoning, hosted searches and assistant message phases, are carried
in order with function calls in memory, through the canonical tool adapter,
and returned only to the same provider/model. They are excluded from Host JSON
serialization, logs, visible answers, and persistence. Hidden reasoning is never
exposed. No separate continuation cache or provider-side conversation is used.

Structured planning and function-tool calls create a background response and
poll its ID for up to 10 minutes per recovery round. OpenAI temporarily retains
the response for roughly 10 minutes to serve these queries even with
`store: false`. Its API rejects explicit requests for encrypted reasoning on
these temporarily persisted responses. The adapter omits that include from
background creation and retrieval. It replays the original reasoning item
alongside its function call, including when OpenAI returns only its ID.
Assistant phases and tool results remain ordered.
The chat shows the last reported queued or in-progress state.
OpenAI does not report a completion percentage or whether reasoning has stalled.
Transient retrieval failures get up to three automatic retries on the same ID.
An explicit rate-limit rejection can also be retried. If recovery runs out,
the interactive chat offers another retrieval while the response ID is available.
If the original request never yielded an ID or the response has expired, the chat
can explicitly authorize a new generation that may incur another charge.
Recovery stays with the live turn and does not replay earlier Host tool actions.

The model listing does not establish endpoint or modality compatibility. Only
explicitly mapped model IDs and dated snapshots are selectable. Unmapped IDs,
embedding, audio, image-generation, and other specialized models remain discovery
entries without claimed chat support. Refresh the explicit compatibility mapping
when adding model families. The provider reports API usage and rate-limit headers;
missing pricing remains unavailable. No inference is used for discovery.

Streaming ends successfully only on a valid `response.completed` event.
Failed/incomplete responses and EOF without completion return typed errors.
Malformed function arguments are returned individually to common Host validation.

## Hosted web search and health

Explicitly supported models advertise native web search. The existing Host
capability resolution enables search and the adapter offers `{"type":"web_search"}`
alongside Host function tools. Tool choice stays automatic: availability is not
proof that a model actually searched. No additional search-provider key is needed.
The response parser reads completed `web_search_call` items and HTTPS
`url_citation` annotations. Both buffered tool responses and streamed terminal
responses propagate citations and a `web.search-completed` activity event.

Usage records distinguish `hostedWebSearchCallCount` (output tool items, including
search/open/find actions), `searchQueryCount` (only disclosed search queries),
`groundedRequestCount`, and unique citation URLs. Intermediate SSE output items
are not counted again. When search queries are omitted, their completeness is
unavailable rather than inferred from the tool count. These counters do not
establish the provider's billed search cost; `providerSearchCost` remains null.

The plain text/image path sends `stream: true`. The existing Host function-tool
path uses complete Responses JSON and forwards final text after completion;
enabling hosted search does not make that path stream token by token. Host
read-only file tools remain available together with OpenAI search. Local
providers and the other adapters retain their existing execution paths.

Provider health is observation-based. A stale successful observation can be
`degraded` without a failed HTTP request. The external-app warning describes
this as stale status instead of the misleading fallback `Unavailable`; it
does not issue background inference or change the connection's configuration.

OpenAI does not provide a decode duration. When a successful response supplies
output token usage, AR shows an estimated average in yellow with `≈`: the
reported output tokens, including reasoning, divided by the elapsed provider
request time observed by AR. The interval includes network, prompt processing,
reasoning, and hosted tools. Host tool execution between requests is excluded;
multiple calls aggregate their reported tokens and measured request times. The
estimate applies to both streaming and complete tool-call responses, survives
session history, and is not comparable to provider-reported Ollama decode speed.
Missing token usage, failures, and cancellations do not produce a fabricated rate.

## Sources

- [Responses and text generation](https://developers.openai.com/api/docs/guides/text)
- [Streaming responses](https://developers.openai.com/api/docs/guides/streaming-responses)
- [Background responses and polling](https://developers.openai.com/api/docs/guides/background)
- [Function calling](https://developers.openai.com/api/docs/guides/function-calling)
- [Current model compatibility and reasoning controls](https://developers.openai.com/api/docs/guides/latest-model)
- [Authentication and project headers](https://developers.openai.com/api/reference/overview)
- [Model discovery](https://developers.openai.com/api/reference/resources/models/methods/list)
- [GPT-6.1 Terra](https://developers.openai.com/api/docs/models/gpt-6.1-terra)
- [Hosted web search](https://developers.openai.com/api/docs/guides/tools-web-search)
- [Reasoning and assistant phase continuity](https://developers.openai.com/api/docs/guides/reasoning)

Deterministic E2E coverage uses the external fake-provider boundary. It does not
validate real credentials, model access, billing, or production API acceptance.
Opt-in credentialed browser/API acceptance and sanitized request evidence are
documented separately in [the web-search investigation](validation/openai-web-search-20261029.md).
