#!/usr/bin/env bash
set -Eeuo pipefail

archive="${1:?Usage: validate-linux-package.sh ARCHIVE [PORT]}"
port="${2:-58744}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -f "$archive" ]]; then
  printf 'Linux package archive was not found: %s\n' "$archive" >&2
  exit 2
fi
extract_dir="$(mktemp -d /tmp/agentic-router-package.XXXXXX)"
cleanup() {
  local result=$?
  case "$extract_dir" in
    /tmp/agentic-router-package.*) rm -rf -- "$extract_dir" ;;
    *) printf 'Unexpected package directory: %s\n' "$extract_dir" >&2 ;;
  esac
  exit "$result"
}
trap cleanup EXIT

tar -xzf "$archive" -C "$extract_dir"
for executable in \
  AgenticRouter \
  run-agentic-router.sh \
  scripts/install-ollama-linux.sh \
  scripts/switch-ollama-linux-profile.sh; do
  if [[ ! -x "$extract_dir/$executable" ]]; then
    printf 'Required package entry is not executable: %s\n' "$executable" >&2
    tar -tvzf "$archive" | grep -F "$executable" >&2 || true
    exit 3
  fi
done

bash "$script_dir/validate-linux-smoke.sh" "$extract_dir" "$port"
printf 'LINUX_PACKAGE_PERMISSIONS_AND_RUNTIME_OK\n'
