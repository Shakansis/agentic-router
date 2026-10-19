# Model GPU affinity and managed Ollama coexistence

Approved behavior, 2026-09-16:

- An explicit model GPU affinity overrides every role, intention, supervisor,
  worker and benchmark GPU selection.
- Model affinity `Auto` uses Settings > General > Default GPU directly. Legacy
  intention and coordinator GPU values remain readable but do not control placement.
- The supervisor uses its own model affinity. Selecting the same model in an
  intention does not configure a global model affinity; use GPU Affinity in the
  model organization card.
- Each managed backend/device selection has its own deterministic loopback port,
  process and lease. Switching roles reuses these processes. A context change
  replaces only the server for the affected selection, never other selections.
- Discovery, diagnostics, setup status, runtime-profile listing and analysis do
  not start or replace Ollama servers. Residency observations use active endpoints.
- The configured model keep-alive remains applicable to the selected model.
  Supervised role changes retain independent residency across managed servers.
  Direct follows the single-model rule below.
- Existing external Ollama endpoints, including the embedding service on 11435,
  remain separate from managed endpoints.

## Direct single-model residency (2026-10-16)

Before a local Direct turn loads its selected model, the Host unloads other
models from every AR-owned Ollama endpoint, including another GPU backend or KV
cache configuration. It sends `keep_alive: 0` to the observed endpoint, without
resolving the old model's current GPU affinity. The selected model is retained
when already resident at the selected endpoint; idle managed server processes
remain available for reuse.

The Host verifies disappearance through `/api/ps` before permitting the next
load. Verification is bounded to ten seconds after the unload response. Failed
or unverified unloading produces a recoverable provider error and prevents the
new load. Activity records expose waiting, unloading and verified unloading.

Local Direct turns take an exclusive request lease for the complete turn,
including tool calls and approvals. They wait for active Chat/Execute turns to
finish and release the lease on completion, failure or cancellation. Supervised
turns share admission with each other; they cannot start during the exclusive
Direct turn. This prevents a model switch from evicting an active turn's model.

On an external Ollama endpoint, cleanup is restricted to model names previously
selected by Chat/Execute in the current Host process. Other externally loaded models
are preserved. Restarting the Host does not grant ownership over external
models. An external service's own eviction policy remains outside AR's control.

The per-model `Native file creation · local model` editor is removed. Settings >
General now exposes one optional global Native file-creation output limit. Blank
preserves the normal output limit; a configured value applies to `create_file` and
`create_files` generation for every model. Legacy per-model JSON/YAML values are
ignored. Ordinary context limits and bounded output-limit recovery remain in force.
