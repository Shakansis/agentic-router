# Host memory investigation — 2026-10-11

## Validation boundary

The requested user-owned running instance could not be identified. Read-only
Windows process/listener surveys found no user-owned `AgenticRouter.Api.exe`,
`dotnet.exe`, or IIS Express and no listener on 5294, 7155, 7156, 63001, or 63002.
The browser's 63002 URL was not evidence of a running server. Later AR processes
were exclusively E2E Hosts whose executable paths and parent processes belonged
to this investigation. The user was asked for the active PID/address.

No user-owned process was stopped, restarted, suspended, instrumented, or changed.
No forced GC, heap dump, root walk, real inference, model loading, or deletion of
user history was performed. The reported recurring 16 GB peak remains unproven
in this run; these results do not establish a memory leak or its roots.

## Confirmed costs and changes

1. History admission/retention checks called `ReadAllAsync` to inspect session
   count, pin status, and timestamps, unnecessarily materializing every transcript.
   `PersistentSessionService` and `ConversationProductivityService` now use the
   existing metadata path for these decisions. Existing explicit archive/delete
   operations also enumerate metadata, without changing their deletion semantics.
2. Persistence and inference projection size checks repeatedly constructed an
   entire indented UTF-16 JSON string, followed by UTF-8 counting, newline copies,
   and file encoding. `PersistentSessionStore` now serializes asynchronously to
   the existing atomic temporary file and measures canonical UTF-8 bytes with a
   counting stream. LF formatting and the terminal LF are explicit. Counts are
   `long`; cancellation propagates through asynchronous projection measurement.
3. Content search previously retained all complete session records for a workspace
   simultaneously. It now sorts metadata and opens one record at a time. Ordering,
   result limits, filters, scanned counts, and invalid-record logging remain.
   Search correctness was tested; its memory benefit was not benchmarked separately.

The exact canonical history, compression verification, legacy transcript reads,
inference projection, atomic replacement, undo bytes, snapshots, and checkpoint
contracts remain. There is no new persistence schema, product cache, dependency,
background job, forced collection, or truncation of historical content.

## Controlled measurements

Both binaries came from the same working checkout, captured immediately before
these memory changes and built after them. Each scenario used a fresh isolated
Release Host, its own data directory, a browser/API fixture, and fake external
provider/harness boundaries. The old binary was preserved while rebuilding only
its test assembly. Original user sessions were never copied into these fixtures.

Private Bytes were sampled every 20 ms during the operation. An opt-in test-only
collector sampled process counters every second and `System.Runtime` EventCounters
every five seconds for 15 seconds (`requestRundown: false`). The collector attaches
only to the fixture's recorded API PID. The same collector was used for both
versions. No forced collection was used. Counter windows include fixture/idle
overhead and are not precise action-only allocation measurements.

Scenario A: 14 persisted histories of 8 MiB each, valid metadata indexes, then 12
new small conversation saves. Scenario B: save eight assistant messages of 2 MiB
each plus a small user message; content includes Unicode and CRLF. MiB is 2^20
bytes. Heap/LOH/GC values below are sampled maxima, not post-GC retained live sizes.

| Measurement | A before | A after | B before | B after |
| --- | ---: | ---: | ---: | ---: |
| API PID | 27712 | 131592 | 129756 | 27712 |
| Baseline Private Bytes | 81,829,888 | 87,113,728 | 73,170,944 | 78,241,792 |
| Sampled peak Private Bytes | 729,059,328 | 90,202,112 | 653,881,344 | 327,880,704 |
| Peak increment (MiB) | 617.25 | 2.95 | 553.81 | 238.07 |
| Managed heap (MiB) | 368.85 | 32.41 | 185.91 | 206.39 |
| LOH (MiB) | 319.60 | 0.09 | 174.10 | 160.10 |
| GC committed bytes | 612,413,440 | 18,493,440 | 442,748,928 | 236,838,912 |
| Allocation increments over collector windows (MiB) | 2,825.98 | 10.18 | 616.94 | 260.41 |
| First-window allocation rate (decimal MB/s) | 590.94 | 1.96 | 128.85 | 54.29 |
| Gen 0 / 1 / 2 collection increments | 25 / 25 / 25 | 0 / 0 / 0 | 9 / 9 / 9 | 7 / 7 / 7 |
| Maximum reported time-in-GC (%) | 5 | 0 | 4 | 14 |

