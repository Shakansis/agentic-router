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
- The configured model keep-alive remains applicable. Independent processes
  prevent role changes from forcibly unloading the other GPU's model; this does
  not reserve VRAM indefinitely or change a configured inactivity timeout.
- Existing external Ollama endpoints, including the embedding service on 11435,
  remain separate from managed endpoints.

The per-model `Native file creation · local model` editor is removed. Settings >
General now exposes one optional global Native file-creation output limit. Blank
preserves the normal output limit; a configured value applies to `create_file` and
`create_files` generation for every model. Legacy per-model JSON/YAML values are
ignored. Ordinary context limits and bounded output-limit recovery remain in force.
