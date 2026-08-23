# Preserved first Native inference and SSE runner failure

The repaired first-turn binding allowed the Native read request to reach the
exact local model. The Host log records five proposals to complete without
calling the required read tool; all five were correctly rejected. This is the
authoritative first model outcome and is classified as model behavior.

The PowerShell `Invoke-WebRequest` client then remained attached to the SSE
response beyond its configured 600-second timeout after Ollama activity had
ended. The runner was stopped without stopping the isolated API or Ollama. That
transport problem is classified separately as a benchmark validation runner
defect.

The next repair cycle uses incremental SSE consumption and terminates only on
a typed terminal event. Any repaired-run Native read result is infrastructure
repair evidence and does not replace this original failed model outcome. The
original request, pre-turn snapshot, workspace registration, preflight, and
isolated API log remain preserved.
