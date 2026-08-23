# Preserved recovery-checkpoint runner limitation

Incremental SSE consumption worked and received the Native first-turn stream.
The exact model again exhausted five planning attempts by proposing completion
without the required read tool. The Host then correctly emitted a typed
`action.recovery-decision-required` checkpoint and waited; automatic mutation
approval does not choose a recovery policy.

The unattended runner had not yet implemented that explicit decision, so its
client was stopped without stopping the isolated API or Ollama. This is a
benchmark validation runner defect, separate from the repeated model-behavior
failure. The next repair cycle selects the offered `stop` option, which
preserves current effects, records the test as unsuccessful, and allows later
independent tests to continue. It never selects retry or specialist and does
not convert the failed objective into a pass.
