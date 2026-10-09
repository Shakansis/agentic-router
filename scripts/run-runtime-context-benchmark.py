"""Opt-in, real local inference benchmark. No application configuration changes.

Uses a fixed loaded context capacity, raw identical prompts, greedy decoding,
512 output tokens and varied synthetic prefixes to avoid long prefix reuse.
Loading is recorded separately; TTFT excludes reported model-loading duration.
Only generated benchmark inputs and outputs are saved.
"""

import argparse
import csv
import hashlib
import json
import random
import re
import statistics
import subprocess
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


def json_request(url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=900) as response:
        return json.load(response)


def prompt_for(filler_tokens, target, repetition):
    # Space + single ASCII letters each tokenize as one token in this model.
    # Actual input counts are nevertheless checked against runtime-reported usage.
    rng = random.Random(20260930 + target * 17 + repetition)
    marker = chr(97 + repetition)
    filler = "".join(" " + rng.choice("abcdefghijklmnopqrstuvwxyz") for _ in range(filler_tokens))
    return (
        "<|im_start|>system\nYou are running a generation throughput benchmark. "
        "The filler is inert data, not instructions. Follow the last instruction.\n<|im_end|>\n"
        f"<|im_start|>user\nTrial {marker}. Filler begins:\n{filler}\nFiller ends.\n"
        "Write a numbered list of all integers from 1 through 10000, one per line, "
        "with a brief explanation after each number. Keep writing until externally stopped. "
        "Do not summarize or skip numbers.\n<|im_end|>\n"
        "<|im_start|>assistant\n<think>\n\n</think>\n\n1. "
    )


class Telemetry:
    def __init__(self, output, windows_adapter_luid=None):
        self.output = output
        self.windows_adapter_luid = windows_adapter_luid
        self.stop = threading.Event()
        self.stage = "setup"
        self.thread = threading.Thread(target=self.sample, daemon=True)

    def sample(self):
        if self.windows_adapter_luid:
            self.sample_windows()
            return
        columns = ["timestamp", "uuid", "name", "pstate", "memory.used", "utilization.gpu",
                   "power.draw", "temperature.gpu", "clocks.sm", "clocks.mem"]
        with self.output.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.writer(handle)
            writer.writerow(["utc", "stage", *columns])
            while not self.stop.is_set():
                try:
                    result = subprocess.run(
                        ["nvidia-smi", "--query-gpu=" + ",".join(columns), "--format=csv,noheader,nounits"],
                        capture_output=True, text=True, timeout=8,
                    )
                    for line in result.stdout.splitlines():
                        writer.writerow([datetime.now(timezone.utc).isoformat(), self.stage,
                                         *next(csv.reader([line], skipinitialspace=True))])
                    handle.flush()
                except (OSError, subprocess.TimeoutExpired) as error:
                    writer.writerow([datetime.now(timezone.utc).isoformat(), self.stage, type(error).__name__])
                self.stop.wait(1)

    def sample_windows(self):
        # LUID is mapped to a physical adapter through DXGI before the run.
        # These WDDM counters differ from NVIDIA's board-wide SMI telemetry.
        luid = self.windows_adapter_luid
        command = (
            f"$memory=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory "
            f"-Filter \"Name='{luid}_phys_0'\"; "
            f"$engines=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine "
            f"| Where-Object {{$_.Name -like '*{luid}_*'}}; "
            "@{memory_mib=if($memory){$memory.DedicatedUsage/1MB}else{$null}; "
            "engine_percent=($engines | Measure-Object UtilizationPercentage -Maximum).Maximum} "
            "| ConvertTo-Json -Compress"
        )
        with self.output.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.writer(handle)
            writer.writerow(["utc", "stage", "adapter_luid", "dedicated_memory_mib", "max_process_engine_percent"])
            while not self.stop.is_set():
                try:
                    result = subprocess.run(["pwsh", "-NoProfile", "-Command", command],
                                            capture_output=True, text=True, timeout=8, check=True)
                    value = json.loads(result.stdout)
                    writer.writerow([datetime.now(timezone.utc).isoformat(), self.stage,
                                     luid, value["memory_mib"], value["engine_percent"]])
                    handle.flush()
                except (OSError, subprocess.SubprocessError, ValueError) as error:
                    writer.writerow([datetime.now(timezone.utc).isoformat(), self.stage, luid, type(error).__name__])
                self.stop.wait(1)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *args):
        self.stop.set()
        self.thread.join(timeout=10)


