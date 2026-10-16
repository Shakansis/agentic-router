# Host file reading

`HostFileReadPolicy` owns admission by remaining context, physical read bounds,
text decoding, output sizing and continuation for `read_file` and `read_files`.
Chat, Native Execute and external harness Host bridges supply the remaining
context budget; they do not implement separate truncation policies.

The thresholds refer to **tokens per file**, as specified in the updated request:

- Up to 20,000 estimated text tokens: prefer a complete read.
- From 20,000 to 60,000: return the complete file when the budget permits.
- Above 60,000: return a selective range, with at most 20,000 tokens of response
  budget for that range. A requested offset selects the relevant starting point.

All tiers respect remaining context and memory. Remaining context subtracts the
existing messages, instructions, tool schemas and configured response reserve
(`Context.ReservedResponseTokens`) from the effective permitted context limit.
The Execute generation ceiling (50% of the window) is not a mandatory input
reservation. Native uses its existing planner request measurements, including
planner instructions. Chat also accounts for role/model runtime limits and may
raise the reserve to its actual configured generation allowance. Harness bridges
use reported context occupancy where available and charge Host results
received since the last report. Native-only instructions/occupancy may remain
estimated when the harness does not report them; configured capacity is not a
claim of exact occupancy.

An oversized file does not forbid a partial range. If useful read headroom is
exhausted, Chat and Native compact old tool evidence and assistant thinking in
active inference context only; user/system content, tool calls and result identity
remain. Full results remain in conversation activity. Compaction is explicit and
does not require a summarization inference. An external Host read with zero
headroom invokes the existing one-attempt context recovery with canonical
continuity and verified effects. Only deterministic read-context failures are
cleared for that materially different retry; security, mutation and unrelated
failure guards remain. Recovery cannot replace unresolved actions or decisions.
If mandatory context still cannot fit after bounded recovery, the existing typed
context failure remains; access boundaries are never bypassed through a process.

For Ollama Execute, Native resolution and the common harness transport also cap
generation by actual estimated input headroom. Context size, MTP, KV options,
batch size and keep_alive are preserved. This prevents admission of a fitting
read followed by rejection of its next inference because a generation ceiling
was incorrectly treated as occupied context. Estimates remain conservative,
not exact native tokenizer counts.

The existing conservative token estimator sizes the serialized response, including
nested JSON escaping, with 512 tokens reserved for result framing. Estimates are not exact
provider tokenizer counts. No inference is issued to estimate file size.

## Individual reads and continuation

An omitted range requests a complete read. A complete, fitting `read_file` still
returns plain text, preserving the existing protocol. Explicit ranges and partial
reads return JSON with `content`, `offsetBytes`, `lengthBytes`, `sizeBytes`,
`truncated`, `nextOffsetBytes`, `encoding`, `truncationReason`, `readMode`,
`availableContextTokens`, `budgetSource` and `maxRangeBytes`.

Continue using the exact `nextOffsetBytes`; null means the end was reached.
Offsets and lengths are bytes, not characters. UTF-8 and BOM-marked UTF-16/UTF-32
are decoded strictly. Partial responses end at a complete character boundary,
without replacement characters or skipped bytes. Invalid offsets/text are typed
validation failures. A range that cannot return any content because context is
exhausted is recoverable (`FILE_READ_CONTEXT_EXHAUSTED`), rather than a successful
zero-progress read. As with existing reads, continuation assumes the source has
not changed between requests.

The physical maximum increases from 128 KiB to 1 MiB per file per call. Explicit
`lengthBytes` uses the same maximum. A standalone caller without a model budget
retains the 128 KiB physical safeguard and receives explicit continuation metadata;
the response identifies that the context budget is unavailable.

## Batched reads

`read_files` accepts `files: [{ path, offsetBytes?, lengthBytes? }]`, ordered from
highest to lowest priority. Up to 128 files may be requested (bounded metadata and open-file resources); every file in the
admitted batch is read in parallel, with no additional concurrency ceiling.
Paths are validated through the existing trusted-root
policy before reading and again at execution. An invalid or missing requested
file rejects the batch; no partial success is claimed for that failed batch.

The response contains `files` identified by relative `path` and the aggregate
`availableContextTokens`. If all eligible contents fit, they are delivered together
in one tool result. Otherwise allocation follows request order; if a priority file
exhausts context, subsequent files are deferred. Complete files have compact
content and completion metadata; partial files retain range metadata. A deferred file has empty content and its original
offset as `nextOffsetBytes`. File buffers are bounded independently of file size:
at most 8 MiB of aggregate input buffers per batch, plus bounded decoded/serialized
representations. Files deferred by this memory budget expose `FILE_READ_MEMORY_EXHAUSTED`
and their continuation offset. Small files do not consume a 1 MiB reservation each.

`read_file` and `read_files` bypass the old 16,000-character protocol cut. All other
tool-result limits remain unchanged. No native harness reader is intercepted or
subjected to these Host read limits. Common guidance prefers complete and batched
reads when viable, and native parallel reading when the harness supports it.

## Validation

Deterministic browser/API E2E coverage verifies complete reads above 16,000
characters and 128 KiB, context-limited and selective responses, UTF-8/UTF-16 byte
continuation, complete batches in one tool call, priority and aggregate budgeting,
trusted-root rejection, batch-count bounds and the unchanged limit of other tools.
The existing external-harness Host bridge regression now expects usable partial
content directly, rather than a mandatory range-request round trip.
