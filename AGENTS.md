# AGENTS.md

## 1. Project Mission

Agentic Router is a local-first chat application that routes each user turn to the most appropriate local LLM.

The product must remain intentionally smaller and simpler than a general-purpose agent orchestrator. Its job is to:

1. receive a user message in a continuous chat session;
2. identify the message intent with a lightweight router;
3. resolve the model and device from user configuration;
4. stream the selected model response to the same conversation;
5. expose concise, collapsible routing and inference activity in the UI.

An expert is a configured combination of intent, model, optional device preference, and system prompt. It is not an autonomous process, agent graph, workflow engine, or tool-using worker.

## 2. Instruction Priority

When instructions conflict, follow this order:

1. the user's current request;
2. this `AGENTS.md`;
3. an approved feature specification in the repository;
4. existing implementation patterns that do not conflict with the items above.

Do not expand scope to solve hypothetical future needs.

## 3. Current Scope

The current implementation must use:

- .NET 10;
- ASP.NET Core Web API;
- the minimal hosting model;
- controllers for HTTP endpoints;
- no Minimal API endpoints;
- Ollama as the only model provider in the first version;
- vanilla HTML, CSS, and JavaScript for the web UI;
- Playwright for .NET with MSTest for browser-driven end-to-end tests;
- a single local application instance;
- one routing decision followed by one expert inference for each user turn.

The application must work with one GPU, multiple GPUs, or provider-managed automatic device selection.

Multi-GPU hardware is optional. Never make a second GPU a runtime requirement.

## 4. Explicit Non-Goals

Do not implement any of the following unless a later approved specification explicitly requests it:

- shell, PowerShell, process, file-system, desktop, or operating-system execution;
- local tools, MCP tools, plugins, or function calling;
- autonomous agents, recursive delegation, agent graphs, planning graphs, or approval gates;
- workflow builders, queues, schedulers, or background job systems;
- Hugging Face training pipelines, fine-tuning, LoRA, dataset preparation, or tokenizer management;
- RAG, embeddings, vector databases, document ingestion, or knowledge bases;
- remote model providers other than Ollama;
- automatic model downloads or destructive model management;
- distributed inference, multi-node scheduling, VRAM allocation engines, or custom GPU schedulers;
- databases, repository patterns, event buses, CQRS, MediatR, or microservices;
- authentication, accounts, teams, permissions, billing, telemetry platforms, installers, auto-update, or release infrastructure;
- image generation;
- persistent chat history across application restarts, unless separately specified.

Interfaces may leave a clean extension point for future providers, but do not build unused implementations.

## 5. Core Product Behavior

For every user turn, the application must follow this pipeline:

1. Load the current configuration before resolving the route.
2. Preserve the current chat session and append the new user message.
3. Classify the message intent with the configured lightweight router model.
4. Resolve the target model using the precedence rules in this document.
5. Resolve the device preference using the precedence rules in this document.
6. Invoke exactly one expert model through Ollama.
7. Stream progress events and response tokens to the browser.
8. Append the completed assistant response to the same session.
9. Keep the conversation interactive for the next user turn.

The classifier may label a request as `chat` or `task`, but both are model inference modes. A `task` does not authorize local command execution.

Do not create hidden agent loops. Do not ask one expert to call another expert.

## 6. Intent and Model Routing

Intent classification and model selection are separate responsibilities.

The router identifies an intent. It must not invent model names, device identifiers, providers, or configuration values.

Initial configurable intents should be small and practical:

- `general-chat`;
- `documentation`;
- `software-development`;
- `software-architecture`;
- `rpg-storytelling`;
- `review-and-testing`.

Intent names, prompts, and mappings must come from configuration rather than being duplicated across the UI and backend.

### 6.1 Model Resolution Precedence

Resolve the expert model in this order:

1. a model explicitly selected for the current request;
2. the configured model override for the detected intent;
3. the first installed and available model in the intent's ordered preferred-model list;
4. the configured global default model;
5. fail with an actionable configuration error.

