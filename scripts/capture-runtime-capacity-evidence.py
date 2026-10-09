"""Copy bounded effective-context evidence from owned benchmark engines."""
import argparse
import json
import re
import shutil
from datetime import datetime, timezone
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    args = parser.parse_args()
    plan = json.loads((args.root / "matrix-plan.json").read_text(encoding="utf-8-sig"))
    directory = args.root / "engine-configurations"
    records = []
    for sample in (args.root / "engines").glob("*/samples.jsonl"):
        case = sample.parent.name.split("-retry-")[0]
        if "-ollama-" in case:
            source = args.setup / f"{case}-ollama-error.log"
            lines = [line for line in source.read_text(encoding="utf-8", errors="replace").splitlines()
                     if "n_ctx_slot =" in line]
            if not lines:
                raise ValueError(f"No native context proof for {case}")
            evidence = lines[-1]
            capacity = int(re.search(r"n_ctx_slot = (\d+)", evidence)[1])
        else:
            source = directory / f"{case}-flags-{plan['capacity']}.json"
            flags = json.loads(source.read_text(encoding="utf-8-sig"))["flags"]
            capacity = int(flags["--ctx-size"])
            evidence = {key: flags[key] for key in ["--device", "--ctx-size", "--cache-type-k", "--cache-type-v"]}
        if capacity != plan["capacity"]:
            raise ValueError(f"Effective context differs for {case}: {capacity}")
        record = dict(case=case, effective_capacity=capacity, source=str(source), evidence=evidence,
                      captured_utc=datetime.now(timezone.utc).isoformat())
        (directory / f"{case}-capacity-evidence.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
        loading_source = directory / f"{case}-loading-{plan['capacity']}.json"
        loading_copy = sample.parent / "engine-loading.json"
        if loading_source.exists() and not loading_copy.exists():
            shutil.copyfile(loading_source, loading_copy)
        records.append(dict(case=case, effective_capacity=capacity))
    print(json.dumps(records))


if __name__ == "__main__":
    main()
