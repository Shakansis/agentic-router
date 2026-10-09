"""Run the explicitly authorized sequential local GPU/runtime benchmark matrix.

No product changes. Uses owned loopback daemons, isolated AR data/workspaces,
existing weights and official installed engines. Records every stage and failure.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REPO = SCRIPTS.parent
SOURCE_MODEL = "qwen3.8:27b-gpu0"
MODEL = "ar-benchmark-qwen27b:controlled"
LM_MODEL = "ar-runtime-context-benchmark"
GPU_CASES = [
    ("amd-rocm", "rocm_v7_1", "amd-rocm", "ROCm0", "luid_0x00000000_0x00018E8D"),
    ("amd-vulkan", "vulkan", "vulkan", "Vulkan1", "luid_0x00000000_0x00018E8D"),
    ("nvidia-cuda", "cuda_v13", "nvidia-cuda12", "CUDA0", None),
    ("nvidia-vulkan", "vulkan", "vulkan", "Vulkan0", None),
]


def request(url, body=None, timeout=900):
    req = urllib.request.Request(url, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as response:
        return json.load(response)


def ps(script):
    result = subprocess.run(["pwsh", "-NoProfile", "-Command", script], capture_output=True,
                            text=True, encoding="utf-8", timeout=120, check=True)
    return result.stdout.strip()


def identity(pid):
    result = ps(f"Get-CimInstance Win32_Process -Filter 'ProcessId={int(pid)}' "
                "| Select-Object ProcessId,ExecutablePath,CreationDate | ConvertTo-Json -Compress")
    return json.loads(result) if result else None


class Matrix:
    def __init__(self, args):
        self.args = args
        self.root = args.output
        self.temp = args.setup
        previous = self.root / "matrix-progress.json"
        state = json.loads(previous.read_text()) if previous.exists() else {}
        self.owned = state.get("owned_processes", {})
        for key, expected in list(self.owned.items()):
            current = identity(expected["ProcessId"])
            if current and current != expected:
                raise ValueError(f"Inherited process ownership changed for {key}")
            if not current:
                self.owned.pop(key)
        self.records = state.get("stages", [])
        self.active_case = "setup"
        self.root.mkdir(parents=True, exist_ok=True)
        self.temp.mkdir(parents=True, exist_ok=True)
        self.save()

    def save(self):
        (self.root / "matrix-progress.json").write_text(json.dumps(dict(
            status="running", updated_utc=datetime.now(timezone.utc).isoformat(), stages=self.records,
            owned_processes=self.owned), indent=2), encoding="utf-8")

    def event(self, case, stage, state, **details):
        row = dict(case=case, stage=stage, state=state, utc=datetime.now(timezone.utc).isoformat(), **details)
        self.records.append(row)
        self.save()
        print(json.dumps(row), flush=True)

    def start(self, key, command, cwd=None, env=None):
        with (self.temp / f"{self.active_case}-{key}.log").open("a", encoding="utf-8") as stdout, \
             (self.temp / f"{self.active_case}-{key}-error.log").open("a", encoding="utf-8") as stderr:
            process = subprocess.Popen(command, cwd=cwd, env=env, stdout=stdout, stderr=stderr,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
        self.owned[key] = identity(process.pid)
        self.save()
        return process

    def stop(self, key):
        expected = self.owned.get(key)
        if not expected:
            return
        current = identity(expected["ProcessId"])
        if current and current != expected:
            raise ValueError(f"PID ownership changed for {key}")
        if current:
            if current["ProcessId"] in {14864, 15192, 46516, 17060}:
                raise ValueError("Refusing to stop a user process")
            subprocess.run(["taskkill", "/PID", str(current["ProcessId"]), "/T", "/F"],
                           capture_output=True, timeout=30, check=True)
        self.owned.pop(key, None)
        self.save()

    def command(self, case, stage, command, timeout=15000, cwd=None):
        self.event(case, stage, "running")
        with (self.root / f"{case}-{stage}.log").open("w", encoding="utf-8") as log:
            result = subprocess.run(command, cwd=cwd or REPO, stdout=log, stderr=subprocess.STDOUT,
                                    timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
        self.event(case, stage, "completed" if result.returncode == 0 else "failed", exit_code=result.returncode)
        if result.returncode:
            raise RuntimeError(f"{case}/{stage} exited {result.returncode}; inspect its log")

    def ready(self, url):
        deadline = time.monotonic()+180
        while time.monotonic() < deadline:
            try:
                request(url, timeout=5)
                return
            except (OSError, ValueError):
                time.sleep(1)
        raise RuntimeError(f"Own benchmark service did not become ready: {url}")

    def quiet(self):
        for key in ["api", "tap", "ollama", "probe"]:
            self.stop(key)
        loaded = request("http://127.0.0.1:12349/api/v0/models", timeout=10).get("data", [])
        if any(model.get("state") == "loaded" and model.get("id") != LM_MODEL for model in loaded):
            raise ValueError("External LM Studio model is loaded; refusing to interfere")
        if any(model.get("state") == "loaded" for model in loaded):
            subprocess.run(["lms", "unload", LM_MODEL], check=True, capture_output=True, timeout=120)

    def tap(self, runtime):
        self.start("tap", [sys.executable, str(SCRIPTS / "serve-runtime-benchmark-tap.py"),
            "--upstream", "http://127.0.0.1:" + ("13435" if runtime == "ollama" else "12349"),
            "--output", str(self.root / "workflow-inferences.jsonl"), "--runtime", runtime])
        self.ready("http://127.0.0.1:13450/__benchmark__/label")

    def api(self):
        env = os.environ.copy()
        env.update(ASPNETCORE_URLS="http://127.0.0.1:5398", AgenticRouter__DataDirectory=str(self.temp / "data"),
                   AgenticRouter__Benchmarking__RootDirectory=str(self.temp / "workspaces"))
        self.start("api", ["dotnet", str(self.temp / "api/AgenticRouter.Api.dll")], cwd=self.temp / "api", env=env)
        self.ready("http://127.0.0.1:5398/api/benchmarks/suite-runs/live")

    def ollama(self, case, library, native_device, kv, capacity):
        env = os.environ.copy()
        env.update(OLLAMA_HOST="127.0.0.1:13435", OLLAMA_MODELS="E:/LLM/models",
                   OLLAMA_LLM_LIBRARY=library, OLLAMA_NO_CLOUD="1", OLLAMA_NOPRUNE="1",
                   OLLAMA_NUM_PARALLEL="1", OLLAMA_MAX_LOADED_MODELS="1", OLLAMA_FLASH_ATTENTION="1",
                   OLLAMA_KV_CACHE_TYPE=kv, OLLAMA_VULKAN="1" if library == "vulkan" else "0")
        if library == "vulkan":
            env.update(CUDA_VISIBLE_DEVICES="-1", HIP_VISIBLE_DEVICES="-1", ROCR_VISIBLE_DEVICES="-1",
                       GGML_VK_VISIBLE_DEVICES=native_device.removeprefix("Vulkan"))
            env.pop("GPU_DEVICE_ORDINAL", None)
        elif library.startswith("rocm"):
            env.update(CUDA_VISIBLE_DEVICES="-1", HIP_VISIBLE_DEVICES="0", ROCR_VISIBLE_DEVICES="0", GPU_DEVICE_ORDINAL="0")
            env.pop("GGML_VK_VISIBLE_DEVICES", None)
        else:
            env.update(CUDA_VISIBLE_DEVICES="GPU-e32524e1-5a66-d5a8-08cf-a7b2e8b39098",
                       HIP_VISIBLE_DEVICES="-1", ROCR_VISIBLE_DEVICES="-1")
            env.pop("GPU_DEVICE_ORDINAL", None)
            env.pop("GGML_VK_VISIBLE_DEVICES", None)
        exe = Path(os.environ["LOCALAPPDATA"]) / "Programs/Ollama/ollama.exe"
        self.start("ollama", [str(exe), "serve"], env=env)
        self.ready("http://127.0.0.1:13435/api/version")
        (self.root / "engine-configurations" / f"{case}.json").write_text(json.dumps(
            {key: value for key, value in env.items() if key.startswith(("OLLAMA_", "CUDA_", "HIP_", "ROCR_", "GGML_VK", "GPU_DEVICE"))}, indent=2))
        aliases = request("http://127.0.0.1:13435/api/tags").get("models", [])
        alias_record = self.root / "controlled-ollama-alias.json"
        existing = next((model for model in aliases if model.get("name") == MODEL), None)
        if existing and not alias_record.is_file():
            raise ValueError("The benchmark alias already existed outside this task")
        parameters = dict(temperature=0, seed=20260930, top_k=0, top_p=1, min_p=0,
                          repeat_penalty=1, presence_penalty=0, num_ctx=131072,
                          num_thread=8, main_gpu=0, num_gpu=999, draft_num_predict=0)
        if not existing:
            request("http://127.0.0.1:13435/api/create", dict(model=MODEL, **{"from": SOURCE_MODEL},
                    parameters=parameters, stream=False))
            alias_record.write_text(json.dumps(dict(model=MODEL, source=SOURCE_MODEL,
                parameters=parameters, weights_sha256="f5f1dd8920d417aac2718b0bda3403da274301efdd6760b4f0f4b864ff2ad57d",
                created_utc=datetime.now(timezone.utc).isoformat(), temporary=True), indent=2))
        shown = request("http://127.0.0.1:13435/api/show", dict(model=MODEL))
        if "temperature                    0" not in shown.get("parameters", ""):
            raise ValueError("Controlled alias did not expose greedy temperature=0")
        (self.root / "engine-configurations" / f"{case}-model-defaults.json").write_text(json.dumps(dict(
            parameters=shown.get("parameters"), architecture=shown.get("model_info", {}).get("general.architecture")), indent=2))
        options = dict(num_ctx=capacity, num_predict=1, num_gpu=999, main_gpu=0, num_batch=512)
        if capacity == 132096:
            options["num_thread"] = 8
        started = time.perf_counter()
        loaded = request("http://127.0.0.1:13435/api/generate", dict(model=MODEL, stream=False,
            keep_alive="60m", options=options))
        (self.root / "engine-configurations" / f"{case}-loading-{capacity}.json").write_text(json.dumps(dict(
            loading_stage_wall_s=time.perf_counter()-started, response=loaded), indent=2))
        log = (self.temp / f"{case}-ollama-error.log").read_text(encoding="utf-8", errors="replace")
        lines = [line for line in log.splitlines() if any(marker in line for marker in
            ["inference compute", "using device", "offloaded", "model buffer size", "KV buffer size", "RS buffer size", "compute buffer size"])]
        (self.root / "engine-configurations" / f"{case}-engine-facts.txt").write_text("\n".join(lines))
        expected = "RX 7900 XTX" if case.startswith("amd") else "RTX 4090"
        if not any("using device" in line and expected in line for line in lines):
            raise ValueError("Loaded engine did not prove the requested physical GPU")

    def lmstudio(self, case, runtime, device, kv, capacity, workflow=False):
        full_runtime = f"llama.cpp-win-x86_64-{runtime}-avx2@2.47.0"
        self.command(case, f"select-{capacity}", ["lms", "runtime", "select", full_runtime], timeout=120)
        load_path = self.root / "engine-configurations" / f"{case}-loading-{capacity}.json"
        sdk = Path(os.environ["TEMP"]) / "ar-runtime-context-benchmark/node-sdk"
        load = ["node", str(SCRIPTS / "load-runtime-benchmark-lmstudio.mjs"),
            str(sdk), str(load_path), kv, device, str(capacity)]
        if workflow:
            load += [str(self.root / "engine-configurations/qwen-workflow-compatible.jinja")]
        self.command(case, f"load-{capacity}", load, timeout=900)
        # Inspect the owned model engine without printing or saving its API key.
        inspected = ps("Get-CimInstance Win32_Process -Filter \"Name='llama-server.exe'\" "
            "| Where-Object {$_.CommandLine -like '*agentic-router-benchmark*'} "
            "| Select-Object ProcessId,ParentProcessId,CreationDate,CommandLine | ConvertTo-Json -Compress")
        candidates = json.loads(inspected)
        candidates = candidates if isinstance(candidates, list) else [candidates]
        if len(candidates) != 1:
            raise ValueError("Expected exactly one owned LM benchmark engine")
        engine = candidates[0]
        command = engine.pop("CommandLine")
        engine["flags"] = {key: re.search(re.escape(key)+r"\s+(\S+)", command)[1]
            for key in ["--device", "--ctx-size", "--cache-type-k", "--cache-type-v", "--threads", "--split-mode", "--n-gpu-layers"]
            if re.search(re.escape(key)+r"\s+(\S+)", command)}
        del command
        (self.root / "engine-configurations" / f"{case}-flags-{capacity}.json").write_text(json.dumps(engine, indent=2))
        return engine["ProcessId"]

    def raw_reference(self, gpu, runtime, kv):
        parent = self.root.parent
        if gpu == "amd-rocm" and runtime == "ollama" and kv == "q8_0":
            return parent / "runtime-benchmark-amd-20260930"
        if gpu == "nvidia-cuda":
            result = parent / "runtime-benchmark-20260930"
            return result / "kv-q4-128k" if kv == "q4_0" else result
        return None

    def run(self):
        handoff = self.root / "active-child-handoff.json"
        if handoff.exists():
            expected = json.loads(handoff.read_text())["process"]
            current = identity(expected["ProcessId"])
            if current and current != expected:
                raise ValueError("Inherited benchmark PID identity changed")
            if current:
                self.event("matrix", "active-child-handoff", "waiting", process_id=current["ProcessId"])
            while identity(expected["ProcessId"]):
                time.sleep(5)
            self.event("matrix", "active-child-handoff", "completed")
            handoff.rename(self.root / ("completed-child-handoff-"+datetime.now().strftime("%H%M%S")+".json"))
        (self.root / "engine-configurations").mkdir(exist_ok=True)
        for phase in self.args.phases:
          for gpu, library, lm_runtime, device, luid in GPU_CASES:
            for kv in ["q8_0", "q4_0"]:
              for runtime in ["ollama", "lmstudio"]:
                    case = f"{gpu}-{runtime}-{kv[:2]}"
                    if self.args.cases and case not in self.args.cases:
                        continue
                    if phase == "responsiveness" and self.args.responsiveness_cases and case not in self.args.responsiveness_cases:
                        continue
                    self.active_case = case
                    reference = self.raw_reference(gpu, runtime, kv) if self.args.engine_depths == [131072] and self.args.engine_capacity == 132096 else None
                    if reference and phase == "engines":
                        self.event(case, "engine-128k", "reference", path=str(reference), target_input_tokens=131072)
                        continue
                    if phase == "engines":
                        overrides = self.root / "engine-result-paths.json"
                        paths = json.loads(overrides.read_text()) if overrides.exists() else {}
                        prior = Path(paths.get(case, str(self.root / "engines" / case))) / "samples.jsonl"
                        prior_rows = [json.loads(line) for line in prior.read_text().splitlines()] if prior.exists() else []
                        if all(len([row for row in prior_rows if row.get("valid") and row.get("phase") == "measured" and row.get("target_input_tokens") == depth]) == 3 for depth in self.args.engine_depths):
                            self.event(case, "engine-128k", "resumed-complete", path=str(prior.parent))
                            continue
                    elif phase == "responsiveness":
                        completed = self.root / "responsiveness" / case / "summary.json"
                        if completed.exists():
                            self.event(case, "responsiveness", "resumed-complete", path=str(completed))
                            continue
                    else:
                        harnesses = ["native", "codex", "claude-code", "opencode", "qwen-code"] if runtime == "ollama" else ["codex", "claude-code", "opencode", "qwen-code"]
                        harnesses = [h for h in harnesses if not (self.root / "workflows" / (case+"-controlled") / f"{h}.json").exists()]
                        if not harnesses:
                            self.event(case, "workflows", "resumed-complete")
                            continue
                    try:
                        self.quiet()
                        evidence = self.root / "engine-configurations" / f"{case}-gpu.json"
                        evidence.write_text(json.dumps(dict(gpu="RX 7900 XTX" if gpu.startswith("amd") else "RTX 4090",
                            backend=library, runtime=runtime, native_device=device, dxgi_luid=luid), indent=2))
                        if runtime == "ollama":
                            self.ollama(case, library, device, kv, 131072 if phase == "workflows" else self.args.engine_capacity)
                            engine_pid = None
                        else:
                            engine_pid = self.lmstudio(case, lm_runtime, device, kv, 131072 if phase == "workflows" else self.args.engine_capacity,
                                                     workflow=phase == "workflows")
                        if phase == "responsiveness":
                            destination = self.root / "responsiveness" / case
                            if (destination / "samples.jsonl").exists():
                                destination = destination.with_name(case+"-retry-"+datetime.now().strftime("%H%M%S"))
                            probe = [sys.executable, str(SCRIPTS / "run-runtime-responsiveness-benchmark.py"),
                                "--runtime", runtime, "--url", "http://127.0.0.1:"+("13435" if runtime == "ollama" else "12349"),
                                "--model", MODEL if runtime == "ollama" else LM_MODEL, "--case", case,
                                "--output", str(destination), "--gpu-evidence", str(evidence), "--capacity", str(self.args.engine_capacity),
                                "--prefix-tokens", str(self.args.latency_prefix_tokens)]
                            if engine_pid:
                                probe += ["--lm-engine-pid", str(engine_pid)]
                            self.command(case, "responsiveness", probe, timeout=5000)
                            continue
                        if phase == "engines":
                            destination = self.root / "engines" / case
                            if (destination / "samples.jsonl").exists():
                                destination = destination.with_name(case+"-retry-"+datetime.now().strftime("%H%M%S"))
                                overrides = self.root / "engine-result-paths.json"
                                paths = json.loads(overrides.read_text()) if overrides.exists() else {}
                                paths[case] = str(destination)
                                overrides.write_text(json.dumps(paths, indent=2))
                            raw = [sys.executable, str(SCRIPTS / "run-runtime-context-benchmark.py"),
                                "--runtime", runtime, "--url", "http://127.0.0.1:" + ("13435" if runtime == "ollama" else "12349"),
                                "--model", MODEL if runtime == "ollama" else LM_MODEL,
                                "--output", str(destination), "--capacity", str(self.args.engine_capacity),
                                "--depths", *map(str, self.args.engine_depths), "--overhead", "101",
                                "--gpu-evidence", str(evidence)]
                            if luid:
                                raw += ["--windows-adapter-luid", luid]
                            if engine_pid:
                                raw += ["--lm-engine-pid", str(engine_pid)]
                            self.command(case, "engine-"+"-".join(map(str, self.args.engine_depths)), raw, timeout=5000)
                            continue
                        if runtime == "ollama":
                            start = time.perf_counter()
                            result = request("http://127.0.0.1:13435/api/generate", dict(model=MODEL, stream=False,
                                keep_alive="60m", options=dict(num_ctx=131072)))
                            (self.root / "engine-configurations" / f"{case}-workflow-preload.json").write_text(json.dumps(dict(
                                loading_stage_wall_s=time.perf_counter()-start, response=result), indent=2))
                        self.tap(runtime)
                        workflow = [sys.executable, str(SCRIPTS / ("run-agentic-runtime-benchmark.py" if runtime == "ollama"
                                                                  else "run-lmstudio-harness-workflows.py")),
                            "--model", MODEL if runtime == "ollama" else LM_MODEL, "--case", case+"-controlled",
                            "--observations", str(self.root / "workflow-inferences.jsonl"),
                            "--output", str(self.root / "workflows" / (case+"-controlled")), "--padding-tokens", str(self.args.workflow_padding_tokens),
                            "--harnesses", *harnesses]
                        if runtime == "ollama":
                            self.api()
                            workflow += ["--api", "http://127.0.0.1:5398"]
                        else:
                            workflow += ["--workspaces", str(self.temp / "standalone-workspaces")]
                        self.command(case, "workflows", workflow, timeout=15000)
                    except Exception as error:
                        self.event(case, "case", "failed", error=str(error))
                    finally:
                        self.quiet()
        self.event("matrix", "all-cases", "completed-attempts")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    parser.add_argument("--phases", nargs="+", choices=["responsiveness", "engines", "workflows"], default=["engines", "workflows"])
    parser.add_argument("--cases", nargs="+")
    parser.add_argument("--responsiveness-cases", nargs="+")
    parser.add_argument("--engine-depths", nargs="+", type=int, default=[131072])
    parser.add_argument("--engine-capacity", type=int, default=132096)
    parser.add_argument("--latency-prefix-tokens", type=int, default=30000)
    parser.add_argument("--workflow-padding-tokens", type=int, default=0)
    args = parser.parse_args()
    Matrix(args).run()


if __name__ == "__main__":
    main()
