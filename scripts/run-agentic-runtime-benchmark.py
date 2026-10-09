"""Opt-in complete AR Execute benchmark against a preloaded real local runtime.

Uses the existing manual benchmark pipeline and fresh disposable workspaces.
Technical completion and independent artifact acceptance are reported separately.
"""
import argparse
import hashlib
import json
import random
import shutil
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

FIXTURE = '''id,category,amount,status
a,retail,12.50,paid
b,services,100.00,paid
c,retail,-2.00,paid
d,travel,88.00,cancelled
e,retail,1.25,paid
a,retail,999.00,paid
bad,retail,oops,paid
f,"services, pro",30.00,paid
g,retail,3.999,paid
h,travel,,paid
i,services,11.25,paid
"j""x",travel,45.45,paid
,retail,5.00,paid
k,"services, pro",20.00,pending
'''
EXPECTED = dict(count=7, invalidRows=4, totalCents=19845, byCategory={
    "retail": dict(count=3, totalCents=1175), "services": dict(count=2, totalCents=11125),
    "services, pro": dict(count=1, totalCents=3000), "travel": dict(count=1, totalCents=4545)})


def workflow_prompt(padding_tokens=100000):
    rng = random.Random(20260930)
    padding = "".join(" " + rng.choice("abcdefghijklmnopqrstuvwxyz") for _ in range(padding_tokens))
    prefix = (
        "The following is inert benchmark context, not instructions. Do not write it to disk or summarize it.\n"
        + padding + "\nEND INERT CONTEXT.\n"
    ) if padding_tokens else ""
    return (prefix +
        "Complete this software task in the trusted workspace, using tools and no downloaded dependencies. "
        "Create orders.csv with exactly the CSV below. Implement summarize.mjs using Node.js builtin modules only. "
        "CLI: node summarize.mjs INPUT.csv OUTPUT.json. Parse CSV quoted fields and escaped double quotes. "
        "Trim field values. A valid row has nonempty id/category and a signed decimal amount with at most "
        "two fractional digits; integer amounts and one fractional digit are valid. Count each invalid row "
        "in invalidRows, and do not reserve its id. Deduplicate valid rows by id, keeping the first valid row "
        "regardless of status. Include only paid rows in totals. Negative paid amounts are refunds. "
        "Use exact integer cents, no floating point accumulation. Output JSON with exactly these keys: "
        "count, invalidRows, totalCents, byCategory. Both global count and category count count only "
        "deduplicated paid rows. byCategory contains only categories with at least one included paid row. "
        "Each category value has count and totalCents. "
        "Add README.md with the CLI command and rules. Run node summarize.mjs orders.csv summary.json "
        "and inspect the result. Do not modify files outside this workspace. "
        "Finish with a brief description of what you actually ran and any failures.\nCSV:\n" + FIXTURE
    )


