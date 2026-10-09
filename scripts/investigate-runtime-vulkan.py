"""Sequential owned Ollama/Vulkan A-B-A test of default versus 4 GiB blocks.

Start with short decode and fresh 32K prefill at 128K capacity. Allocation
logging runs separately; it is disabled for every performance sample.
"""
import argparse
import json
import os
import shutil
import sys
import time
from datetime import datetime, timezone
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
from types import SimpleNamespace

spec = spec_from_file_location("matrix", Path(__file__).with_name("run-comprehensive-runtime-matrix.py"))
module = module_from_spec(spec)
spec.loader.exec_module(module)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    args = parser.parse_args()
    handoff = args.root/"active-child-handoff.json"
    if handoff.exists():
        expected = json.loads(handoff.read_text())["process"]
        current = module.identity(expected["ProcessId"])
        if current and current != expected:
            raise ValueError("Active benchmark child ownership changed")
        while module.identity(expected["ProcessId"]):
            time.sleep(5)
        handoff.rename(args.root/("completed-child-handoff-"+datetime.now().strftime("%H%M%S")+".json"))
    matrix = module.Matrix(SimpleNamespace(output=args.root, setup=args.setup))
    output = args.root/"vulkan-investigation"
    output.mkdir(exist_ok=True)
    original = {key: os.environ.get(key) for key in ["GGML_VK_SUBALLOCATION_BLOCK_SIZE", "GGML_VK_MEMORY_LOGGER"]}
    protocol = dict(started_utc=datetime.now(timezone.utc).isoformat(), order=["default-a", "4gib", "default-b"],
        gpu="RX 7900 XTX", backend="official installed Ollama 0.34.2 Vulkan / llama.cpp 391fac164",
        model=module.MODEL, kv="q8_0", capacity=131072, num_parallel=1, batch=512, threads=8,
        stages=["allocation-only logging, isolated model reload", "2K prompt + 512 decode, warmup + n=3", "fresh 32K prompt + 64 decode, warmup + n=1"],
        deadline_s_per_stage=600, note="No uncached 128K repetitions before inspecting this diagnostic; no system/user environment changes")
    (output/"protocol.json").write_text(json.dumps(protocol, indent=2))
    results = []
    try:
        matrix.quiet()
        for variant in protocol["order"]:
            os.environ.pop("GGML_VK_SUBALLOCATION_BLOCK_SIZE", None)
            if variant == "4gib":
                os.environ["GGML_VK_SUBALLOCATION_BLOCK_SIZE"] = "4294967296"
            case = "amd-vulkan-diagnostic-"+variant
            item = dict(variant=variant, requested_block_bytes=4294967296 if variant == "4gib" else None,
                        suballocation_policy="explicit 4 GiB" if variant == "4gib" else "installed default")
            results.append(item)
            matrix.active_case = case+"-allocation"
            os.environ["GGML_VK_MEMORY_LOGGER"] = "1"
            matrix.ollama(matrix.active_case, "vulkan", "Vulkan1", "q8_0", 131072)
            log = args.setup/(matrix.active_case+"-ollama-error.log")
            shutil.copy2(log, output/(variant+"-allocation.log"))
            memory = [line for line in log.read_text(errors="replace").splitlines() if "ggml_vulkan memory:" in line]
            (output/(variant+"-memory.json")).write_text(json.dumps(memory, indent=2))
            item["memory_logger_lines"] = len(memory)
            matrix.quiet()
            os.environ.pop("GGML_VK_MEMORY_LOGGER", None)
            matrix.active_case = case
            matrix.ollama(case, "vulkan", "Vulkan1", "q8_0", 131072)
            evidence = args.root/"engine-configurations"/(case+"-gpu.json")
            evidence.write_text(json.dumps(dict(gpu="RX 7900 XTX", backend="vulkan", runtime="ollama",
                native_device="Vulkan1", dxgi_luid="luid_0x00000000_0x00018E8D", suballocation=item["suballocation_policy"]), indent=2))
            for stage, depth, repetitions, tokens in [("short-decode", 2048, 3, 512), ("prefill-32k", 32768, 1, 64)]:
                destination = output/variant/stage
                command = [sys.executable, str(Path(__file__).with_name("run-runtime-context-benchmark.py")),
                    "--runtime", "ollama", "--url", "http://127.0.0.1:13435", "--model", module.MODEL,
                    "--output", str(destination), "--capacity", "131072", "--depths", str(depth),
                    "--output-tokens", str(tokens), "--repetitions", str(repetitions), "--overhead", "101",
                    "--windows-adapter-luid", "luid_0x00000000_0x00018E8D", "--gpu-evidence", str(evidence)]
                matrix.command(case, stage, command, timeout=600)
                item[stage] = json.loads((destination/"summary.json").read_text())
                (output/"results.json").write_text(json.dumps(results, indent=2))
            matrix.quiet()
        (output/"completion.json").write_text(json.dumps(dict(completed_utc=datetime.now(timezone.utc).isoformat(),
            status="short-decode-and-32k-prefill-diagnostic-complete", full_128k_followup="Decide from measured A/B/A results"), indent=2))
    finally:
        matrix.quiet()
        for key, value in original.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


if __name__ == "__main__":
    main()
