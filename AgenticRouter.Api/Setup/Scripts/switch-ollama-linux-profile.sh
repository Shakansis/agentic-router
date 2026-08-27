#!/usr/bin/env bash
set -Eeuo pipefail

current_profile="${1:-}"
target_profile="${2:-}"
manifest_dir="${3:-}"
base_url="https://ollama.com/download/ollama-linux-amd64.tar.zst"
rocm_url="https://ollama.com/download/ollama-linux-amd64-rocm.tar.zst"
override_path="/etc/systemd/system/ollama.service.d/agentic-router-acceleration.conf"
service_stopped=false
cancelled=false

finish() {
  local result=$?
  if [[ $result -ne 0 && "$service_stopped" == "true" ]]; then
    sudo systemctl start ollama >/dev/null 2>&1 || true
  fi
  if [[ -n "${work_dir:-}" && -d "$work_dir" ]]; then
    rm -rf -- "$work_dir"
  fi
  if [[ "$cancelled" == "true" ]]; then
    printf '\nOllama profile change was cancelled. Press Enter to close this terminal.\n'
  elif [[ $result -eq 0 ]]; then
    printf '\nOllama profile change finished. Press Enter to close this terminal.\n'
  else
    printf '\nOllama profile change failed with exit code %s. Press Enter to close this terminal.\n' "$result"
  fi
  read -r _ || true
  exit "$result"
}
trap finish EXIT

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  printf 'This profile-change workflow supports Linux x86_64 only.\n' >&2
  exit 2
fi
for profile in "$current_profile" "$target_profile"; do
  if [[ "$profile" != "standard" && "$profile" != "vulkan" && "$profile" != "rocm" ]]; then
    printf 'Invalid Ollama profile: %s\n' "$profile" >&2
    exit 2
  fi
done
if [[ "$current_profile" == "$target_profile" ]]; then
  printf 'The current and target Ollama profiles are identical.\n' >&2
  exit 2
fi
if [[ -z "$manifest_dir" || ! -f "$manifest_dir/install.properties" || ! -f "$manifest_dir/base.files" ]]; then
  printf 'The managed Ollama installation manifests are incomplete.\n' >&2
  exit 3
fi
for command_name in comm curl install sha256sum sort sudo tar wc; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required command is missing: %s\n' "$command_name" >&2
    exit 3
  fi
done

recorded_profile="$(while IFS= read -r line; do
  case "$line" in
    requestedProfile=*) printf '%s' "${line#requestedProfile=}"; break ;;
  esac
done < "$manifest_dir/install.properties")"
if [[ "$recorded_profile" != "$current_profile" ]]; then
  printf 'The managed profile changed after review. Expected %s, found %s.\n' \
    "$current_profile" "$recorded_profile" >&2
  exit 4
fi

work_dir="$(mktemp -d)"
umask 077
old_base_normalized="$work_dir/old-base.files"
old_rocm_normalized="$work_dir/old-rocm.files"
rocm_only="$work_dir/old-rocm-only.files"