Never silently choose an arbitrary installed model.

Example behavior:

- A documentation request with no override may resolve to `gpt-oss:20b` from the documentation profile.
- If the user configures Gemma for documentation, that configured model wins.
- If the composer has an explicit model selection, that request-level selection wins.

The UI must show which model was selected and why, without turning routing metadata into a prominent chat message.

### 6.2 Device Resolution Precedence

Resolve the device preference in this order:

1. a request-level device override, when supported;
2. the detected intent's configured device override;
3. the selected model's configured device override;
4. the global default device;
5. `auto`, delegated to the provider.

Required behavior:

- With one detected GPU, use it automatically unless the provider is configured differently.
- With multiple GPUs, use the configured default unless an override exists.
- Do not ask the user to select a GPU for every message.
- A device value must be a discovered provider capability or `auto`, not a hardcoded label such as `placa-1`.
- If Ollama cannot honor per-request device binding in the active runtime, report that limitation clearly and use the provider's configured default behavior.
- Never claim that a model ran on a specific GPU unless the application can verify or reliably obtain that information.

Do not build a GPU scheduler. Model loading, caching, and memory residency remain Ollama responsibilities in this version.

## 7. Configuration

Persist user-editable settings in one local JSON configuration store behind a small interface such as `ISettingsStore`.

Do not add a database.

The configuration must support:

- Ollama base URL;
- router model;
- global default expert model;
- global default device or `auto`;
- configurable intent profiles;
- model override per intent;
- ordered preferred models per intent;
- optional device override per intent or model;
- system prompt per intent;
- request timeout values;
- heartbeat interval for long-running streams.

A representative shape is:

```json
{
  "provider": {
    "type": "ollama",
    "baseUrl": "http://localhost:11434"
  },
  "router": {
    "model": "lightweight-router-model"
  },
  "defaults": {
    "model": "gpt-oss:20b",
    "device": "auto"
  },
  "intents": {
    "documentation": {
      "model": "gpt-oss:20b",
      "preferredModels": ["gpt-oss:20b", "gemma4:latest"],
      "device": "default",
      "systemPrompt": "You are a technical documentation specialist."
    }
  }
}
```

This is a contract example, not a requirement to copy property names blindly. Keep the final schema small, typed, validated, and versionable.

Invalid settings must be rejected with field-level error information. Do not partially save invalid configuration.

## 8. Recommended Application Structure

Prefer one production project and one Playwright test area. Do not create a multi-project architecture without a concrete need.

```text
/
  AGENTS.md
  layout-example*.html
  AgenticRouter.slnx
  AgenticRouter.Api/
    Controllers/
    Chat/
    Routing/
    Configuration/
    Providers/
      Ollama/
    Sessions/
    Contracts/
    wwwroot/
  tests/
    AgenticRouter.EndToEndTests/
```

Keep responsibilities explicit:

- Controllers translate HTTP input and output only.
- A chat-turn service coordinates one turn.
- An intent classifier classifies the turn.
- A model resolver applies model precedence.
- A device resolver applies device precedence.
- An Ollama provider owns Ollama HTTP communication.
- A settings store owns validated local configuration.
- A session store owns current in-memory conversation state.

Use built-in .NET dependency injection, options, logging, and `HttpClientFactory`.

Do not introduce architectural layers whose only purpose is renaming data as it crosses folders.

## 9. Backend Rules

- Use controllers, not Minimal API route declarations.
- Keep business logic out of controllers.
- Use typed request, response, event, and error contracts.
- Enable nullable reference types.
- Use asynchronous APIs end to end.
- Pass `CancellationToken` through controllers, services, provider calls, and stream writers.
- Use `HttpClientFactory` for Ollama communication.
- Validate configuration and request contracts at the boundary.
- Keep provider-specific DTOs inside the Ollama provider area.
- Do not leak Ollama response shapes into application or UI contracts.
- Do not use static mutable application state.
- Do not block async code with `.Result`, `.Wait()`, or thread sleeps.
- Keep build output at zero errors and zero warnings.

