# PLAN v34: Qwen Benchmark Context and Lifecycle

## Goal

Restore reliable Qwen Code Basic CRUD execution without hiding context waste.
Use 32,768 tokens as the current benchmark floor, expose only tools relevant to
the active benchmark turn, and release the workspace-scoped Qwen daemon before
the Host deletes the disposable workspace.

## Confirmed evidence

- Qwen Code 0.21.13 received about 23,700 prompt tokens for each minimal CRUD
  task while the Host configured an 8,192-token hard context limit.
- Automatic compression retained about 23,500 tokens and the daemon failed
  with `Context is too large to send safely after automatic compression`.
- The Qwen runtime loaded nine native tools and seventeen Host MCP tools even
  though the benchmark profile exposed only six canonical capabilities.
- Qwen produced the exact CREATE effect, but the benchmark reported
  `benchmark-cleanup-failed` because the workspace-scoped daemon was still
  alive when cleanup ran.
- Cleanup replacement discarded the original Qwen/context error, obscuring the
  primary cause.

## Smallest complete change

1. Advance the Ollama runtime profile schema and migrate the legacy benchmark
   default only. The new benchmark profile is minimum 32,768, target 32,768,
   maximum 40,960, output limit 4,096. Preserve explicitly customized profiles.
2. Configure the Qwen MCP bridge from the active Host capability profile, not
   the maximum application-wide tool catalog.
3. Mark benchmark turns for minimal native tool inventory. Keep normal Execute
   native capabilities unchanged.
4. Mark benchmark turns to release workspace-scoped harness resources after
   terminal state. Qwen stops its owned daemon before benchmark cleanup; normal
   Execute session continuity remains unchanged.
5. If cleanup fails, retain the primary harness error and add cleanup failure
   as bounded evidence rather than replacing the root cause.

## Validation

1. Add deterministic coverage for runtime profile migration, Qwen 32k benchmark
   context, scoped tool inventory, daemon release, cleanup, and primary-error
   preservation.
2. Run focused Qwen and benchmark E2E coverage.
3. Run the complete deterministic fake-provider suite.
4. Run isolated Release build, formatter verification, and intended-diff
   inspection.

## Boundary

Do not run a real model, GPU workload, cloud provider, download, or service
restart. Do not implement VRAM-derived sizing or a new settings editor in this
change. Preserve exact model/provider/workspace selection, Host validation,
approval, scoring, and benchmark ordering.

## Completion evidence

- Runtime profile schema v2 now migrates only the exact legacy benchmark
  default to minimum/target 32,768, maximum 40,960, and output limit 4,096.
  Explicitly customized benchmark profiles remain unchanged.
- Qwen benchmark turns now receive only the active Host bridge tools and the
  native tools needed by that capability profile. Normal Execute turns retain
  the full existing Qwen native inventory.
- Benchmark turns release the workspace-scoped Qwen daemon before disposable
  workspace cleanup. A cleanup failure no longer replaces an earlier harness
  or context failure; it is recorded as additional bounded evidence.
- Isolated Release build passed with zero warnings and zero errors.
- After the previously running API exited, the normal Release output was also
  rebuilt successfully with zero warnings and zero errors.
- Focused migration and live-benchmark-boundary coverage passed 2/2. All tests
  containing `QwenCode` passed 14/14.
- The complete deterministic fake-provider browser/API suite passed 278/278,
  zero skipped, in 167.1 seconds.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
- No real model inference, GPU workload, cloud request, download, or service
  restart was performed. The corrected real Qwen CRUD rerun remains a manual
  gate after the running API is rebuilt and restarted.
