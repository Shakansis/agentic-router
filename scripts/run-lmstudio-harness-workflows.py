"""Opt-in standalone harness workflows against an explicitly preloaded LM Studio.

Native/AR is not implemented here. Uses vendor CLIs, isolated configuration,
workspace-local file permissions and the transparent benchmark transport tap.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import time
import uuid
from pathlib import Path

spec = importlib.util.spec_from_file_location("workflow", Path(__file__).with_name("run-agentic-runtime-benchmark.py"))
workflow = importlib.util.module_from_spec(spec)
spec.loader.exec_module(workflow)


def base_env(config_dir):
    env = os.environ.copy()
    for key in ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "GOOGLE_API_KEY", "GEMINI_API_KEY",
                "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"]:
        env.pop(key, None)
    env.update(NO_COLOR="1", DISABLE_TELEMETRY="1", DISABLE_ERROR_REPORTING="1", DISABLE_AUTOUPDATER="1")
    return env


def cli_configuration(harness, directory, endpoint, model):
    directory.mkdir(parents=True)
    env = base_env(directory)
    env.update(OPENAI_BASE_URL=endpoint+"/v1", OPENAI_API_BASE=endpoint+"/v1", OPENAI_API_KEY="lmstudio")
    if harness == "codex":
        env.update(CODEX_HOME=str(directory), CODEX_OSS_BASE_URL=endpoint + "/v1")
        (directory / "config.toml").write_text('approval_policy = "never"\nsandbox_mode = "workspace-write"\nmodel_context_window = 131072\nweb_search = "disabled"\n[windows]\nsandbox = "unelevated"\n')
        return [shutil.which("codex"), "exec", "--oss", "--local-provider", "lmstudio", "--model", model,
                "--sandbox", "workspace-write", "--ephemeral", "--json", "--skip-git-repo-check", "--disable", "plugins",
                "--disable", "remote_plugin", "--disable", "remote_control", "-"], env
    if harness == "claude-code":
        env.update(CLAUDE_CONFIG_DIR=str(directory), ANTHROPIC_BASE_URL=endpoint,
                   ANTHROPIC_AUTH_TOKEN="lmstudio", ANTHROPIC_MODEL=model,
                   ANTHROPIC_DEFAULT_OPUS_MODEL=model, ANTHROPIC_DEFAULT_SONNET_MODEL=model,
                   ANTHROPIC_DEFAULT_HAIKU_MODEL=model, CLAUDE_CODE_SUBAGENT_MODEL=model,
                   CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC="1", API_TIMEOUT_MS="900000",
                   CLAUDE_STREAM_IDLE_TIMEOUT_MS="900000")
        return [shutil.which("claude"), "--print", "--input-format", "stream-json", "--output-format", "stream-json",
                "--verbose", "--include-partial-messages", "--model", model, "--bare", "--no-chrome",
                "--disable-slash-commands", "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}',
                "--tools", "Read,Write,Edit,Glob,Grep,Bash", "--permission-mode", "acceptEdits",
                "--allowedTools", "Read(./**)", "Write(./**)", "Edit(./**)", "Glob(./**)", "Grep(./**)",
                "Bash(node summarize.mjs *)", "Bash(node --version)", "--max-turns", "20"], env
    if harness == "opencode":
        config = dict(model=f"benchmark/{model}", provider={"benchmark": dict(npm="@ai-sdk/openai-compatible",
            name="LM Studio local benchmark", options=dict(baseURL=endpoint+"/v1", apiKey="lmstudio", timeout=900000),
            models={model: dict(name=model, limit=dict(context=131072, output=32000))})},
            permission=dict(external_directory="deny", edit="allow", webfetch="deny", websearch="deny", task="deny",
                            bash={"node summarize.mjs *": "allow", "node --version": "allow", "pwd": "allow", "ls": "allow", "*": "deny"}))
        config_path = directory / "opencode.json"
        config_path.write_text(json.dumps(config))
        env.update(OPENCODE_CONFIG=str(config_path), XDG_CONFIG_HOME=str(directory / "config"),
                   XDG_DATA_HOME=str(directory / "data"), XDG_CACHE_HOME=str(directory / "cache"))
        executable = Path(os.environ["APPDATA"]) / "npm/node_modules/opencode-ai/bin/opencode.exe"
        return [str(executable), "run", "--pure", "--format", "json", "--model", f"benchmark/{model}"], env
    if harness == "qwen-code":
        config = {"$version": 4, "model": {"name": model}, "modelProviders": {"openai": [dict(
            id=model, name=model, envKey="OPENAI_API_KEY", baseUrl=endpoint+"/v1",
            generationConfig=dict(contextWindowSize=131072, maxRetries=0, temperature=0))]},
            "security": {"auth": {"selectedType": "openai"}},
            "tools": {"approvalMode": "auto-edit", "computerUse": {"enabled": False}},
            "permissions": {"allow": ["run_shell_command(node summarize.mjs *)", "run_shell_command(node --version)"],
                            "deny": ["agent", "skill", "save_memory", "web_search", "web_fetch", "ask_user_question"]},
            "privacy": {"usageStatisticsEnabled": False}, "general": {"enableAutoUpdate": False}, "disableAllHooks": True,
            "memory": {"enableManagedAutoMemory": False, "enableManagedAutoDream": False, "enableAutoSkill": False,
                       "enableTeamMemory": False, "enableTeamMemorySync": False}}
        config_path = directory / "settings.json"
        config_path.write_text(json.dumps(config))
        env.update(QWEN_HOME=str(directory), QWEN_CODE_SYSTEM_SETTINGS_PATH=str(config_path), OPENAI_API_KEY="lmstudio",
                   QWEN_CODE_NO_UPDATE_NOTIFIER="1")
        cli = Path(os.environ["APPDATA"]) / "npm/node_modules/@qwen-code/qwen-code/cli.js"
        return [shutil.which("node"), str(cli), "--model", model, "--prompt", "", "--output-format", "stream-json"], env
    raise ValueError(harness)


def summarize_cli(stdout):
    events = []
    for line in stdout.splitlines():
        try:
            item = json.loads(line)
        except ValueError:
            continue
        if not isinstance(item, dict):
            continue
        # Store event types/status and final intended answer; no reasoning/prompt payloads.
        record = {key: item[key] for key in ["type", "subtype", "is_error", "duration_ms", "num_turns"] if key in item}
        if item.get("type") == "result":
            record["result"] = str(item.get("result", ""))[-4000:]
        if item.get("type") in {"error", "turn.failed"}:
            record["error"] = str(item.get("message") or item.get("error") or "")[-2000:]
        nested = item.get("item") or item.get("part") or {}
        if isinstance(nested, dict):
            record["item_type"] = nested.get("type")
            state = nested.get("state")
            record["status"] = nested.get("status") or (state.get("status") if isinstance(state, dict) else None)
            record["item_id"] = nested.get("id") or nested.get("callID")
            if nested.get("type") == "error":
                record["error"] = str(nested.get("message") or nested.get("text") or "")[-2000:]
            if nested.get("type") == "command_execution":
                record["command"] = nested.get("command")
                record["exit_code"] = nested.get("exit_code")
        message = item.get("message")
        blocks = message.get("content", []) if isinstance(message, dict) else []
        if isinstance(blocks, list):
            record["tools"] = [{key: block[key] for key in ["type", "id", "tool_use_id", "name", "is_error"] if key in block}
                               for block in blocks if isinstance(block, dict) and block.get("type") in {"tool_use", "tool_result"}]
        # Qwen CLI reports tools as independent stream events.
        for key in ["tool_id", "tool_name", "status"]:
            if key in item:
                record[key] = item[key]
        events.append(record)
    return events


def tool_facts(events, stderr=""):
    calls, failures = set(), set()
    retries = 0
    for index, event in enumerate(events):
        identity = event.get("item_id") or event.get("tool_id") or str(index)
        if event.get("item_type") in {"command_execution", "file_change", "mcp_tool_call", "tool"}:
            calls.add(identity)
            if event.get("status") in {"error", "failed"} or event.get("exit_code", 0):
                failures.add(identity)
        if event.get("type") == "tool_use":
            calls.add(identity)
        if event.get("type") == "tool_result" and event.get("status") in {"error", "failed"}:
            failures.add(identity)
        for block in event.get("tools", []):
            if block["type"] == "tool_use":
                calls.add(block.get("id") or str(index))
            elif block.get("is_error"):
                failures.add(block.get("tool_use_id") or str(index))
        retries += event.get("subtype") == "api_retry"
    router_errors = sum("codex_core::tools::router: error=" in line for line in stderr.splitlines())
    return dict(observed_tool_calls=len(calls) if calls or not router_errors else None,
                observed_tool_failures=len(failures) if calls or not router_errors else None,
                native_router_errors=router_errors,
                observed_provider_retries=retries, recovery_attempts=None,
                provenance="vendor CLI events; recovery attempts unavailable without Host observer")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default="ar-runtime-context-benchmark")
    parser.add_argument("--case", required=True)
    parser.add_argument("--tap", default="http://127.0.0.1:13450")
    parser.add_argument("--observations", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--workspaces", type=Path, required=True)
    parser.add_argument("--harnesses", nargs="+", default=["codex", "claude-code", "opencode", "qwen-code"])
    parser.add_argument("--padding-tokens", type=int, default=100000)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    prompt = workflow.workflow_prompt(args.padding_tokens)
    (args.output / "task.txt").write_text(prompt, encoding="utf-8")
    for harness in args.harnesses:
        label = f"{args.case}-{harness}"
        result_path = args.output / f"{harness}.json"
        if result_path.exists():
            raise ValueError(f"Already recorded {label}")
        workspace = args.workspaces / label
        directory = args.output / "cli-configurations" / harness
        if workspace.exists() or directory.exists():
            attempt = uuid.uuid4().hex[:12]
            label += f"-attempt-{attempt}"
            workspace = args.workspaces / label
            directory = args.output / "cli-configurations" / f"{harness}-attempt-{attempt}"
        workspace.mkdir(parents=True)
        # Codex refuses helper binaries below TEMP; keep isolated CLI homes with
        # durable results while disposable workspaces remain below TEMP.
        command, env = cli_configuration(harness, directory, args.tap, args.model)
        workflow.request(args.tap + "/__benchmark__/label", dict(label=label))
        payload = (json.dumps(dict(type="user", message=dict(role="user", content=prompt)))+"\n") if harness == "claude-code" else prompt
        print(f"START standalone {label}", flush=True)
        start = time.perf_counter()
        process = subprocess.Popen(command, cwd=workspace, env=env, stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        try:
            stdout, stderr = process.communicate(payload, timeout=1600)
            status = dict(exit_code=process.returncode, events=summarize_cli(stdout),
                          stderr="\n".join(line for line in stderr.splitlines() if not line.lstrip().startswith("at "))[-2000:])
        except subprocess.TimeoutExpired:
            if process.poll() is None:
                if os.name == "nt":
                    subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=20)
                else:
                    process.kill()
            stdout, stderr = process.communicate(timeout=30)
            status = dict(exit_code=None, timeout=True, events=summarize_cli(stdout),
                          stderr="\n".join(line for line in stderr.splitlines() if not line.lstrip().startswith("at "))[-2000:])
        execution_wall_s = time.perf_counter()-start
        workflow.wait_for_observations(args.tap)
        verification_start = time.perf_counter()
        acceptance = workflow.validate_artifacts(workspace)
        files = workflow.snapshot_artifacts(workspace, args.output / "artifacts" / harness)
        record = dict(case=args.case, harness=harness, execution_path="standalone vendor CLI",
                      observation_label=label,
                      context_capacity=131072, padding_tokens=args.padding_tokens,
                      prompt_sha256=hashlib.sha256(prompt.encode()).hexdigest(),
                      wall_s=execution_wall_s, wall_source="Vendor CLI process start to exit",
                      independent_verification_s=time.perf_counter()-verification_start, validation=acceptance, artifacts=files,
                      inference=workflow.aggregate_observations(args.observations, label), cli=status,
                      tools=tool_facts(status.get("events", []), stderr))
        result_path.write_text(json.dumps(record, indent=2), encoding="utf-8")
        print(json.dumps(dict(label=label, wall_s=record["wall_s"], validation=acceptance["passed"],
                              inference=record["inference"], exit_code=status["exit_code"])), flush=True)


if __name__ == "__main__":
    main()