The frontend must communicate with the local API. It must never call Ollama directly.

## 10. Streaming Contract

Use a simple server-to-browser streaming mechanism suitable for one-way chat updates. Prefer Server-Sent Events or a streamed HTTP response unless a concrete requirement justifies WebSockets.

The stream should expose typed events equivalent to:

- `turn.started`;
- `intent.detected`;
- `route.selected`;
- `model.started`;
- `response.delta`;
- `heartbeat`;
- `response.completed`;
- `turn.failed`.

Rules:

- Only `response.delta` contributes text to the visible assistant answer.
- Routing, model, device, timing, and heartbeat events belong in a secondary activity area.
- Heartbeats keep the UI informed during periods without tokens.
- Heartbeats are not chat messages and must not pollute conversation history.
- Flush stream data promptly.
- Preserve event ordering.
- End every turn with exactly one terminal event: completed or failed.
- Cancellation must stop the provider request and close the stream cleanly.

A stable event envelope should include an event type, turn ID, timestamp, and typed payload.

## 11. UI and Interaction Contract

Use the root `layout-example*.html` file as a visual starting point, not as production architecture or a file to copy without review.

The target interaction should follow familiar local coding-chat patterns while using original project styling and assets.

Required behavior:

- The chat is the primary surface.
- User messages align to the right.
- Assistant answers remain readable and visually dominant.
- The composer remains accessible at the bottom.
- The conversation continues in one session after each response.
- Streaming text appears progressively in the assistant answer.
- Router and expert activity appears in a muted details block.
- Activity details are collapsible with a caret.
- Completed activity collapses by default.
- Expanding or collapsing activity must not interrupt token streaming.
- The activity summary should show elapsed time or current state in a compact form.
- The UI should show the selected intent, model, provider, and device when known.
- Status pings must be visible without becoming separate assistant messages.
- Configuration must support global defaults and per-intent overrides.
- The composer should provide a small `Auto` or explicit model selector.
- Enter sends a message.
- Shift+Enter inserts a line break.
- Escape closes an open configuration dialog.
- Collapsible controls must expose accessible state such as `aria-expanded`.
- Focus states must remain visible.

Auto-scroll only while the user is already near the bottom. Never pull the viewport away from content the user is reading.

Use plain HTML, CSS, and JavaScript. Prefer separate production files such as `index.html`, `styles.css`, and `app.js`. Do not add React, Vue, Angular, Svelte, Tailwind, Bootstrap, a bundler, or a component framework.

Do not add a frontend build pipeline. Do not add Node.js, npm, `package.json`, `node_modules`, or JavaScript-based test tooling. Use the official Playwright for .NET packages for browser tests.

## 12. Error Handling

Errors must be useful to both the user and the developer.

Do not swallow exceptions and do not replace every failure with a generic message.

Return a stable error contract containing fields equivalent to:

- `code`;
- `message`;
- `stage`;
- `provider`;
- `model`;
- `intent`;
- `retryable`;
- `traceId`;
- optional sanitized details.

Rules:

- Preserve the original exception as the logged cause.
- Propagate useful provider status and sanitized response information.
- Never expose secrets, full stack traces, or raw internal objects in the browser.
- Make configuration, connection, unavailable-model, timeout, cancellation, invalid-router-output, and streaming failures distinguishable.
- Any fallback must be explicit in the activity stream and logs.
- Do not silently retry indefinitely.
- Error messages must state the failed stage and a corrective action when one is known.

## 13. Testing Policy

The automated test suite must contain end-to-end tests only.

Use Playwright for .NET with MSTest to exercise the browser UI and the running .NET API together.

The default E2E suite may replace Ollama only at its external HTTP boundary with a deterministic fake provider. Do not mock internal controllers, routing services, settings, session behavior, or browser code.

