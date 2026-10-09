"""Compare official builds before/at the reported regression and installed native engine.

Same existing weights, projector, GPU, 128K capacity and direct native requests.
No installed binary replacement. Downloaded archives must match GitHub asset SHA.
"""
import argparse
import hashlib
import json
import os
import re
import statistics
import subprocess
import time
import urllib.request
import zipfile
from datetime import datetime, timezone
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path
from types import SimpleNamespace


def import_script(name):
    spec = spec_from_file_location(name, Path(__file__).with_name(name+".py"))
    result = module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


matrix_module = import_script("run-comprehensive-runtime-matrix")
raw = import_script("run-runtime-context-benchmark")


def prepare(root, metadata_file):
    release = json.loads(metadata_file.read_text())
    asset, = release["assets"]
    directory = root/release["tag"]
    directory.mkdir(exist_ok=True)
    archive = directory/asset["name"]
    if not archive.exists():
        with urllib.request.urlopen(asset["browser_download_url"], timeout=120) as response, archive.open("wb") as destination:
            while chunk := response.read(1024*1024):
                destination.write(chunk)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if "sha256:"+digest != asset["digest"]:
        raise ValueError("Official archive hash does not match the GitHub asset digest")
    expanded = directory/"bin"
    expanded.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as zipped:
        for item in zipped.infolist():
            target = (expanded/item.filename).resolve()
            if not target.is_relative_to(expanded.resolve()) or (item.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError("Unsafe archive entry")
        zipped.extractall(expanded)
    executable, = expanded.rglob("llama-server.exe")
    (directory/"archive-verification.json").write_text(json.dumps(dict(asset=asset, sha256=digest, verified=True), indent=2))
    return executable


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    parser.add_argument("--wait-identity", type=Path)
    args = parser.parse_args()
    if args.wait_identity:
        expected = json.loads(args.wait_identity.read_text())
        current = matrix_module.identity(expected["ProcessId"])
        if current and current != expected:
            raise ValueError("Previous diagnostic ownership changed")
        while matrix_module.identity(expected["ProcessId"]):
            time.sleep(5)
    matrix = matrix_module.Matrix(SimpleNamespace(output=args.root, setup=args.setup))
    root = args.root/"vulkan-investigation"/"build-comparison"
    root.mkdir(exist_ok=True)
    if (root/"results.json").exists():
        raise ValueError("Don't overwrite an existing build comparison")
    matrix.quiet()
    bins = {tag: prepare(root, args.root/"vulkan-investigation"/metadata)
        for tag, metadata in [("b9459", "pre-regression-release.json"), ("b9460", "first-regression-release.json")]}
    installed = Path(os.environ["LOCALAPPDATA"])/"Programs/Ollama/lib/ollama/llama-server.exe"
    bins["installed-391fac164"] = installed
    protocol = dict(started_utc=datetime.now(timezone.utc).isoformat(), builds=list(bins), gpu="RX 7900 XTX",
        weights_sha256="f5f1dd8920d417aac2718b0bda3403da274301efdd6760b4f0f4b864ff2ad57d",
        capacity=131072, kv="q8_0", threads=8, batch=512, parallel=1, context_checkpoints=0,
        cache_prompt=False, cache_ram_mib=0, speculation="default none", suballocation="installed default",
        note="Isolated native diagnostic; uniform checkpoint/cache policy differs from the main Ollama run. 2K decode n=3; 32K fresh prefill n=1, both after warmup.")
    (root/"protocol.json").write_text(json.dumps(protocol, indent=2))
    rows = []
    try:
        for build, executable in bins.items():
            destination = root/build/"samples"
            destination.mkdir(parents=True)
            matrix.active_case = "amd-vulkan-build-"+build
            env = os.environ.copy()
            for key in list(env):
                if key.startswith(("GGML_VK_", "LLAMA_ARG_")):
                    env.pop(key)
            env.update(GGML_VK_VISIBLE_DEVICES="1", CUDA_VISIBLE_DEVICES="-1", HIP_VISIBLE_DEVICES="-1", ROCR_VISIBLE_DEVICES="-1")
            if build.startswith("installed"):
                env["PATH"] = str(executable.parent/"vulkan")+os.pathsep+str(executable.parent)+os.pathsep+env["PATH"]
                env["GGML_BACKEND_PATH"] = str(executable.parent/"vulkan")
            command = [str(executable), "--model", "E:/LLM/models/blobs/sha256-"+protocol["weights_sha256"],
                "--mmproj", "E:/LLM/models/blobs/sha256-ac3714bfdddeca31351f2752bf1a63f266f4df87c0b68c895e44945ca704448e",
                "--host", "127.0.0.1", "--port", "13501", "--ctx-size", "131072", "--parallel", "1",
                "--cache-type-k", "q8_0", "--cache-type-v", "q8_0", "--flash-attn", "on", "--batch-size", "512",
                "--ubatch-size", "512", "--gpu-layers", "999", "--threads", "8", "--split-mode", "none",
                "--main-gpu", "0", "--ctx-checkpoints", "0", "--cache-ram", "0"]
            matrix.start("probe", command, cwd=executable.parent, env=env)
            matrix.ready("http://127.0.0.1:13501/health")
            log = args.setup/(matrix.active_case+"-probe-error.log")
            text = log.read_text(errors="replace")
            if not re.search(r"using device Vulkan\d+.*RX 7900 XTX", text):
                raise ValueError("Native engine did not prove the requested physical GPU")
            (destination.parent/"native-startup.log").write_text(text)
            # The exact same native streaming collector is used for every build.
            config = SimpleNamespace(engine_url="http://127.0.0.1:13501", engine_key=None, cache_prompt=False)
            samples = []
            with raw.Telemetry(destination/"gpu.csv", "luid_0x00000000_0x00018E8D") as telemetry:
                for stage, target, tokens, repetitions in [("short-decode", 2048, 512, 3), ("prefill-32k", 32768, 64, 1)]:
                    for repetition in range(-1, repetitions):
                        prompt = raw.prompt_for(target-101, target, repetition+1)
                        telemetry.stage = stage+"-"+str(repetition)
                        result = raw.llama_server_inference(config, prompt, tokens)
                        result.pop("output")
                        result.update(build=build, stage=stage, phase="warmup" if repetition < 0 else "measured",
                            repetition=repetition, target_input_tokens=target, prompt_sha256=hashlib.sha256(prompt.encode()).hexdigest())
                        result["valid"] = result["input_tokens"] == target and result["output_tokens"] == tokens
                        samples.append(result)
                        with (destination/"samples.jsonl").open("a") as handle:
                            handle.write(json.dumps(result)+"\n")
                        print(json.dumps(result), flush=True)
                        if not result["valid"]:
                            raise ValueError("Native build token counts differ; don't rank unmatched prompts")
                    selected = [sample for sample in samples if sample["stage"] == stage and sample["phase"] == "measured"]
                    rows.append(dict(build=build, stage=stage, samples=len(selected),
                        tok_s=statistics.median(sample["tok_s"] for sample in selected),
                        prefill_s=statistics.median(sample["prefill_s"] for sample in selected),
                        ttft_s=statistics.median(sample["ttft_excluding_load_s"] for sample in selected)))
                    (root/"results.json").write_text(json.dumps(rows, indent=2))
            matrix.quiet()
    finally:
        matrix.quiet()


if __name__ == "__main__":
    main()