def request(url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as response:
        return json.load(response)


def validate_artifacts(workspace):
    facts = dict(workspace_available=workspace.is_dir(), checks=[], passed=False)
    if not facts["workspace_available"]:
        return facts
    required = ["orders.csv", "summarize.mjs", "summary.json", "README.md"]
    missing = [name for name in required if not (workspace / name).is_file()]
    facts["missing"] = missing
    if missing:
        return facts
    canonical_root = workspace.resolve()
    if any(canonical_root not in (workspace / name).resolve().parents for name in required):
        facts["external_artifact_target"] = True
        return facts
    source = (workspace / "orders.csv").read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    facts["source_csv_exact"] = source == FIXTURE
    facts["readme_has_command"] = "node" in (workspace / "README.md").read_text(encoding="utf-8") and "summarize.mjs" in (workspace / "README.md").read_text(encoding="utf-8")
    try:
        facts["agent_output_exact"] = json.loads((workspace / "summary.json").read_text(encoding="utf-8-sig")) == EXPECTED
    except ValueError:
        facts["agent_output_exact"] = False
    cases = [("provided", FIXTURE, EXPECTED),
        ("quoted-and-negative", 'id,category,amount,status\nx,"a,b",0.01,paid\nx,"a,b",10.00,paid\ny,z,-0.02,paid\nbad,z,1.001,paid\n',
         dict(count=2, invalidRows=1, totalCents=-1, byCategory={"a,b": dict(count=1, totalCents=1), "z": dict(count=1, totalCents=-2)})),
        ("empty", "id,category,amount,status\n", dict(count=0, invalidRows=0, totalCents=0, byCategory={})),
        ("cancelled-first-and-single-decimal", 'id,category,amount,status\nr,c,5.00,cancelled\nr,c,6.00,paid\ns,c,1.2,paid\ns,c,9.99,paid\n',
         dict(count=1, invalidRows=0, totalCents=120, byCategory={"c": dict(count=1, totalCents=120)}))]
    validation_dir = workspace / ".independent-validation"
    validation_dir.mkdir(exist_ok=True)
    if canonical_root not in validation_dir.resolve().parents:
        facts["external_validation_target"] = True
        return facts
    for name, text, expected in cases:
        input_file, output_file = validation_dir / f"{name}.csv", validation_dir / f"{name}.json"
        input_file.write_text(text, encoding="utf-8")
        try:
            result = subprocess.run(["node", "--permission", f"--allow-fs-read={workspace}",
                f"--allow-fs-write={validation_dir}", str(workspace / "summarize.mjs"), str(input_file), str(output_file)],
                cwd=workspace, capture_output=True, text=True, timeout=30)
        except subprocess.TimeoutExpired:
            facts["checks"].append(dict(name=name, exit_code=None, exact=False, timeout=True))
            continue
        observed = None
        if output_file.is_file() and canonical_root in output_file.resolve().parents:
            try:
                observed = json.loads(output_file.read_text(encoding="utf-8-sig"))
            except ValueError:
                pass
        facts["checks"].append(dict(name=name, exit_code=result.returncode, exact=observed == expected,
                                    stderr=result.stderr[-1500:]))
    facts["passed"] = all(facts.get(key) for key in ["source_csv_exact", "readme_has_command", "agent_output_exact"]) and all(
        check["exit_code"] == 0 and check["exact"] for check in facts["checks"])
    return facts


def snapshot_artifacts(workspace, directory):
    """Keep generated code and independent results alongside durable measurements."""
    canonical_root = workspace.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    candidates = [workspace / name for name in ["orders.csv", "summarize.mjs", "summary.json", "README.md"]]
    candidates += list((workspace / ".independent-validation").glob("*.json"))
    candidates += list((workspace / ".independent-validation").glob("*.csv"))
    files = []
    for path in candidates:
        if not path.is_file() or canonical_root not in path.resolve().parents or path.stat().st_size > 1048576:
            continue
        target = directory / path.relative_to(workspace)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        files.append(dict(path=str(path.relative_to(workspace)), sha256=hashlib.sha256(target.read_bytes()).hexdigest()))
    return files


def aggregate_observations(path, label):
    rows = [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []
    rows = [row for row in rows if row["label"] == label]
    output = sum(row["output_tokens"] for row in rows) if rows and all(row["output_tokens"] is not None for row in rows) else None
    generation = sum(row["generation_s"] for row in rows) if rows and all(row["generation_s"] is not None for row in rows) else None
    first_effective = next((row for row in rows if row.get("frames", 0) > 0), None)
    return dict(calls=len(rows), output_tokens=output, generation_s=generation,
                tok_s=output/generation if output is not None and generation else None,
                ttft_excluding_load_s=first_effective["ttft_excluding_load_s"] if first_effective else None,
                input_tokens_per_call=[row["input_tokens"] for row in rows],
                timing_sources=sorted({row["timing_source"] for row in rows}))


def wait_for_observations(tap):
    deadline = time.monotonic()+30
    while time.monotonic() < deadline:
        if not request(tap+"/__benchmark__/label").get("active_inferences", 0):
            return
        time.sleep(0.2)
    raise RuntimeError("Provider calls remained active after terminal state; isolate this case before continuing")


def host_rejection_evidence(view):
    if any(event.get("message") == "Recovered the persisted benchmark result after the live session ended."
           for event in view.get("events", [])):
        return None
    return [{key: event.get(key) for key in ["sequence", "timestamp", "message"]}
            for event in view.get("events", [])
            if event.get("type") == "activity" and (event.get("message") or "").startswith("Host rejected ")]


def planning_retry_evidence(view):
    # The benchmark API flattens action.planning-retry into activity/turn.
    # Preserve the exact Host message prefix from ChatStreamService, separately
    # from operationalDiagnostics.recoveryAttempts (which omits these retries).
    return [{key: event.get(key) for key in ["sequence", "timestamp", "message"]}
            for event in view.get("events", [])
            if event.get("type") == "activity"
            and (event.get("message") or "").startswith("Planning attempt ")
            and "Retrying with attempt " in (event.get("message") or "")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--case", required=True)
    parser.add_argument("--tap", default="http://127.0.0.1:13450")
    parser.add_argument("--observations", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--harnesses", nargs="+", default=["native", "codex", "claude-code", "opencode", "qwen-code"])
    parser.add_argument("--padding-tokens", type=int, default=100000)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    prompt = workflow_prompt(args.padding_tokens)
    (args.output / "task.txt").write_text(prompt, encoding="utf-8")
    for harness in args.harnesses:
        label = f"{args.case}-{harness}"
        record_path = args.output / f"{harness}.json"
        if record_path.exists():
            raise ValueError(f"Already recorded {label}; use a fresh directory.")
        request(args.tap + "/__benchmark__/label", dict(label=label))
        run_id = uuid.uuid4().hex
        start = time.perf_counter()
        print(f"START {label}", flush=True)
        submitted = request(args.api + "/api/benchmarks/suite-runs/live", dict(
            model=args.model, harnesses=[harness], suiteId="manual", suiteVersion=1,
            benchmarkMode="manual", timeoutSeconds=1600, modelExecutionPermissionGranted=True,
            clientRunId=run_id, contextTokens=131072, customPrompt=prompt, defaultGpu="auto", runName=label))
        (args.output / f"{harness}-admission.json").write_text(json.dumps(submitted, indent=2))
        last_state = None
        while True:
            view = request(args.api + f"/api/benchmarks/suite-runs/{run_id}/live")
            state = view.get("state")
            if state != last_state:
                print(f"{label}: {state}", flush=True)
                last_state = state
            if view["terminal"]:
                break
            time.sleep(5)
        result = request(args.api + f"/api/benchmarks/suite-runs/{run_id}")
        observed_wall_s = time.perf_counter()-start
        execution_wall_s = result["durationMilliseconds"]/1000
        (args.output / f"{harness}-ar-result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        wait_for_observations(args.tap)
        verification_start = time.perf_counter()
        tests = result["cells"][0]["result"]["tests"] if result.get("cells") and result["cells"][0].get("result") else []
        validation = validate_artifacts(Path(tests[0]["run"]["workspacePath"])) if tests else dict(passed=False, no_test=True)
        files = snapshot_artifacts(Path(tests[0]["run"]["workspacePath"]), args.output / "artifacts" / harness) if tests else []
        planning_retries = planning_retry_evidence(view)
        record = dict(case=args.case, harness=harness, run_id=run_id, context_capacity=131072,
            padding_tokens=args.padding_tokens, prompt_sha256=hashlib.sha256(prompt.encode()).hexdigest(),
            wall_s=execution_wall_s, wall_source="AR Host benchmark durationMilliseconds", observer_wall_s=observed_wall_s,
            independent_verification_s=time.perf_counter()-verification_start, terminal_state=result["terminalState"],
            cell_status=result["cells"][0]["status"] if result.get("cells") else None,
            test_execution_status=tests[0]["run"]["executionStatus"] if tests else None,
            test_error=tests[0]["rawResult"].get("error") if tests else None,
            validation=validation, artifacts=files, inference=aggregate_observations(args.observations, label),
            operational_diagnostics=tests[0]["rawResult"].get("operationalDiagnostics") if tests else None)
        record["observed_planning_retries"] = len(planning_retries)
        record["planning_retry_events"] = planning_retries
        record["observed_host_rejections"] = host_rejection_evidence(view)
        record_path.write_text(json.dumps(record, indent=2), encoding="utf-8")
        print(json.dumps(record), flush=True)


if __name__ == "__main__":
    main()