Every Playwright test has a maximum timeout of 60 seconds.

When a test exceeds 60 seconds:

1. do not simply increase the timeout;
2. identify whether the delay is caused by startup, selectors, polling, streaming, provider behavior, or a deadlock;
3. report the cause;
4. correct the implementation or test synchronization;
5. keep the timeout at or below 60 seconds.

Testing rules:

- Use Playwright assertions and event-based waiting.
- Do not use arbitrary sleeps.
- Keep tests independent and deterministic.
- Start from known configuration and session state.
- Capture useful trace, console, network, and screenshot artifacts on failure.
- Keep the normal suite independent from installed local models and GPU hardware.

Minimum E2E coverage:

1. application and chat UI load successfully;
2. configuration loads, validates, saves, and survives a page refresh;
3. a documentation request resolves to the configured documentation model;
4. a request-level model override wins over automatic resolution;
5. a single-device environment works without prompting for a GPU;
6. streamed tokens build one continuous assistant response;
7. activity events remain collapsible while the answer streams;
8. the next user message retains prior session context;
9. an unavailable model produces a useful visible error with a trace ID;
10. a provider failure reaches the UI without a generic silent fallback.

Do not add unit or integration test projects unless the user changes this policy.

## 14. Code Quality Rules

Apply SOLID pragmatically. Favor cohesive code over design-pattern ceremony.

- Keep classes and methods focused.
- Prefer clear names and small functions over explanatory comments.
- Do not write comments unless they explain a necessary non-obvious reason, constraint, workaround, or protocol detail.
- If code needs a comment to explain what it does, simplify the code first.
- Do not use comments as section dividers or narration.
- Avoid `#region` blocks.
- Avoid premature abstractions and generic frameworks.
- Avoid duplicate routing or configuration logic.
- Prefer immutable records for contracts and configuration snapshots.
- Prefer explicit result types for expected failures.
- Keep I/O at the edges.
- Do not add dependencies when the platform already provides a simple solution.
- Remove dead code and unused configuration during the same change that makes them obsolete.
- Never leave placeholder production behavior that reports success without performing the operation.

## 15. Change Discipline for Coding Agents

Before editing:

1. inspect the repository structure and relevant files;
2. inspect the current build and test commands;
3. read the root layout example when the task changes UI behavior;
4. state the smallest implementation slice that satisfies the request.

While editing:

- Preserve existing working behavior unless the request changes it.
- Make the smallest coherent vertical change.
- Do not refactor unrelated code.
- Do not add future features as scaffolding.
- Do not overwrite the root layout example unless explicitly instructed.
- Do not rename public contracts casually.
- Keep configuration backward-compatible when practical, or provide a direct migration.

After editing:

1. run formatting if configured;
2. run `dotnet build` in Release configuration;
3. run the relevant Playwright E2E tests;
4. verify that no test exceeds 60 seconds;
5. report changed files, commands run, results, and any real limitation.

Never claim validation that was not executed.

## 16. Definition of Done

A change is complete only when all applicable items are true:

- the requested behavior works through the browser;
- model and device precedence are deterministic;
- Ollama communication remains isolated behind the provider boundary;
- the response streams into one continuous assistant message;
- routing and heartbeat details are available in a collapsible secondary area;
- continuous chat context works for the current session;
- errors expose useful stage and trace information;
- the Release build has zero errors and zero warnings;
- relevant E2E tests pass within the 60-second limit;
- no local execution, agent graph, RAG, training, or unrequested provider work was introduced;
- the implementation remains understandable without unnecessary comments.

## 17. Scope Guardrail

The product is a focused local LLM router with a polished chat interface.

When deciding between a simple direct implementation and a reusable orchestration platform, choose the simple direct implementation.

Future capabilities such as local actions, additional providers, persistent history, RAG, fine-tuning, and advanced GPU scheduling require separate specifications and must not leak into the current implementation by assumption.
