"""Loaded-model latency: short chat, fresh 2K, and incremental 128K cache reuse.

Real inference only. Cache reuse is requested and provider evidence is recorded;
it is never assumed from a fast response. Model loading is a separate stage.
"""
import argparse
import hashlib
import json
import re
import statistics
import subprocess
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from importlib.util import module_from_spec, spec_from_file_location

spec = spec_from_file_location("raw_benchmark", Path(__file__).with_name("run-runtime-context-benchmark.py"))
raw = module_from_spec(spec)
spec.loader.exec_module(raw)


def public_chat(args, text):
    messages = [dict(role="user", content=text)]
    if args.runtime == "ollama":
        path = "/api/chat"
        body = dict(model=args.model, messages=messages, stream=True, keep_alive="30m",
                    options=raw.options(args, 128))
    else:
        path = "/v1/chat/completions"
        body = dict(model=args.model, messages=messages, stream=True, temperature=0,
                    seed=20260930, max_tokens=128, stream_options=dict(include_usage=True))
    req = urllib.request.Request(args.url.rstrip("/")+path, data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    first_any = first_visible = None
    final = {}
    with urllib.request.urlopen(req, timeout=1800) as response:
        for line in response:
            value = line.decode().strip()
            if args.runtime == "lmstudio":
                if not value.startswith("data:") or value[5:].strip() == "[DONE]":
                    continue
                value = value[5:]
            if not value:
                continue
            event = json.loads(value)
            if "error" in event:
                raise RuntimeError(str(event["error"]))
            if args.runtime == "ollama":
                delta = event.get("message", {})
                visible = delta.get("content", "")
                reasoning = delta.get("thinking", "")
                if event.get("done"):
                    final = event
            else:
                choices = event.get("choices", [])
                delta = choices[0].get("delta", {}) if choices else {}
                visible = delta.get("content", "")
                reasoning = delta.get("reasoning_content", "") or delta.get("reasoning", "")
                if event.get("usage"):
                    final = event["usage"]
            elapsed = time.perf_counter()-started
            if (visible or reasoning or delta.get("tool_calls")) and first_any is None:
                first_any = elapsed
            if visible and first_visible is None:
                first_visible = elapsed
    load = final.get("load_duration")
    load = load/1e9 if load is not None else None
    return dict(ttft_raw_s=first_any, ttft_excluding_load_s=first_any-load if first_any is not None and load is not None else first_any,
                first_visible_s=first_visible-load if first_visible is not None and load is not None else first_visible,
                load_s=load, loading_excluded_by_preload=True, wall_s=time.perf_counter()-started,
                input_tokens=final.get("prompt_eval_count", final.get("prompt_tokens")),
                output_tokens=final.get("eval_count", final.get("completion_tokens")),
                prefill_s=final.get("prompt_eval_duration", 0)/1e9 if "prompt_eval_duration" in final else None,
                timing_source="public-stream", thinking_policy="provider default; first generated and first visible measured separately")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=["ollama", "lmstudio"], required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--case", required=True)
    parser.add_argument("--capacity", type=int, default=132096)
    parser.add_argument("--prefix-tokens", type=int, default=30000)
    parser.add_argument("--lm-engine-pid", type=int)
    parser.add_argument("--gpu-evidence", type=Path, required=True)
    args = parser.parse_args()
    args.cache_prompt = True
    if args.runtime == "lmstudio":
        if not args.lm_engine_pid:
            raise ValueError("An explicitly owned engine PID is required")
        inspected = subprocess.run(["pwsh", "-NoProfile", "-Command",
            f"(Get-CimInstance Win32_Process -Filter 'ProcessId={args.lm_engine_pid}').CommandLine"],
            capture_output=True, text=True, check=True).stdout
        if "agentic-router-benchmark" not in inspected:
            raise ValueError("Engine namespace does not belong to this benchmark")
        args.engine_url = "http://127.0.0.1:"+re.search(r"--port\s+(\d+)", inspected)[1]
        args.engine_key = re.search(r"--api-key\s+(\S+)", inspected)[1]
        del inspected
        loaded = raw.json_request(args.url+"/api/v0/models")["data"]
    else:
        loaded = raw.json_request(args.url+"/api/ps")["models"]
    if not any((model.get("id") or model.get("name")) == args.model and model.get("state", "loaded") == "loaded" for model in loaded):
        raise ValueError("Explicit preload required; do not mix JIT loading with responsiveness")
    args.output.mkdir(parents=True, exist_ok=True)
    samples = args.output/"samples.jsonl"
    if samples.exists():
        raise ValueError("Choose a fresh responsiveness results directory")
    metadata = dict(case=args.case, runtime=args.runtime, model=args.model, capacity=args.capacity,
        started_utc=datetime.now(timezone.utc).isoformat(), gpu=json.loads(args.gpu_evidence.read_text()),
        scenarios=["short-public-chat", "fresh-2k-raw", f"incremental-{args.prefix_tokens}-raw"], repetitions=3,
        cache_policy="native cache_prompt=true; Ollama provider cache policy; provider evidence retained",
        raw_thinking_policy="identical raw ChatML with closed thinking prefill",
        prefix_tokens=args.prefix_tokens,
        note="Prefix prime is setup, not a cached sample; cached_input_tokens may be unavailable; prompt evaluation counts are not assumed to be total context")
    (args.output/"metadata.json").write_text(json.dumps(metadata, indent=2))
    inference = raw.ollama_inference if args.runtime == "ollama" else raw.lmstudio_inference
    rows = []
    def save(row, scenario, repetition, phase, prompt):
        output = row.pop("output", None)
        row.update(case=args.case, runtime=args.runtime, scenario=scenario, repetition=repetition, phase=phase,
                   prompt_sha256=hashlib.sha256(prompt.encode()).hexdigest(), utc=datetime.now(timezone.utc).isoformat())
        row["valid"] = row.get("ttft_excluding_load_s") is not None and (row.get("load_s") is None or row["load_s"] < .25)
        rows.append(row)
        with samples.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(row)+"\n")
        print(json.dumps(row), flush=True)
        if not row["valid"]:
            raise ValueError("Invalid latency sample; inspect the saved provider evidence")
        return output
    # Distinct first words prevent the short/fresh probes from sharing a prefix.
    for rep in range(-1, 3):
        text = f"{chr(65+rep+1)}. Reply with exactly READY."
        save(public_chat(args, text), "short-public-chat", rep, "warmup" if rep < 0 else "measured", text)
    for rep in range(-1, 3):
        prompt = raw.prompt_for(2048-101, 2048, rep+1)
        save(inference(args, prompt, 64), "fresh-2k-raw", rep, "warmup" if rep < 0 else "measured", prompt)
    prompt = raw.prompt_for(args.prefix_tokens-101, args.prefix_tokens, 5)
    output = save(inference(args, prompt, 64), f"incremental-{args.prefix_tokens}-raw", -1, "prime", prompt)
    for rep in range(3):
        prompt += output+"\n<|im_end|>\n<|im_start|>user\nContinue the numbered list.\n<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n"
        output = save(inference(args, prompt, 64), f"incremental-{args.prefix_tokens}-raw", rep, "measured", prompt)
    summary = []
    for scenario in metadata["scenarios"]:
        chosen = [row for row in rows if row["scenario"] == scenario and row["phase"] == "measured"]
        item = dict(case=args.case, scenario=scenario, samples=len(chosen))
        for key in ["ttft_excluding_load_s", "first_visible_s", "prefill_s", "cached_input_tokens", "input_tokens"]:
            values = [row[key] for row in chosen if row.get(key) is not None]
            item[key] = statistics.median(values) if values else None
        item["cache_evidence"] = "provider cache count" if all(row.get("cached_input_tokens") is not None for row in chosen) else "cache count unavailable; inspect prefill/counts"
        summary.append(item)
    (args.output/"summary.json").write_text(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