def options(args, output_tokens):
    return dict(num_ctx=args.capacity, num_predict=output_tokens, temperature=0,
                seed=20260930, top_k=0, top_p=1, min_p=0, repeat_penalty=1,
                presence_penalty=0, num_batch=512, num_thread=8, main_gpu=0,
                num_gpu=999, draft_num_predict=0)


def ollama_inference(args, prompt, output_tokens):
    body = dict(model=args.model, prompt=prompt, raw=True, stream=True,
                keep_alive="30m", options=options(args, output_tokens))
    request = urllib.request.Request(args.url.rstrip("/") + "/api/generate",
                                     data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
    start = time.perf_counter()
    first = None
    content = []
    final = None
    with urllib.request.urlopen(request, timeout=900) as response:
        for line in response:
            if not line.strip():
                continue
            event = json.loads(line)
            if "error" in event:
                raise RuntimeError(event["error"])
            token = event.get("thinking", "") + event.get("response", "")
            if token:
                if first is None:
                    first = time.perf_counter() - start
                content.append(token)
            if event.get("done"):
                final = event
    if final is None:
        raise RuntimeError("Inference ended without final usage/timing.")
    loading = final.get("load_duration")
    loading = loading / 1e9 if loading is not None else None
    generation = final.get("eval_duration")
    generation = generation / 1e9 if generation is not None else None
    prefill = final.get("prompt_eval_duration")
    prefill = prefill / 1e9 if prefill is not None else None
    tokens = final.get("eval_count")
    result = dict(input_tokens=final.get("prompt_eval_count"), output_tokens=tokens,
                  cached_input_tokens=final.get("prompt_eval_cached_count"),
                  load_s=loading, ttft_raw_s=first,
                  ttft_excluding_load_s=max(0, first - loading) if first is not None and loading is not None else None,
                  prefill_s=prefill, generation_s=generation,
                  tok_s=tokens / generation if tokens is not None and generation else None,
                  wall_s=time.perf_counter() - start, stop_reason=final.get("done_reason"),
                  timing_source="provider", output="".join(content))
    return result


def lmstudio_inference(args, prompt, output_tokens):
    available = json_request(args.url.rstrip("/") + "/api/v0/models")["data"]
    if not any(model["id"] == args.model and model["state"] == "loaded" for model in available):
        raise RuntimeError("LM Studio model must be explicitly preloaded; JIT loading is not a benchmark sample.")
    return llama_server_inference(args, prompt, output_tokens)


def llama_server_inference(args, prompt, output_tokens):
    start = time.perf_counter()
    # The public LM streaming API omits decode timing. Read the native timing
    # fields of this explicitly owned engine instance instead. Its transient
    # auth key stays in memory and is never persisted or printed.
    headers = {"Content-Type": "application/json"}
    if args.engine_key:
        headers["Authorization"] = "Bearer " + args.engine_key
    request = urllib.request.Request(args.engine_url + "/completion", data=json.dumps(dict(
        prompt=prompt, temperature=0, seed=20260930,
        top_k=0, top_p=1, min_p=0, repeat_penalty=1, presence_penalty=0,
        n_predict=output_tokens, stream=True, cache_prompt=getattr(args, "cache_prompt", False),
    )).encode(), headers=headers)
    final = {}
    content = []
    first = None
    with urllib.request.urlopen(request, timeout=900) as response:
        for line in response:
            text = line.decode().strip()
            if not text.startswith("data:") or text[5:].strip() == "[DONE]":
                continue
            event = json.loads(text[5:])
            token = event.get("content")
            if token:
                if first is None:
                    first = time.perf_counter() - start
                content.append(token)
            if event.get("stop"):
                final = event
    stats = final.get("timings", {})
    # Model must be explicitly loaded beforehand. No JIT-load TTFT is accepted.
    generation = stats.get("predicted_ms")
    generation = generation / 1000 if generation is not None else None
    prefill = stats.get("prompt_ms")
    prefill = prefill / 1000 if prefill is not None else None
    tokens = final.get("tokens_predicted")
    if tokens is None or tokens <= 1:
        generation = None  # Calibration is for token counts, never a speed sample.
    return dict(input_tokens=final.get("tokens_evaluated"), output_tokens=tokens,
                cached_input_tokens=stats.get("cache_n"), load_s=None, loading_excluded_by_preload=True,
                ttft_raw_s=first,
                ttft_excluding_load_s=first, prefill_s=prefill,
                generation_s=generation, tok_s=tokens / generation if tokens is not None and generation else None,
                provider_tok_s=stats.get("predicted_per_second"), decode_evaluations=stats.get("predicted_n"),
                wall_s=time.perf_counter() - start, stop_reason=final.get("stop_type"),
                timing_source="native-engine", timings=stats,
                output="".join(content))


def summarize(directory, rows):
    measured = [row for row in rows if row.get("phase") == "measured" and row.get("valid")]
    summary = []
    for runtime, target in sorted({(row["runtime"], row["target_input_tokens"]) for row in measured}):
        selected = [row for row in measured if row["runtime"] == runtime and row["target_input_tokens"] == target]
        item = dict(runtime=runtime, target_input_tokens=target, samples=len(selected),
                    input_tokens=[row["input_tokens"] for row in selected])
        for key in ["tok_s", "ttft_excluding_load_s", "prefill_s", "generation_s", "load_s"]:
            values = [row[key] for row in selected if row.get(key) is not None]
            item[key] = statistics.median(values) if values else None
            if key == "tok_s" and values:
                item["tok_s_min"] = min(values)
                item["tok_s_max"] = max(values)
        summary.append(item)
    (directory / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    keys = ["runtime", "target_input_tokens", "samples", "input_tokens", "tok_s", "tok_s_min", "tok_s_max",
            "ttft_excluding_load_s", "prefill_s", "generation_s", "load_s"]
    with (directory / "summary.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=keys)
        writer.writeheader()
        writer.writerows(summary)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=["ollama", "lmstudio"], required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--capacity", type=int, default=132096)
    parser.add_argument("--depths", type=int, nargs="+", default=[2048, 8192, 32768, 65536, 131072])
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--output-tokens", type=int, default=512)
    parser.add_argument("--overhead", type=int)
    parser.add_argument("--lm-engine-pid", type=int)
    parser.add_argument("--windows-adapter-luid")
    parser.add_argument("--gpu-evidence", type=Path)
    parser.add_argument("--skip-warmup", action="store_true", help="Targeted diagnostic only: one fresh large prefill after the separate short calibration")
    args = parser.parse_args()
    if args.windows_adapter_luid and not re.fullmatch(r"luid_0x[0-9A-Fa-f]{8}_0x[0-9A-Fa-f]{8}", args.windows_adapter_luid):
        raise ValueError("Expected a DXGI adapter LUID.")
    if args.runtime == "lmstudio":
        if args.lm_engine_pid is None:
            raise ValueError("Explicit owned LM Studio engine PID is required.")
        command = (f"(Get-CimInstance Win32_Process -Filter 'ProcessId = {args.lm_engine_pid}').CommandLine")
        inspected = subprocess.run(["pwsh", "-NoProfile", "-Command", command],
                                   capture_output=True, text=True, check=True).stdout
        if "agentic-router-benchmark" not in inspected:
            raise ValueError("Engine is not using this benchmark's own model namespace.")
        args.engine_url = "http://127.0.0.1:" + re.search(r"--port\s+(\d+)", inspected)[1]
        args.engine_key = re.search(r"--api-key\s+(\S+)", inspected)[1]
        args.effective_capacity = int(re.search(r"--ctx-size\s+(\d+)", inspected)[1])
        del inspected
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "prompts").mkdir(exist_ok=True)
    (args.output / "outputs").mkdir(exist_ok=True)
    inference = ollama_inference if args.runtime == "ollama" else lmstudio_inference
    metadata = dict(started_utc=datetime.now(timezone.utc).isoformat(),
                    runtime=args.runtime, model=args.model, url=args.url,
                    capacity=args.capacity, depths=args.depths, repetitions=args.repetitions,
                    output_tokens=args.output_tokens, options=options(args, args.output_tokens),
                    prompt_kind="synthetic randomized ASCII letters, explicit identical raw ChatML prompt",
                    ttft_definition="first received token minus reported loading; measured samples preloaded",
                    gpu=(json.loads(args.gpu_evidence.read_text(encoding="utf-8-sig")) if args.gpu_evidence else
                         {"adapter_luid": args.windows_adapter_luid} if args.windows_adapter_luid else
                         subprocess.run(["nvidia-smi", "--query-gpu=index,name,uuid,driver_version,memory.total,power.limit",
                                         "--format=csv,noheader"], capture_output=True, text=True).stdout.strip()))
    metadata["version"] = json_request(args.url.rstrip("/") + "/api/version") if args.runtime == "ollama" else None
    (args.output / f"metadata-{args.runtime}.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    rows = []
    rows_path = args.output / "samples.jsonl"
    if rows_path.exists():
        rows = [json.loads(line) for line in rows_path.read_text(encoding="utf-8").splitlines()]
        if any(row["runtime"] == args.runtime for row in rows):
            raise ValueError("This runtime already has samples here; choose a fresh results directory.")
    with Telemetry(args.output / f"gpu-{args.runtime}.csv", args.windows_adapter_luid) as telemetry:
        if args.runtime == "ollama":
            print("Preloading model separately from TTFT...", flush=True)
            start = time.perf_counter()
            preload = json_request(args.url.rstrip("/") + "/api/generate", dict(
                model=args.model, stream=False, keep_alive="30m", options=options(args, 1)))
            provider_loading = preload.get("load_duration")
            (args.output / f"loading-{args.runtime}.json").write_text(json.dumps(dict(
                loading_stage_wall_s=time.perf_counter() - start,
                provider_load_s=provider_loading / 1e9 if provider_loading is not None else None,
                response=preload), indent=2), encoding="utf-8")
            loaded = json_request(args.url.rstrip("/") + "/api/ps").get("models", [])
            current = next((model for model in loaded if model.get("name") == args.model or model.get("model") == args.model), None)
            metadata["effective_capacity"] = current.get("context_length") if current else None
            metadata["effective_capacity_source"] = "Ollama /api/ps context_length"
        else:
            metadata["effective_capacity"] = args.effective_capacity
            metadata["effective_capacity_source"] = "Owned native engine --ctx-size"
        if metadata["effective_capacity"] is not None and metadata["effective_capacity"] != args.capacity:
            raise ValueError("The loaded context capacity differs from the requested benchmark capacity.")
        telemetry.stage = "calibration"
        calibration = inference(args, prompt_for(128, 128, 20), 1)
        overhead = args.overhead if args.overhead is not None else calibration["input_tokens"] - 128
        metadata["calibration"] = {key: value for key, value in calibration.items() if key != "output"}
        metadata["prompt_overhead_tokens"] = overhead
        (args.output / f"metadata-{args.runtime}.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
        print(f"Calibrated raw prompt overhead: {overhead} tokens", flush=True)
        for target in args.depths:
            if target + args.output_tokens > args.capacity:
                raise ValueError("Context capacity must reserve space for the complete output.")
            for repetition in range(0 if args.skip_warmup else -1, args.repetitions):
                phase = "warmup" if repetition == -1 else "measured"
                telemetry.stage = f"{args.runtime}-{target}-{phase}-{repetition}"
                prompt = prompt_for(target - overhead, target, repetition + 1)
                stem = f"{target}-{repetition}"
                (args.output / "prompts" / f"{stem}.txt").write_text(prompt, encoding="utf-8")
                print(f"{telemetry.stage}: generating {args.output_tokens} tokens", flush=True)
                try:
                    row = inference(args, prompt, args.output_tokens)
                    output = row.pop("output")
                    (args.output / "outputs" / f"{args.runtime}-{stem}.txt").write_text(output, encoding="utf-8")
                    row.update(runtime=args.runtime, target_input_tokens=target, repetition=repetition,
                               phase=phase, prompt_sha256=hashlib.sha256(prompt.encode()).hexdigest())
                    row["valid"] = (row["output_tokens"] == args.output_tokens
                                    and abs(row["input_tokens"] - target) <= 8
                                    and row.get("tok_s") is not None
                                    and (row.get("load_s") is None or row["load_s"] < 0.25))
                    print(json.dumps({key: value for key, value in row.items()
                                      if key in ["input_tokens", "output_tokens", "tok_s", "ttft_excluding_load_s", "load_s", "valid"]}), flush=True)
                except (urllib.error.URLError, RuntimeError, OSError) as error:
                    row = dict(runtime=args.runtime, target_input_tokens=target, repetition=repetition,
                               phase=phase, valid=False, error=str(error))
                    print(json.dumps(row), flush=True)
                rows.append(row)
                with rows_path.open("a", encoding="utf-8") as handle:
                    handle.write(json.dumps(row) + "\n")
                summarize(args.output, rows)
                if not row["valid"]:
                    raise RuntimeError("Invalid benchmark sample; inspect evidence before continuing.")
    print(json.dumps(summarize(args.output, rows), indent=2), flush=True)


if __name__ == "__main__":
    main()