Windows reused PID 27712 between runs; timestamps and executable paths, rather
than PID alone, distinguish them. Scenario A's sampled peak increment fell by
99.5%; scenario B's by 57.0% in this pair. Other fresh after-write runs observed
154–184 MiB increments (the earlier streaming-only run observed 162 MiB).
GC timing and heap samples vary: B's sampled managed heap and time-in-GC increased,
so these measurements do **not** support a blanket claim that every GC metric
improved. Private commit is not resident RAM, and allocation is not retention.

Evidence lives under `.artifacts/memory-audit-20261011/`:

- `before/results/admission-before-final.trx` and its runtime-counter attachments;
- `before/results/serialization-before-comparable.trx` and its attachments;
- `validated/results/admission-counters/admission-counters.trx`;
- `validated/results/serialization-counters/serialization-counters.trx`;
- `after-final/results/recovery-final/recovery-final.trx`;
- `validated/results/regressions-final/regressions-final.trx`.

TRX stdout preserves PID, baseline/peak, and final counter summaries even when
MSTest cleans passing-test attachment deployment directories. These artifacts
contain synthetic data only. The test collector is optional via
`AGENTIC_ROUTER_E2E_MEMORY_COLLECTOR`; it is not part of the product build.

## Tests and commands

`MemoryRetentionEndToEndTests` adds browser/running-API regressions for:

- admission against 112 MiB of saved bodies, a bounded Private Bytes increment,
  exact reopening of separate histories, and reopening after Host restart;
- large history writes with bounded Private Bytes increment and exact Unicode/
  CRLF content recovery;
- 30 consecutive fake-Codex Execute turns, 60 persisted messages, all streamed
  reasoning recovered exactly after restarting the test Host and paging backwards.

The final sequence observed a 125.35 MiB Private Bytes increase (58,458,112 to
189,898,752), sampled after each turn. This is a short repeated-execution test,
not an hourly soak, a peak sampler, or proof of bounded growth for large journals.
The before binary fails the new two memory bounds as expected; these failures
demonstrate the regressions rather than failures of the final implementation.

Two existing supervision recovery fixtures sent directly through the button and
did not answer the current no-Git decision. They now use the existing
`StartMessageAsync` helper, which supplies the fixture's context-only decision.
Assertions, deadlines, approval policy, and production behavior were preserved.
Both passed on rerun.

Commands, with separate output paths and no persistent build servers:

```powershell
dotnet format whitespace AgenticRouter.slnx --no-restore --verify-no-changes --include <the seven changed C# files>
dotnet build AgenticRouter.slnx -c Release --no-restore --disable-build-servers -m:1 -p:OutputPath=<isolated validated/bin/> -v minimal
dotnet test <isolated/AgenticRouter.EndToEndTests.dll> --filter <targeted E2E filter> --logger "trx;LogFileName=<run>.trx" --results-directory <separate run directory>
git diff --check
```

Release build: zero warnings/errors. Scoped formatting verification and diff
whitespace checks passed. The final isolated regression run passed **34/34**,
covering history search/retention, compression/corruption/legacy continuation,
snapshots and rollback across harnesses, browser loss/reconnection, supervision
restart, checkpoint migration, and the repeated Execute sequence. The two final
memory measurement tests passed separately: **36/36 final targeted cases**.
The full repository test suite and real-provider workloads were not run.

Final read-only process verification found no remaining AR Hosts, fake harness
servers, collectors, or `dotnet` processes from these test/build runs. The
pre-existing Qwen/other user-owned processes were left untouched.

## Remaining investigation

Code review found candidates, not proven leaks:

- `LiveChatRuns`: recent completed runs are trimmed on new-run admission, but each
  run retains an event journal without a byte cap. Replay currently depends on
  that journal. Large/long active runs still require measurements and a durable
  incremental journal before clearing references safely.
- `ExecutionSessionStore`: retains up to 50 sessions including undo state. Clearing
  original bytes would break undo. Aggregate byte retention was not measured.
- Durable supervision: 20-run and 256-event limits constrain counts, not the bytes
  of task results/checkpoints. No live-size/root measurements were available.
- Harness channels, active controller timelines, request buffers, and snapshot
  captures can retain active content. Normal settlement paths remove capture
  dictionaries, but their behavior during a multi-hour real workload is unmeasured.
- A selected very large record is still deserialized fully, and large individual
  JSON strings can still require sizable serializer/parser buffers. Search is
  incremental by session; persistence has not become an append-only event store.

No unmeasured journal/cache/snapshot refactor was introduced. To complete the
real-instance phase, the active PID/address is required. A passive process/counter
time series can then correlate real peaks with active sessions and operations.
Object roots and native-versus-managed attribution remain unavailable; more
invasive heap inspection requires separate user authorization. A many-hour soak
and proof that RAM is proportional solely to active work remain outstanding.
