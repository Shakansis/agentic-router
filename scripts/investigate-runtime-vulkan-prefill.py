"""Fresh 128K A/B with a 15-minute wall deadline and native progress/WDDM evidence."""
import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
from types import SimpleNamespace

spec = spec_from_file_location("matrix", Path(__file__).with_name("run-comprehensive-runtime-matrix.py"))
module = module_from_spec(spec)
spec.loader.exec_module(module)


class Monitor:
    def __init__(self, log, directory, pid):
        self.log, self.directory, self.pid = log, directory, pid
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.sample, daemon=True)

    def sample(self):
        start = time.perf_counter()
        offset = self.log.stat().st_size
        target_task = None
        last_memory = -10
        with (self.directory/"native-progress.jsonl").open("w") as progress, (self.directory/"process-memory.jsonl").open("w") as memory:
            while not self.stop.is_set():
                with self.log.open(errors="replace") as source:
                    source.seek(offset)
                    lines = source.readlines()
                    offset = source.tell()
                for line in lines:
                    begun = re.search(r"task (\d+).*new prompt.*task.n_tokens = 131072", line)
                    if begun:
                        target_task = begun[1]
                    if target_task is None or not re.search(r"task "+target_task+r"\b", line):
                        continue
                    position = re.search(r"cached n_tokens = (\d+)", line)
                    native = re.search(r"prompt processing, n_tokens = (\d+).*t = ([\d.]+) s / ([\d.]+) tokens per second", line)
                    if position or native:
                        row = dict(utc=datetime.now(timezone.utc).isoformat(), stage_wall_s=time.perf_counter()-start,
                            task=target_task, processed_tokens=int((native or position)[1]),
                            native_prompt_s=float(native[2]) if native else None,
                            native_prompt_tok_s=float(native[3]) if native else None,
                            observation="native timing" if native else "batch position observed by log poll, not a TTFT measurement")
                        progress.write(json.dumps(row)+"\n")
                        progress.flush()
                if time.perf_counter()-start-last_memory >= 10:
                    last_memory = time.perf_counter()-start
                    command = (f"$p=Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory "
                        f"| Where-Object {{$_.Name -like 'pid_{self.pid}_*luid_0x00000000_0x00018E8D*'}} "
                        "| Select-Object Name,DedicatedUsage,SharedUsage,LocalUsage,NonLocalUsage,TotalCommitted; "
                        f"$c=Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter 'IDProcess={self.pid}' "
                        "| Select-Object IDProcess,WorkingSetPrivate,PrivateBytes,PageFaultsPersec; "
                        "@{gpu=@($p);process=$c}|ConvertTo-Json -Depth 4 -Compress")
                    try:
                        result = module.ps(command)
                        memory.write(json.dumps(dict(utc=datetime.now(timezone.utc).isoformat(), native_pid=self.pid,
                            counters=json.loads(result)))+"\n")
                        memory.flush()
                    except Exception as error:
                        memory.write(json.dumps(dict(error=type(error).__name__))+"\n")
                self.stop.wait(1)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *args):
        self.stop.set()
        self.thread.join(timeout=12)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    args = parser.parse_args()
    matrix = module.Matrix(SimpleNamespace(output=args.root, setup=args.setup))
    root = args.root/"vulkan-investigation"/"full-prefill"
    root.mkdir(parents=True, exist_ok=True)
    if (root/"results.json").exists():
        raise ValueError("Choose a fresh diagnostic; don't overwrite results")
    original = {key: os.environ.get(key) for key in ["GGML_VK_SUBALLOCATION_BLOCK_SIZE", "GGML_VK_MEMORY_LOGGER"]}
    rows = []
    (root/"protocol.json").write_text(json.dumps(dict(capacity=132096, actual_input=131072, output_tokens=64,
        samples=1, warmup="short calibration only; first large prompt after fresh load", deadline_wall_s=900,
        order=["default", "4gib"], note="Single diagnostic sample, not a replacement for the n=3 benchmark. Allocation logger disabled. Driver limits effective blocks to 2 GiB."), indent=2))
    try:
        matrix.quiet()
        for variant in ["default", "4gib"]:
            os.environ.pop("GGML_VK_MEMORY_LOGGER", None)
            os.environ.pop("GGML_VK_SUBALLOCATION_BLOCK_SIZE", None)
            if variant == "4gib":
                os.environ["GGML_VK_SUBALLOCATION_BLOCK_SIZE"] = "4294967296"
            case = "amd-vulkan-full-prefill-"+variant
            matrix.active_case = case
            destination = root/variant
            destination.mkdir()
            matrix.ollama(case, "vulkan", "Vulkan1", "q8_0", 132096)
            pid = matrix.owned["ollama"]["ProcessId"]
            children = json.loads(module.ps(f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={pid}' "
                "| Where-Object {$_.Name -eq 'llama-server.exe'} | Select-Object ProcessId,ExecutablePath,CreationDate | ConvertTo-Json -Compress"))
            children = children if isinstance(children, list) else [children]
            if len(children) != 1:
                raise ValueError("Expected the single native engine belonging to this daemon")
            native = children[0]
            (destination/"native-identity.json").write_text(json.dumps(native, indent=2))
            evidence = args.root/"engine-configurations"/(case+"-gpu.json")
            evidence.write_text(json.dumps(dict(gpu="RX 7900 XTX", backend="vulkan", runtime="ollama", device="Vulkan1")))
            command = [sys.executable, str(Path(__file__).with_name("run-runtime-context-benchmark.py")),
                "--runtime", "ollama", "--url", "http://127.0.0.1:13435", "--model", module.MODEL,
                "--output", str(destination), "--depths", "131072", "--output-tokens", "64", "--repetitions", "1",
                "--skip-warmup", "--overhead", "101", "--windows-adapter-luid", "luid_0x00000000_0x00018E8D",
                "--gpu-evidence", str(evidence)]
            row = dict(variant=variant, requested_block_bytes=4294967296 if variant == "4gib" else None,
                native_pid=native["ProcessId"], status="running")
            rows.append(row)
            try:
                with Monitor(args.setup/(case+"-ollama-error.log"), destination, native["ProcessId"]):
                    matrix.command(case, "targeted-prefill-128k", command, timeout=900)
                row.update(status="measured", summary=json.loads((destination/"summary.json").read_text()))
            except subprocess.TimeoutExpired:
                row.update(status="wall-deadline", error="900-second diagnostic wall deadline; no throughput/TTFT result invented")
            finally:
                matrix.quiet()
                (root/"results.json").write_text(json.dumps(rows, indent=2))
    finally:
        matrix.quiet()
        for key, value in original.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


if __name__ == "__main__":
    main()
