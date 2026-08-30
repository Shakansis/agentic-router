# Pre-1.0 readiness checklist

This checklist reports evidence; it does not declare a release ready when a
required item is skipped.

- [ ] Version declarations agree across project, browser, and release notes.
- [ ] Formatter and Release build pass with zero warnings.
- [ ] Full deterministic Playwright E2E passes with zero skipped tests.
- [ ] Publish artifact verifier passes.
- [ ] Persisted stores expose supported schema identities.
- [ ] Migration backup, atomic replacement, original preservation, and
  failure-state behavior pass.
- [ ] Secrets, local data, tests, fake providers, diagnostics, and reports are
  absent from publish and backup.
- [ ] Provider adapters, tool conformance, workspace security, session storage,
  usage ledger, reconciliation, backup, restore, and rollback evidence pass.
- [ ] Safe mode disables Execute, provider/model activity, settings writes, and
  automatic history loading while allowing backup.
- [ ] `AGENTS.md`, release notes, decisions, and maintainer documentation match
  implemented behavior.
- [ ] Real Ollama/cloud checks are marked run or skipped with permission reason.
- [ ] Intended diff, remote SHA, and final working-tree state are recorded.
