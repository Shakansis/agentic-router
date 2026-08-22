# PLAN v32: Parallel E2E Batches

## Goal

Reduce deterministic E2E wall-clock time without weakening assertions or
sharing mutable API/workspace state across parallel tests. Preserve the current
browser + running API + external-boundary fake architecture.

## Current bottleneck

- `ChatEndToEndTests` contains nearly the entire browser/API suite in one class.
- One static `TestEnvironment` owns the API, data root, workspace, provider
  fakes, and harness fakes for that class.
- Assembly-level `DoNotParallelize` plus one configured worker forces all 277
  tests through one serial lane; the current isolated Release baseline is
  7m56s.
- Merely moving methods between files would not create safe isolation or
  parallelism.

## Smallest complete change

1. Split the monolithic class into a shared generic E2E base plus a small set of
   cohesive test batches. Each closed generic batch owns an independent static
   `TestEnvironment`, so API ports, data, settings, workspaces, provider fakes,
   harness runtimes, and browser contexts cannot collide across batches.
2. Keep methods within each batch serial. Enable conservative class-level
   parallelism with two workers initially, then raise it to three after explicit
   user confirmation that the machine has sufficient capacity. Retain explicit
   non-parallel markers only where a test truly uses process-global state.
3. Keep common helpers and infrastructure inside the existing test project.
   Do not create a separate common project until multiple test projects exist.
4. Preserve all test names, data rows, assertions, fake-boundary contracts, and
   the ability to run focused filters.

## Intended batches

- provider/chat/UI and usage;
- benchmark and external harnesses;
- Native Execute, plans, approvals, and recovery;
- execution state, persistence, and remaining execution scenarios.

The initial split should balance measured runtime rather than maximize the
number of classes. Additional projects or CI shards are useful later only if
class-level isolation does not provide enough speedup.

## Validation

1. Build isolated Release output with zero warnings/errors.
2. List tests and prove the count/data rows are unchanged.
3. Run each batch independently once.
4. Run the complete suite repeatedly with two and then three class-level workers
   to detect collisions or flakiness and record wall-clock time against 7m56s.
5. Run formatter verification and `git diff --check`.
6. Do not invoke real local models, GPU workloads, cloud providers, downloads,
   or the user's running normal Release API.

## Boundary

This is test-architecture and execution-time work only. Do not change product
behavior, assertions, timeouts, provider/harness semantics, or production
dependencies. Do not create separate test projects unless measurement proves
the simpler class-level design insufficient.

## Results

- Isolated Release build: passed with zero warnings and zero errors.
- Discovery: preserved all 277 cases (244 batched E2E methods including data
  rows, plus 16 execution-effect cases).
- Independent batches passed: provider/UI 29/29 in 40.2s; benchmark/harness
  64/64 in 113.2s; Execute core 94/94 in 144.5s; execution state 74/74 in
  160.5s.
- Two-worker control passed twice: 277/277 in 279.7s and 280.9s.
- Three-worker configuration passed twice: 277/277 in 199.4s and 199.5s.
- Three workers reduce wall-clock time by about 58.1% from the 476s serial
  baseline and by about 28.8% from the measured two-worker average.
- Final formatter verification and `git diff --check` passed. Temporary
  isolated build outputs were removed without touching the user's running
  Release API processes.
- Parallel execution exposed a ledger polling read racing with an API write.
  The test now opens that JSONL with read/write sharing while preserving the
  original content assertions; both three-worker repetitions passed afterward.

Status: complete. Three class-level workers are the measured configuration;
separate test projects are not justified by the current evidence.
