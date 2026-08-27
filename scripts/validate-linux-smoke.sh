#!/usr/bin/env bash
set -Eeuo pipefail

publish_dir="${1:?Usage: validate-linux-smoke.sh PUBLISH_DIRECTORY [PORT]}"
port="${2:-58741}"
if [[ -f "$publish_dir/AgenticRouter" ]]; then
  app="$publish_dir/AgenticRouter"
else
  app="$publish_dir/AgenticRouter.Api"
fi

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  printf 'This smoke test requires Linux x86_64.\n' >&2
  exit 2
fi
if [[ ! -f "$app" ]]; then
  printf 'Published AgenticRouter.Api executable was not found: %s\n' "$app" >&2
  exit 2
fi
for command_name in curl grep python3 seq; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required smoke-test command is missing: %s\n' "$command_name" >&2
    exit 3
  fi
done

smoke_dir="$(mktemp -d /tmp/agentic-router-linux-smoke.XXXXXX)"
app_pid=""
probe_pid=""
cleanup() {
  local result=$?
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill -TERM "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  if [[ -n "$probe_pid" ]] && kill -0 "$probe_pid" 2>/dev/null; then
    kill -TERM "$probe_pid" 2>/dev/null || true
    wait "$probe_pid" 2>/dev/null || true
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

mkdir -p "$smoke_dir/data/installation-manifests/ollama"
cat > "$smoke_dir/data/installation-manifests/ollama/install.properties" <<'MANIFEST'
schemaVersion=1
requestedProfile=rocm
MANIFEST
cat > "$smoke_dir/data/installation-manifests/ollama/base.files" <<'MANIFEST'
usr/bin/ollama
usr/lib/ollama/libbase.so
usr/lib/ollama/shared.so
MANIFEST
cat > "$smoke_dir/data/installation-manifests/ollama/rocm.files" <<'MANIFEST'
usr/lib/ollama/shared.so
usr/lib/ollama/rocm/libhip.so
MANIFEST

python3 -c '
import mmap
import pathlib
import sys
import time

path = pathlib.Path(sys.argv[1])
path.write_bytes(bytes(4096))
with path.open("r+b") as stream:
    with mmap.mmap(stream.fileno(), 0):
        time.sleep(30)
' "$smoke_dir/libggml-hip.so" "ollama runner" &
probe_pid=$!

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
grep -q '"manifestProfile":"rocm"' "$smoke_dir/status.json"
grep -q '"observedBackend":"rocm"' "$smoke_dir/status.json"
grep -q '"state":"verified"' "$smoke_dir/status.json"

curl --fail --silent --max-time 5 \
  -H 'Content-Type: application/json' \
  -d '{"targetProfile":"standard"}' \
  "http://127.0.0.1:$port/api/setup/ollama/profile-switch/plan" \
  > "$smoke_dir/plan.json"
grep -q '"currentProfile":"rocm"' "$smoke_dir/plan.json"
grep -q '"targetProfile":"standard"' "$smoke_dir/plan.json"
grep -q '"rocmOnlyFilesToRemove":1' "$smoke_dir/plan.json"
grep -q '"preservesModelsAndData":true' "$smoke_dir/plan.json"

apply_status="$(curl --silent --max-time 5 -o "$smoke_dir/apply.json" -w '%{http_code}' \
  -H 'Content-Type: application/json' \
  -d '{"planId":"not-a-reviewed-plan"}' \
  "http://127.0.0.1:$port/api/setup/ollama/profile-switch/apply")"
test "$apply_status" = "409"

manifest_before="$(sha256sum "$smoke_dir/data/installation-manifests/ollama/install.properties")"
printf 'n\n\n' | bash "$publish_dir/scripts/switch-ollama-linux-profile.sh" \
  rocm standard "$smoke_dir/data/installation-manifests/ollama" \
  > "$smoke_dir/switch-cancel.log"
grep -q 'Profile change cancelled.' "$smoke_dir/switch-cancel.log"
manifest_after="$(sha256sum "$smoke_dir/data/installation-manifests/ollama/install.properties")"
test "$manifest_before" = "$manifest_after"

printf 'etc/agentic-router-unsafe\n' \
  >> "$smoke_dir/data/installation-manifests/ollama/rocm.files"
unsafe_status="$(curl --silent --max-time 5 -o "$smoke_dir/unsafe-plan.json" -w '%{http_code}' \
  -H 'Content-Type: application/json' \
  -d '{"targetProfile":"standard"}' \
  "http://127.0.0.1:$port/api/setup/ollama/profile-switch/plan")"
test "$unsafe_status" = "409"
grep -q '"code":"profile-switch-manifest-unsafe"' "$smoke_dir/unsafe-plan.json"
printf 'LINUX_SETUP_SMOKE_OK\n'
