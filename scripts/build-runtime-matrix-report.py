"""Recompute reviewed benchmark tables from real samples and workflow evidence."""
import argparse
import csv
import json
import re
import statistics
from datetime import datetime, timezone
from pathlib import Path

HARNESSES = ["native", "codex", "claude-code", "opencode", "qwen-code"]
GPUS = [("amd-rocm", "RX 7900 XTX", "ROCm"), ("amd-vulkan", "RX 7900 XTX", "Vulkan"),
        ("nvidia-cuda", "RTX 4090", "CUDA"), ("nvidia-vulkan", "RTX 4090", "Vulkan")]


def read(path, default=None):
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.is_file() else default


def jsonl(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()] if path.is_file() else []


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--complete", action="store_true")
    parser.add_argument("--report-project", type=Path)
    args = parser.parse_args()
    root = args.root
    plan = read(root / "matrix-plan.json", {})
    targets = plan.get("depths", [131072])
    upper_target = targets[-1]
    short_target = targets[0] if len(targets) > 1 else None
    capacity = plan.get("capacity", 132096)
    progress = read(root / "matrix-progress.json", {})
    stages = progress.get("stages", [])
    engines, workflows, curves, responsiveness, vulkan, engine_files, workflow_files, latency_files, vulkan_files, violations = [], [], [], [], [], [], [], [], [], []
    paths = read(root / "engine-result-paths.json", {})
    observations = jsonl(root / "workflow-inferences.jsonl")
    for gpu_key, gpu, backend in GPUS:
        for kv in ["q8", "q4"]:
            for runtime in ["ollama", "lmstudio"]:
                case = f"{gpu_key}-{runtime}-{kv}"
                directory = Path(paths.get(case, str(root / "engines" / case)))
                if targets == [131072] and gpu_key == "amd-rocm" and runtime == "ollama" and kv == "q8":
                    directory = root.parent / "runtime-benchmark-amd-20260930"
                if targets == [131072] and gpu_key == "nvidia-cuda":
                    directory = root.parent / "runtime-benchmark-20260930"
                    if kv == "q4":
                        directory /= "kv-q4-128k"
                raw = jsonl(directory / "samples.jsonl")
                selected = [row for row in raw if row.get("runtime") == runtime and row.get("phase") == "measured"
                            and row.get("target_input_tokens") == upper_target and row.get("valid")]
                short = [row for row in raw if short_target is not None and row.get("runtime") == runtime and row.get("phase") == "measured" and row.get("target_input_tokens") == short_target and row.get("valid")]
                failures = [row for row in stages if row.get("case") == case and row.get("state") == "failed"]
                workflow_stages = [row for row in stages if row.get("case") == case and row.get("stage") == "workflows"]
                meta = read(directory / f"metadata-{runtime}.json", {})
                engine = dict(case=case, label=f"{gpu} · {backend} · {runtime}", gpu=gpu, backend=backend, runtime=runtime,
                    kv=kv.upper(), status="measured" if len(selected) == 3 and (short_target is None or len(short) == 3) else "incomplete" if selected or short else "failed" if failures else "pending",
                    capacity=capacity, samples=len(selected), input_tokens=upper_target, output_tokens=512,
                    short_samples=len(short), short_input_tokens=short_target, short_tok_s=None, short_ttft_s=None, short_prefill_s=None,
                    tok_s=None, tok_s_min=None, tok_s_max=None, ttft_s=None, prefill_s=None, generation_s=None,
                    loading_s=None, error=failures[-1].get("error") if failures else None)
                loading = read(directory / "engine-loading.json")
                if loading is None:
                    loading = read(root / "engine-configurations" / f"{case}-loading-{capacity}.json")
                if loading is None:
                    loading = read(root / "engine-configurations" / f"{case}-loading-131072.json")
                if loading is None:
                    loading = read(directory / f"loading-{runtime}.json", {})
                engine["loading_s"] = loading.get("loading_stage_wall_s")
                for row in selected+short:
                    if row["input_tokens"] != row["target_input_tokens"] or row["output_tokens"] != 512:
                        violations.append(f"Token counts are not exact for {case}")
                    if abs(row["tok_s"] - row["output_tokens"]/row["generation_s"]) > 1e-7:
                        violations.append(f"Throughput does not reconcile for {case}")
                    if row.get("load_s") is not None and row["load_s"] >= 0.25:
                        violations.append(f"Loading contaminated a measured sample for {case}")
                if (selected or short) and meta.get("capacity") != capacity:
                    violations.append(f"Loaded context capacity differs from the protocol for {case}")
                proof = read(root / "engine-configurations" / f"{case}-capacity-evidence.json", {})
                effective_capacity = meta.get("effective_capacity") or proof.get("effective_capacity")
                engine["effective_capacity"] = effective_capacity
                if (selected or short) and effective_capacity != capacity:
                    violations.append(f"Effective native context capacity is unverified or differs for {case}")
                if short:
                    for key, source_key in [("short_tok_s", "tok_s"), ("short_ttft_s", "ttft_excluding_load_s"), ("short_prefill_s", "prefill_s")]:
                        values = [row[source_key] for row in short if row.get(source_key) is not None]
                        engine[key] = statistics.median(values) if values else None
                    engine_files.append(str((directory / "samples.jsonl").relative_to(root.parent)))
                if selected:
                    for result_key, raw_key in [("tok_s", "tok_s"), ("ttft_s", "ttft_excluding_load_s"),
                                                ("prefill_s", "prefill_s"), ("generation_s", "generation_s")]:
                        values = [row[raw_key] for row in selected if row.get(raw_key) is not None]
                        engine[result_key] = statistics.median(values) if values else None
                    engine["tok_s_min"] = min(row["tok_s"] for row in selected)
                    engine["tok_s_max"] = max(row["tok_s"] for row in selected)
                    engine_files.append(str((directory / "samples.jsonl").relative_to(root.parent)))
                telemetry_file = directory / f"gpu-{runtime}.csv"
                if telemetry_file.is_file():
                    with telemetry_file.open(encoding="utf-8-sig") as handle:
                        all_telemetry = list(csv.DictReader(handle))
                        telemetry = [row for row in all_telemetry if f"{upper_target}-measured" in row.get("stage", "")]
                    memory_key = "dedicated_memory_mib" if gpu_key.startswith("amd") else "memory.used"
                    memory = [float(row[memory_key])/1024 for row in telemetry if row.get(memory_key)]
                    engine["gpu_memory_median_gib"] = statistics.median(memory) if memory else None
                    engine["gpu_memory_peak_gib"] = max(memory) if memory else None
                    engine["gpu_memory_source"] = "WDDM dedicated allocation" if gpu_key.startswith("amd") else "NVIDIA SMI memory.used"
                    memory_short = [float(row[memory_key])/1024 for row in all_telemetry if row.get(memory_key) and short_target is not None and f"{short_target}-measured" in row.get("stage", "")]
                    engine["gpu_memory_short_median_gib"] = statistics.median(memory_short) if memory_short else None
                facts_file = root / "engine-configurations" / f"{case}-engine-facts.txt"
                if facts_file.exists():
                    values = re.findall(r"KV buffer size\s*=\s*([\d.]+) MiB", facts_file.read_text())
                    engine["native_kv_allocation_mib"] = float(values[-1]) if values else None
                for evidence_file in [directory / f"metadata-{runtime}.json", telemetry_file, directory / "engine-loading.json", facts_file,
                                      root / "engine-configurations" / f"{case}-capacity-evidence.json",
                                      root / "engine-configurations" / f"{case}-flags-{capacity}.json"]:
                    if evidence_file.exists():
                        engine_files.append(str(evidence_file.relative_to(root.parent)))
                engines.append(engine)
                latency_dirs = sorted((root / "responsiveness").glob(case+"*")) if (root / "responsiveness").exists() else []
                latency_dirs = [path for path in latency_dirs if path.name == case or path.name.startswith(case+"-retry-")]
                completed_latency = [path for path in latency_dirs if (path / "summary.json").exists()]
                if completed_latency:
                    latency_directory = completed_latency[-1]
                    latency_rows = jsonl(latency_directory / "samples.jsonl")
                    latency_meta = read(latency_directory / "metadata.json", {})
                    scenarios = latency_meta.get("scenarios", ["short-public-chat", "fresh-2k-raw", "incremental-128k-raw"])
                    if latency_meta.get("capacity") != capacity or latency_meta.get("prefix_tokens") != plan.get("latency_prefix_tokens", 30000):
                        violations.append(f"Responsiveness context/prefix differs from the protocol for {case}")
                    for scenario in scenarios:
                        rows = [row for row in latency_rows if row.get("scenario") == scenario and row.get("phase") == "measured" and row.get("valid")]
                        item = dict(case=case, label=engine["label"], gpu=gpu, backend=backend, runtime=runtime, kv=kv.upper(), scenario=scenario, samples=len(rows))
                        for key in ["ttft_excluding_load_s", "first_visible_s", "prefill_s", "cached_input_tokens", "input_tokens"]:
                            values = [row[key] for row in rows if row.get(key) is not None]
                            item[key] = statistics.median(values) if values else None
                        item["cache_evidence"] = "provider cache count" if rows and all(row.get("cached_input_tokens") is not None for row in rows) else "cache count unavailable; inspect prefill/counts"
                        responsiveness.append(item)
                        if len(rows) != 3 or any(row.get("load_s", 0) is not None and row.get("load_s", 0) >= .25 for row in rows):
                            violations.append(f"Invalid responsiveness repetitions/loading for {case}/{scenario}")
                    latency_files.append(str((latency_directory / "samples.jsonl").relative_to(root.parent)))
                depths = sorted({row.get("target_input_tokens") for row in raw if row.get("valid")})
                for depth in depths if len(depths) > 2 else []:
                    rows = [row for row in raw if row.get("runtime") == runtime and row.get("phase") == "measured"
                            and row.get("target_input_tokens") == depth and row.get("valid")]
                    if rows and kv == "q8":
                        curves.append(dict(case=case, label=engine["label"], context_tokens=depth,
                            tok_s=statistics.median(row["tok_s"] for row in rows), samples=len(rows)))
                for harness in HARNESSES:
                    workflow = dict(case=case, gpu=gpu, backend=backend, runtime=runtime, kv=kv.upper(), harness=harness,
                        path="AR / Execute" if runtime == "ollama" else "vendor CLI / LM Studio",
                        status="pending", quality=None, technical=None, wall_s=None, calls=None, output_tokens=None,
                        generation_s=None, tok_s=None, ttft_s=None, tools=None, tool_failures=None, recovery=None,
                        input_min=None, input_max=None, error=None)
                    if runtime == "lmstudio" and harness == "native":
                        workflow.update(status="unavailable", error="Native/AR has no LM Studio provider; user chose no product change")
                        workflows.append(workflow)
                        continue
                    path = root / "workflows" / (case+"-controlled") / f"{harness}.json"
                    result = read(path)
                    if result:
                        label = result.get("observation_label", result["case"]+"-"+harness)
                        rows = [row for row in observations if row["label"] == label]
                        exact_tokens = sum(row["output_tokens"] for row in rows) if rows and all(row.get("output_tokens") is not None for row in rows) else None
                        gen = sum(row["generation_s"] for row in rows) if rows and all(row.get("generation_s") is not None for row in rows) else None
                        inference = result["inference"]
                        expected_model = "ar-benchmark-qwen27b:controlled" if runtime == "ollama" else "ar-runtime-context-benchmark"
                        if any(row.get("model") != expected_model for row in rows):
                            violations.append(f"A workflow used a different model for {label}")
                        if any((row.get("settings", {}).get("options") or {}).get("num_ctx", capacity) != capacity for row in rows):
                            violations.append(f"A workflow requested a different context capacity for {label}")
                        if inference.get("output_tokens") != exact_tokens or inference.get("generation_s") != gen:
                            violations.append(f"Workflow inference sums do not reconcile for {label}")
                        counts = [row["input_tokens"] for row in rows if row.get("input_tokens") is not None]
                        diagnostics = result.get("operational_diagnostics") or {}
                        cli = result.get("cli") or {}
                        tools = result.get("tools") or {}
                        technical = result.get("test_execution_status") or result.get("terminal_state")
                        if runtime == "lmstudio":
                            technical = "timeout" if cli.get("timeout") else "completed" if cli.get("exit_code") == 0 else "failed"
                        cli_errors = [event.get("error") for event in cli.get("events", []) if event.get("error")]
                        terminal = next((event for event in reversed(cli.get("events", []))
                                         if event.get("type") == "result"), {})
                        acceptance = result["validation"]
                        artifact_issues = []
                        if acceptance.get("missing"):
                            artifact_issues.append("Missing files: " + ", ".join(acceptance["missing"]))
                        for key, explanation in [("source_csv_exact", "Source CSV differs from the required fixture"),
                                                 ("readme_has_command", "README lacks the required command"),
                                                 ("agent_output_exact", "Generated summary differs from the expected JSON")]:
                            if acceptance.get(key) is False:
                                artifact_issues.append(explanation)
                        artifact_issues += ["Independent fixture failed: " + check["name"]
                                            for check in acceptance.get("checks", []) if not check.get("exact")]
                        workflow.update(status="executed", quality=bool(result["validation"].get("passed")), technical=technical,
                            wall_s=result["wall_s"], calls=len(rows), output_tokens=exact_tokens, generation_s=gen,
                            tok_s=exact_tokens/gen if exact_tokens is not None and gen else None,
                            ttft_s=rows[0].get("ttft_excluding_load_s") if rows else None,
                            tools=diagnostics.get("toolCalls", tools.get("observed_tool_calls")),
                            tool_failures=diagnostics.get("failedToolCalls", tools.get("observed_tool_failures")),
                            recovery=diagnostics.get("recoveryAttempts", tools.get("recovery_attempts")),
                            input_min=min(counts) if counts else None, input_max=max(counts) if counts else None,
                            error=result.get("test_error") or ("; ".join(cli_errors) if cli_errors else None) or cli.get("stderr") or None)
                        workflow["timing_sources"] = ", ".join(inference.get("timing_sources", []))
                        workflow["wall_source"] = result.get("wall_source")
                        workflow["planning_retries"] = result.get("observed_planning_retries")
                        rejection_evidence = result.get("observed_host_rejections")
                        rejection_path = path.with_name(harness + "-host-rejections.json")
                        if rejection_evidence is None and rejection_path.is_file():
                            rejection_evidence = read(rejection_path).get("events")
                            workflow_files.append(str(rejection_path.relative_to(root.parent)))
                        workflow["host_rejections_observed"] = len(rejection_evidence) if rejection_evidence is not None else None
                        workflow["prompt_template"] = "Original Ollama template" if runtime == "ollama" else "GGUF template with ordered system/developer aggregation"
                        known_counts = [row["output_tokens"] for row in rows if row.get("output_tokens") is not None]
                        first_effective = next((row for row in rows if row.get("frames", 0) > 0), None)
                        workflow["ttft_s"] = first_effective.get("ttft_excluding_load_s") if first_effective else None
                        workflow["calls_with_generation"] = sum(row.get("frames", 0) > 0 for row in rows)
                        workflow["native_router_errors"] = tools.get("native_router_errors")
                        workflow["partial_output_tokens"] = sum(known_counts) if exact_tokens is None and known_counts else None
                        workflow["calls_missing_usage"] = sum(row.get("output_tokens") is None for row in rows)
                        workflow["validation_checks"] = result["validation"].get("checks", [])
                        checks = acceptance.get("checks", [])
                        workflow["functional_checks_passed"] = (all(check.get("exit_code") == 0 and check.get("exact")
                                                                   for check in checks) if len(checks) == 4 else None)
                        if acceptance.get("source_csv_exact") is False:
                            csv_file = path.parent / "artifacts" / harness / "orders.csv"
                            task_file = path.parent / "task.txt"
                            if csv_file.is_file() and task_file.is_file():
                                expected_csv = task_file.read_text(encoding="utf-8").partition("\nCSV:\n")[2]
                                actual_csv = csv_file.read_text(encoding="utf-8-sig")
                                if expected_csv and actual_csv.splitlines() == expected_csv.splitlines():
                                    artifact_issues = ["CSV line content matches; final line terminator differs" if item == "Source CSV differs from the required fixture" else item for item in artifact_issues]
                        workflow["artifact_issues"] = "; ".join(artifact_issues) or None
                        host_error = result.get("test_error")
                        workflow["terminal_reason"] = (terminal.get("subtype") if terminal.get("is_error")
                                                        else host_error.get("code") if isinstance(host_error, dict) else None)
                        workflow_files.append(str(path.relative_to(root.parent)))
                    elif workflow_stages and workflow_stages[-1].get("state") == "failed":
                        workflow.update(status="setup-failed", error=next(row.get("error") or "See stage log" for row in reversed(failures) if row.get("stage") == "workflows"))
                    workflows.append(workflow)
    diagnostic = Path(plan.get("previous_extreme_results", root)) / "vulkan-investigation"
    allocation = read(diagnostic / "device-limit-and-allocations.json", {})
    buffers = {row["variant"]: row for row in allocation.get("allocation_measurements", [])}
    for variant in ["default-a", "4gib", "default-b"]:
        for stage in ["short-decode", "prefill-32k"]:
            path = diagnostic / variant / stage / "samples.jsonl"
            selected = [row for row in jsonl(path) if row.get("phase") == "measured" and row.get("valid")]
            if not selected:
                continue
            item = dict(variant=variant, stage=stage, capacity=131072, samples=len(selected),
                input_tokens=selected[0]["input_tokens"], output_tokens=selected[0]["output_tokens"],
                requested_block_gib=4 if variant == "4gib" else None,
                effective_block_gib=allocation.get("requested_4gib_effective_gib") if variant == "4gib" else 1,
                peak_device_buffers=buffers.get(variant, {}).get("peak_active_device_buffers"),
                status="measured", tok_s=statistics.median(row["tok_s"] for row in selected),
                prefill_s=statistics.median(row["prefill_s"] for row in selected),
                ttft_s=statistics.median(row["ttft_excluding_load_s"] for row in selected))
            vulkan.append(item)
            vulkan_files.append(str(path.relative_to(root.parent)))
            if any(row.get("load_s", 0) >= .25 or abs(row["tok_s"]-row["output_tokens"]/row["generation_s"]) > 1e-7 for row in selected):
                violations.append(f"Vulkan diagnostic timing does not reconcile: {variant}/{stage}")
    for result in read(diagnostic / "full-prefill/results.json", []):
        variant = result["variant"]
        selected = [row for row in jsonl(diagnostic / "full-prefill" / variant / "samples.jsonl") if row.get("valid")]
        item = dict(variant=variant, stage="fresh-prefill-128k", capacity=132096, samples=len(selected),
            input_tokens=131072, output_tokens=64, status=result["status"],
            requested_block_gib=4 if variant == "4gib" else None,
            effective_block_gib=allocation.get("requested_4gib_effective_gib") if variant == "4gib" else 1,
            peak_device_buffers=None, tok_s=None, prefill_s=None, ttft_s=None)
        if selected:
            for key, sample_key in [("tok_s", "tok_s"), ("prefill_s", "prefill_s"), ("ttft_s", "ttft_excluding_load_s")]:
                item[key] = statistics.median(row[sample_key] for row in selected)
        vulkan.append(item)
        vulkan_files.append(str((diagnostic / "full-prefill" / variant / "samples.jsonl").relative_to(root.parent)))
    sources = dict(label="Real authorized local benchmark samples and independent file validation",
        caveats=["Synthetic benchmark input; all inference and usage are real.",
            "Raw engine cells use 3 measured repetitions plus warmup. Tool workflows use one execution per cell.",
            f"Configured capacity is {capacity}; engine inputs are {targets}. Tool task uses no inert padding in the corrected usage protocol; provider input includes harness instructions/history.",
            "Engine timings come from providers; workflow timings may use first-to-last generated streaming frames.",
            "Unavailable metrics are null, never zero.", "AMD WDDM telemetry is not directly comparable to NVIDIA SMI."],
        evidenceFlow=[dict(title="Reproducible source", detail="Run scripts/build-runtime-matrix-report.py --root RESULTS. Read samples.jsonl, workflow-inferences.jsonl and per-harness JSON; recompute medians and weighted token rates.")],
        metricDefinitions=[dict(label="Throughput", definition="Output tokens divided by generation duration; never divided by total workflow time.",
            componentIds=["engine-short-speed", "engine-speed", "engine-table", "workflow-table", "engine-findings", "verdict"], formula="output_tokens / generation_s"),
            dict(label="TTFT", definition=f"Dispatch of first effective inference to first generated frame, with provider loading subtracted or explicitly preloaded model. Configured capacity {capacity}; input workloads {targets} measured separately, including prompt processing. Responsiveness table: public chat and incremental inputs, measured separately.",
                 componentIds=["engine-table", "engine-ttft", "workflow-table", "engine-findings", "verdict"]),
            dict(label="Loading", definition="Separate wall time of explicit model load stage, excluded from TTFT.", componentIds=["engine-table", "engine-findings", "verdict"]),
            dict(label="Execution total", definition="AR Host benchmark durationMilliseconds or standalone CLI process start to exit. Preloading is a separate stage. Independent observer verification and evidence copying occur afterward and are excluded.", componentIds=["workflow-table"]),
            dict(label="Artifact correctness", definition="Exact fixture, four independent node executions, exact JSON results and README command. Technical completion alone does not pass.",
                 componentIds=["workflow-table", "workflow-findings", "verdict"])])
    completed_engines = sum(row["status"] == "measured" for row in engines)
    executed = [row for row in workflows if row["status"] == "executed"]
    stats = dict(engine_cells_measured=completed_engines, engine_cells_planned=16, workflow_cells_executed=len(executed),
        workflow_cells_available=72, workflow_cells_unavailable=8, artifact_passes=sum(row["quality"] for row in executed),
        technical_completions=sum(row["technical"] == "completed" for row in executed))
    stats["artifact_and_technical_success"] = sum(row["quality"] and row["technical"] == "completed" for row in executed)
    stats["functional_passes"] = sum(row.get("functional_checks_passed") is True for row in executed)
    stats["functional_verified"] = sum(row.get("functional_checks_passed") is not None for row in executed)
    stats["formal_newline_failures"] = sum("final line terminator differs" in (row.get("artifact_issues") or "") for row in executed)
    stats["engine_workloads_measured"] = sum(row["samples"] == 3 for row in engines)+sum(row["short_samples"] == 3 for row in engines) if short_target is not None else completed_engines
    stats["engine_workloads_planned"] = 16*len(targets)
    stats["responsiveness_scenarios_measured"] = sum(row["samples"] == 3 for row in responsiveness)
    stats["responsiveness_scenarios_planned"] = 48
    coverage_complete = completed_engines == 16 and len(executed) == 72 and stats["responsiveness_scenarios_measured"] == 48
    retry = read(root / "infrastructure-retries-pending.json", {})
    if args.complete and retry and retry.get("status") != "completed":
        raise ValueError("Cannot mark the report complete while an infrastructure retry is pending.")
    if args.complete and (not coverage_complete or violations):
        raise ValueError("Cannot mark the report complete before coverage and measurement validation pass.")
    completed_dates = [row["utc"][:10] for row in stages if row.get("state") == "completed"]
    cutoff = max(completed_dates, default="2026-09-30")
    engine_source = dict(sources, sourceFiles=sorted(set(engine_files)))
    engine_source["sourceFiles"] += [str(path.relative_to(root.parent)) for path in
                                   [root / "benchmark-environment.json", root / "host-hardware.json", root / "video-drivers.json", root / "gguf-architecture-facts.json", root / "lmstudio-installed-runtimes.txt", root / "ollama-environment-defaults-observed.json"] if path.exists()]
    workflow_source = dict(sources, sourceFiles=sorted(set(workflow_files)))
    stock_failure = root / "lmstudio-stock-template-failure.json"
    if stock_failure.exists():
        workflow_source["sourceFiles"] += [str(stock_failure.relative_to(root.parent))]
    template_evidence = root / "engine-configurations/qwen-workflow-compatible.evidence.json"
    if template_evidence.exists():
        workflow_source["sourceFiles"] += [str(template_evidence.relative_to(root.parent))]
        workflow_source["caveats"] = sources["caveats"] + [
            "LM Studio tool workflows use an isolated loaded-instance template that aggregates system/developer blocks in order; the original GGUF body, raw engine tests and transparent transport remain unchanged."]
        workflow_source["linkedSources"] = [dict(title="LM Studio issue 2298", url="https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2298",
            detail="Multiple developer messages are mapped to separate system messages and rejected by the stock Qwen template. Local Codex logs reproduced that failure before generation; ordered instruction aggregation is tested only on benchmark instances.")]
    retry_evidence = root / "infrastructure-retries-pending.json"
    if retry_evidence.is_file():
        retry_record = read(retry_evidence)
        workflow_source["sourceFiles"].append(str(retry_evidence.relative_to(root.parent)))
        original_record = root / retry_record.get("original_record", "")
        if original_record.is_file():
            workflow_source["sourceFiles"].append(str(original_record.relative_to(root.parent)))
        workflow_source["metricDefinitions"] += [dict(label="Infrastructure correction and preserved first attempt",
            definition="Codex/ROCm/LM Studio/Q8 was repeated with the standard Windows unelevated workspace sandbox after the initial configuration prevented commands. The original record remains archived. The main matrix uses the corrected attempt, including any remaining artifact failure; no other failed cell is repeated to select a favorable outcome.",
            componentIds=["workflow-table", "harness-summary", "workflow-findings", "verdict"]) ]
    latency_source = dict(sources, sourceFiles=sorted(set(latency_files)),
        metricDefinitions=[dict(label="Loaded-model responsiveness", definition=f"Three repetitions per scenario, capacity {capacity}, separate {plan.get('latency_prefix_tokens', 30000)}-token prime in the usage protocol. First generated frame includes reasoning; first visible content is reported separately for public chat. Incremental cache reuse is requested, never assumed; inspect native cache counts and prompt processing time.", componentIds=["responsiveness-table", "responsiveness-introduction", "engine-findings", "verdict"])])
    vulkan_source = dict(sources, sourceFiles=sorted(set(vulkan_files))+[str((diagnostic / "protocol.json").relative_to(root.parent)), str((diagnostic / "device-limit-and-allocations.json").relative_to(root.parent))],
        metricDefinitions=[dict(label="Vulkan A/B/A diagnostic", definition="RX 7900 XTX, official Ollama 0.34.2/llama.cpp 391fac164, same weights/KV Q8 and sampler. Default, requested 4 GiB, then default again. Driver maxMemoryAllocationSize/maxBufferSize are 2 GiB, applied by the backend. Memory logging uses separate loads and stays off during performance samples. Short decode n=3; 32K prefill n=1 after warmup; fresh 128K follow-up n=1 with 900-second wall deadline and process memory/native progress evidence.", componentIds=["vulkan-diagnostic-table", "vulkan-findings"])],
        linkedSources=[dict(title="llama.cpp issue 24066", url="https://github.com/ggml-org/llama.cpp/issues/24066", detail="Unconfirmed regression report for RX 6600/Linux/Qwen3.5-9B; hardware differs."), dict(title="llama.cpp issue 27734", url="https://github.com/ggml-org/llama.cpp/issues/27734", detail="RX 7900 XTX/Windows/128K with four unified slots and a different GGUF quant: reports a decode cliff with a short prompt; root cause stated as a hypothesis.")])
    harness_summary = []
    for runtime in ["ollama", "lmstudio"]:
        for harness in HARNESSES:
            relevant = [row for row in workflows if row["runtime"] == runtime and row["harness"] == harness]
            recorded = [row for row in relevant if row["status"] == "executed"]
            passed = [row for row in recorded if row["quality"]]
            durations = [row["wall_s"] for row in passed if row.get("wall_s") is not None]
            harness_summary.append(dict(runtime=runtime, harness=harness, planned=len(relevant),
                executed=len(recorded), unavailable=sum(row["status"] == "unavailable" for row in relevant),
                artifact_passes=len(passed), functional_passes=sum(row.get("functional_checks_passed") is True for row in recorded),
                functional_verified=sum(row.get("functional_checks_passed") is not None for row in recorded),
                technical_completions=sum(row["technical"] == "completed" for row in recorded),
                successful_duration_min_s=min(durations) if durations else None,
                successful_duration_max_s=max(durations) if durations else None))
    summary_source = dict(workflow_source, metricDefinitions=[dict(label="Observed outcomes across configurations",
        definition="Counts of single runs across GPU/backend/KV configurations, separated by execution path. Full artifact acceptance, four verified functional fixtures and technical completion are separate. Duration range includes only full artifact passes; this is descriptive coverage, not repeated-run statistical evidence.",
        componentIds=["harness-summary"])])
    snapshot = dict(surface="report", title="Qwen 27B: capacidade de 128K e prompts de uso real" if short_target is not None else "Qwen 27B em 128K: GPUs, engines e execuÃ§Ãµes com tools",
        protocol=dict(capacity=capacity, targets=targets, workflow_padding_tokens=plan.get("workflow_padding_tokens"), previous_extreme_results=plan.get("previous_extreme_results")),
        generatedAt=datetime.now(timezone.utc).isoformat(), status="measured" if coverage_complete else "measured-partial",
        buildStatus="complete" if args.complete else "creating", report=dict(asOf=cutoff), filters=[], stats=stats,
        queries={"harness_summary": dict(rows=harness_summary, source=summary_source), "engines": dict(rows=engines, source=engine_source), "workflows": dict(rows=workflows, source=workflow_source),
                 "context_curve": dict(rows=curves, source=engine_source), "responsiveness": dict(rows=responsiveness, source=latency_source), "vulkan_diagnostic": dict(rows=vulkan, source=vulkan_source)})
    existing = read((args.report_project or root / "report-app") / "src/data.json", {})
    if existing.get("id"):
        snapshot["id"] = existing["id"]
    validation = dict(stats=stats, violations=violations, all_engine_counts_exact=not violations,
        complete_requested_coverage=coverage_complete,
        note="Setup failures require investigation; unavailable paths are distinct from unexecuted cells.")
    (root / "reviewed-snapshot.json").write_text(json.dumps(snapshot, indent=2), encoding="utf-8")
    (root / "analysis-validation.json").write_text(json.dumps(validation, indent=2), encoding="utf-8")
    for name, rows in [("harness-summary", harness_summary), ("engines", engines), ("workflows", workflows), ("responsiveness", responsiveness), ("vulkan-diagnostic", vulkan)]:
        columns = list(dict.fromkeys(key for row in rows for key in row if key != "validation_checks"))
        with (root / f"{name}-reviewed.csv").open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=columns, extrasaction="ignore")
            writer.writeheader()
            writer.writerows(rows)
    print(json.dumps(validation), flush=True)


if __name__ == "__main__":
    main()
