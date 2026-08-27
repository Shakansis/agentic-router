#!/usr/bin/env bash
set -Eeuo pipefail

publish_dir="${1:?Usage: validate-linux-smoke.sh PUBLISH_DIRECTORY [PORT]}"
port="${2:-58741}"
app="$publish_dir/AgenticRouter.Api"

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  printf 'This smoke test requires Linux x86_64.\n' >&2
  exit 2
fi
if [[ ! -f "$app" ]]; then
  printf 'Published AgenticRouter.Api executable was not found: %s\n' "$app" >&2
  exit 2
fi
for command_name in curl grep seq; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required smoke-test command is missing: %s\n' "$command_name" >&2
    exit 3
  fi
done

smoke_dir="$(mktemp -d /tmp/agentic-router-linux-smoke.XXXXXX)"
app_pid=""
cleanup() {
  local result=$?
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill -TERM "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  if [[ $result -ne 0 && -f "$smoke_dir/app.log" ]]; then
    tail -n 80 "$smoke_dir/app.log" >&2 || true
  fi
  case "$smoke_dir" in
    /tmp/agentic-router-linux-smoke.*) rm -rf -- "$smoke_dir" ;;
    *) printf 'Unexpected smoke directory: %s\n' "$smoke_dir" >&2 ;;
  esac
  exit "$result"
}
trap cleanup EXIT

chmod +x "$app"
ASPNETCORE_URLS="http://127.0.0.1:$port" \
AgenticRouter__DataDirectory="$smoke_dir/data" \
  "$app" >"$smoke_dir/app.log" 2>&1 &
app_pid=$!

for _ in $(seq 1 100); do
  if curl --fail --silent --max-time 2 \
    "http://127.0.0.1:$port/api/setup/status" \
    >"$smoke_dir/status.json"; then
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    printf 'Linux host exited before becoming ready.\n' >&2
    exit 1
  fi
  sleep 0.2
done

test -s "$smoke_dir/status.json"
grep -q '"platform":"linux-x64"' "$smoke_dir/status.json"
grep -q '"profiles":\[' "$smoke_dir/status.json"
grep -Eq '"id":"(standard|vulkan|rocm)"' "$smoke_dir/status.json"
printf 'LINUX_SETUP_SMOKE_OK\n'
