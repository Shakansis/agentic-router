# PLAN v40: Evidence-Based Recommendations — Milestone 12

## Goal

Add deterministic, traceable Model x Harness recommendations derived from the
benchmark evidence already persisted by Agentic Router. Recommendations inform
the user only; they never select, execute, benchmark, install, or route.

## Invariants

- Evidence priority is current comparable local evidence, comparable historical
  local evidence, explicitly requested external evidence, then clearly labeled
  inference. Insufficient evidence remains explicit.
- Reuse Milestone 11 comparability and the active scoring profile. Never treat
  incompatible conditions as equivalent or external evidence as locally
  measured fact.
- One run does not receive the same confidence as repeated comparable evidence.
  Do not invent statistical certainty.
- Categories must map to existing measured dimensions or suite/scenario facts;
  do not create unsupported task taxonomies.
- Identical evidence, category, scoring profile, and algorithm version produce
  identical rankings and explanations.
- Persisted benchmark evidence remains immutable. Recommendation projections
  contain evidence links and enough algorithm metadata for later explanation.
- External research is optional, user-triggered, primary-source-first, and
  strictly separated from local evidence and inference. No benchmark payload is
  sent externally.
- No recommendation request starts a benchmark, model, harness, task, install,
  switch, or automatic route.

## Ordered implementation

1. Define versioned recommendation contracts for supported categories,
   evidence strength, ranked candidates, strengths/weaknesses, missing
   measurements, supporting run links, and optional external evidence.
2. Implement a deterministic recommendation service over persisted raw
   benchmark evidence and Milestone 11 environment/comparability metadata.
   Aggregate repeated comparable Model x Harness runs and retain conflicts.
3. Map supported categories to existing dimensions: general coding, exact
   filesystem work, long continuity, recovery-heavy, correctness-first,
   terminality-first, and efficiency-first. Apply the active scoring profile
   where relevant.
4. Classify confidence from evidence count, recency/current-environment match,
   comparability, completion, and conflict without statistical claims. Identify
   missing Model x Harness measurements that would most improve confidence.
5. Add read-only recommendation endpoints. Add an explicitly requested external
   evidence path using the existing web-search boundary when available, keeping
   source facts separate from local rankings and continuing gracefully when web
   research is disabled or unavailable.
6. Add a compact recommendation section to the existing benchmark view with
   category/profile selection, ranked explanations, evidence links, missing
   evidence actions, and an opt-in external research control.
7. Add deterministic browser/API E2E coverage for evidence priority, history,
   profiles/categories, comparability, insufficiency, conflicts, external
   separation, determinism, traceability, backward compatibility, and absence
   of automatic execution.
8. Run applicable E2E coverage, Release build, format verification, JavaScript
   syntax validation, `git diff --check`, and intended-diff review.

## Scope exclusions

Do not add automatic routing, task/model/harness selection, task execution,
benchmark execution, uploads, community data, installation, Goose, DSH, or
Milestone 13 behavior.

## Stop condition

After implementation and deterministic validation, stop at:

`MILESTONE 12 EVIDENCE-BASED RECOMMENDATIONS READY FOR MANUAL TEST`

Do not start Milestone 13 without explicit approval.

## Execution record — 2026-08-23

- Added deterministic `benchmark-recommendation-v1` ranking over persisted raw
  benchmark evidence. The newest relevant local run is authoritative; directly
  comparable history contributes 30 percent, partially comparable history 10
  percent, and incompatible history remains traceable but does not affect the
  score.
- Supported categories map only to existing score/behavior dimensions and
  scenario IDs: general coding, exact filesystem work, long continuity,
  recovery-heavy, correctness-first, terminality-first, and efficiency-first.
  Targeted categories retain 20 percent of the selected scoring profile.
- Confidence is limited for one run or only partial/incompatible history,
  moderate for one repeated comparable run, strong for two or more, and mixed
  when relevant scores conflict by at least 20 points. Missing evidence lists
  the explicit Model x Harness measurements that would improve confidence.
- Recommendation projections persist immutably with algorithm/profile,
  evidence fingerprint, ranked explanations, and exact supporting benchmark run
  links. No benchmark result is modified.
- Optional Ollama Web Search runs only from the explicit external-research
  action. It sends public category/Model/Harness names, never benchmark payloads,
  and returns HTTPS citations as separate unverified external evidence. External
  sources do not silently alter locally measured rankings.
- The existing benchmark page now includes compact category/profile controls,
  local and external actions, ranked strengths/weaknesses, confidence and
  comparability counts, supporting evidence links, missing measurements, and a
  separate external-source section.
- Focused M11/M12 browser/API coverage passed 4/4. The complete deterministic
  E2E suite passed 302/302. Release build passed with zero warnings/errors;
  format verification, JavaScript syntax, and `git diff --check` passed. No real
  model, GPU, cloud provider, task, benchmark, or harness execution was run.