normalize_manifest() {
  local source_file="$1"
  local target_file="$2"
  : > "$target_file"
  while IFS= read -r raw; do
    if [[ "$raw" == /* || "$raw" == \\* ]]; then
      printf 'Unsafe absolute package-manifest path: %s\n' "$raw" >&2
      exit 5
    fi
    local normalized="${raw#./}"
    normalized="${normalized#/}"
    [[ -z "$normalized" || "$normalized" == */ ]] && continue
    if [[ "$normalized" == *../* ]]; then
      printf 'Unsafe package-manifest path: %s\n' "$raw" >&2
      exit 5
    fi
    printf '%s\n' "$normalized" >> "$target_file"
  done < "$source_file"
  sort -u -o "$target_file" "$target_file"
}

normalize_manifest "$manifest_dir/base.files" "$old_base_normalized"
remove_count=0
if [[ "$current_profile" == "rocm" ]]; then
  if [[ ! -f "$manifest_dir/rocm.files" ]]; then
    printf 'The ROCm package manifest is required before leaving the ROCm profile.\n' >&2
    exit 3
  fi
  normalize_manifest "$manifest_dir/rocm.files" "$old_rocm_normalized"
  comm -23 "$old_rocm_normalized" "$old_base_normalized" > "$rocm_only"
  while IFS= read -r relative; do
    [[ -z "$relative" ]] && continue
    if [[ "$relative" != usr/lib/ollama/* ]]; then
      printf 'Unsafe ROCm-only removal path: %s\n' "$relative" >&2
      exit 5
    fi
  done < "$rocm_only"
  remove_count="$(wc -l < "$rocm_only")"
fi

printf 'Agentic Router controlled Ollama profile change\n'
printf 'Current package profile: %s\n' "$current_profile"
printf 'Target package profile:  %s\n\n' "$target_profile"
printf 'The reviewed operation will:\n'
printf '  1. Download the current official Linux x64 base package from ollama.com.\n'
if [[ "$target_profile" == "rocm" ]]; then
  printf '  2. Download the current official ROCm supplemental package from ollama.com.\n'
else
  printf '  2. Install no ROCm supplemental package in the target profile.\n'
fi
printf '  3. Stop Ollama only while package files and the Agentic Router override are changed.\n'
if [[ "$current_profile" == "rocm" ]]; then
  printf '  4. Remove exactly %s ROCm-only files from the saved package manifests.\n' "$remove_count"
else
  printf '  4. No package files require removal from the current profile.\n'
fi
printf '  5. Re-extract the base package to restore shared files coherently.\n'
if [[ "$target_profile" == "vulkan" ]]; then
  printf '  6. Set the Agentic Router-owned OLLAMA_VULKAN=1 systemd override.\n'
else
  printf '  6. Remove only the Agentic Router-owned Vulkan override when present.\n'
fi
printf '  7. Restart Ollama for independent backend verification.\n\n'
printf 'Models, model storage, user settings, GPU drivers, service accounts, and unrelated files are preserved.\n\n'
read -r -p 'Apply this reviewed profile change? [y/N] ' answer
if [[ "$answer" != "y" && "$answer" != "Y" ]]; then
  cancelled=true
  printf 'Profile change cancelled.\n'
  exit 0
fi

base_package="$work_dir/ollama-linux-amd64.tar.zst"
rocm_package="$work_dir/ollama-linux-amd64-rocm.tar.zst"
new_base_files="$work_dir/base.files"
new_rocm_files="$work_dir/rocm.files"
printf '\nDownloading official Ollama base package...\n'
curl --fail --location --silent --show-error "$base_url" --output "$base_package"
tar --zstd -tf "$base_package" > "$new_base_files"
base_hash="$(sha256sum "$base_package")"
printf '%s  ollama-linux-amd64.tar.zst\n' "${base_hash%% *}" > "$work_dir/base.sha256"
if [[ "$target_profile" == "rocm" ]]; then
  printf 'Downloading official Ollama ROCm supplemental package...\n'
  curl --fail --location --silent --show-error "$rocm_url" --output "$rocm_package"
  tar --zstd -tf "$rocm_package" > "$new_rocm_files"
  rocm_hash="$(sha256sum "$rocm_package")"
  printf '%s  ollama-linux-amd64-rocm.tar.zst\n' "${rocm_hash%% *}" > "$work_dir/rocm.sha256"
fi

printf 'Requesting sudo authorization for the reviewed system changes...\n'
sudo -v
if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  if systemctl is-active --quiet ollama; then
    sudo systemctl stop ollama
    service_stopped=true
  fi
fi

if [[ "$current_profile" == "rocm" ]]; then
  while IFS= read -r relative; do
    [[ -z "$relative" ]] && continue
    sudo rm -f -- "/$relative"
  done < "$rocm_only"
fi

sudo tar --zstd -xf "$base_package" -C /usr
if [[ "$target_profile" == "rocm" ]]; then
  sudo tar --zstd -xf "$rocm_package" -C /usr
fi

if [[ "$target_profile" == "vulkan" ]]; then
  override_file="$work_dir/agentic-router-acceleration.conf"
  printf '[Service]\nEnvironment="OLLAMA_VULKAN=1"\n' > "$override_file"
  sudo install -d -m 0755 "$(dirname "$override_path")"
  sudo install -m 0644 "$override_file" "$override_path"
else
  sudo rm -f -- "$override_path"
fi

install -m 0600 "$new_base_files" "$manifest_dir/base.files"
install -m 0600 "$work_dir/base.sha256" "$manifest_dir/base.sha256"
if [[ "$target_profile" == "rocm" ]]; then
  install -m 0600 "$new_rocm_files" "$manifest_dir/rocm.files"
  install -m 0600 "$work_dir/rocm.sha256" "$manifest_dir/rocm.sha256"
else
  rm -f -- "$manifest_dir/rocm.files" "$manifest_dir/rocm.sha256"
fi
{
  printf 'schemaVersion=1\n'
  printf 'requestedProfile=%s\n' "$target_profile"
  printf 'basePackageUrl=%s\n' "$base_url"
  if [[ "$target_profile" == "rocm" ]]; then
    printf 'supplementalPackageUrl=%s\n' "$rocm_url"
  fi
  printf 'recordedAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "$manifest_dir/install.properties"

if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  sudo systemctl daemon-reload
  sudo systemctl enable --now ollama
  service_stopped=false
  sudo systemctl --no-pager --full status ollama || true
else
  printf '\nsystemd is not active. Start Ollama manually with: ollama serve\n'
fi

printf '\nThe managed package profile is now %s.\n' "$target_profile"
printf 'Agentic Router will report requested, manifest, observed backend, and fallback states separately.\n'
