# Conversation rewind, snapshots and history files

Approved scope (2026-10-10): Edit in Chat rewinds immediately, restores the original
composer payload, and removes that turn and its descendants. Execute calls this
Rollback and also restores captured filesystem effects. The Host owns the operation
for every harness; new browser execution identity prevents native context reuse.

Updated scope (2026-10-11): snapshot capture is deferred until a permitted mutation
has exact target paths. The Host persists the original bytes and target manifest
before allowing the first mutation of each target in the turn. Subsequent writes
retain that first original. Creation records an absent original; rename records both
ends. Explicit directory mutations cover their targeted subtree. Read-only turns
capture no project files. Ignored files are included only when they are mutation
targets; unrelated external ignored changes do not justify a project-wide snapshot.

The existing bounded harness workspace observer remains separate: it enforces
trusted-root/effect verification and retains recovery evidence for native actions.
It does not imply an eager durable snapshot of the whole workspace. Opaque process
writes without predeclared paths can retain their original bytes from that observer
at settlement; this is weaker than the pre-write persistence guarantee for exact
filesystem targets and must not be represented as crash-safe pre-write capture.

Snapshots capture regular files inside the trusted workspace regardless of Git
ignore rules. Git metadata, AR's own data directory (when physically nested in a
project), and unsafe reparse traversal remain outside the existing
filesystem authority. Capture failure is explicit; never claim file rollback coverage
without a complete baseline. No branch changes, merges or per-prompt project commits.
Existing Git HEAD/index changes and later file changes are conflicts, not permission
to reset the repository. No-Git execution requires a window/workspace decision before
execution, or explicit initialization and an initial local commit.

Enabled history saves exact attachment bytes and rollback blobs in Host-owned
application data, separate from project snapshots. Disabled history uses a temporary window namespace; it is not resumable
after reload. Content is addressed by SHA-256 and reused without changing its bytes.
Persisted history is lossless; inference context fitting is a separate projection.

View files shows names, versions and originating conversations, never internal paths.
Opening/exporting materializes a copy. External applications cannot edit the stored
original. Compact history operates on archived conversations. Each item has one
permanent representation, raw or compressed. Verify decompression and exact digest
before removing raw storage. Reads materialize only requested content. Temporary
copies are disposable; they are not part of history. Deletion collects only content
without remaining references.

The existing configuration backup ZIP can read canonical conversations in either
raw or compressed form and retains its existing sanitization and size limits.
Restoring a conversation replaces its canonical representation and invalidates its
metadata cache. Uploaded bytes and filesystem snapshots remain outside the existing
ZIP categories; the backup screen states that boundary explicitly. Extending those
categories/limits is a separate product decision.

Host file work uses a folder/document SVG matching the existing brain's stroke style.
Host progress is distinct from model loading/generation. Enumeration has unknown
percentage; once work is known, a single operation percentage counts measured work
across its actions. Capture/compression/verification descriptions remain separate.
Per-action durations and work counters are saved for diagnosis when history is enabled.
No fabricated percentages or inference timing. Snapshot latency must be measured on
the actual tree, including ignored build output, without real model/GPU invocation.

Historical eager-capture measurement, superseded by targeted capture (2026-10-10): production enumeration covered 74,136
regular files / 19.78 GB after excluding Git metadata and the nested Host data root.
Avoiding repeated ancestry checks for every enumerated child reduced enumeration
from 112.3 s to 17.0 s; capture still revalidates full ancestry before reading bytes.
The subsequent capture was stopped at its 45 s measurement budget with 4.32 GB read
and 17,495 files captured. Its 62.3 s total is an incomplete lower bound, not a full
snapshot duration or model latency. No build/test artifact exclusions have been
applied; the coverage/performance decision remains with the user. Measurements use
temporary disposable storage and include no model or GPU inference.
