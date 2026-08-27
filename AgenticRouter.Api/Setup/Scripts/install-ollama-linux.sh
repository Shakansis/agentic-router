#!/usr/bin/env bash
set -Eeuo pipefail

profile="${1:-}"
manifest_dir="${2:-}"
base_url="https://ollama.com/download/ollama-linux-amd64.tar.zst"
rocm_url="https://ollama.com/download/ollama-linux-amd64-rocm.tar.zst"
cancelled=false

finish() {
  local result=$?
  if [[ -n "${work_dir:-}" && -d "$work_dir" ]]; then
    rm -rf -- "$work_dir"
  fi
  if [[ "$cancelled" == "true" ]]; then
    printf '\nOllama setup was cancelled. Press Enter to close this terminal.\n'
  elif [[ $result -eq 0 ]]; then
    printf '\nOllama setup finished. Press Enter to close this terminal.\n'
  else
    printf '\nOllama setup failed with exit code %s. Press Enter to close this terminal.\n' "$result"
  fi
  read -r _ || true
  exit "$result"
}
trap finish EXIT

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  printf 'This installer supports Linux x86_64 only.\n' >&2
  exit 2
fi
if [[ "$profile" != "standard" && "$profile" != "vulkan" && "$profile" != "rocm" ]]; then
  printf 'Invalid acceleration profile: %s\n' "$profile" >&2
  exit 2
fi
if [[ -z "$manifest_dir" ]]; then
  printf 'The installation manifest directory was not provided.\n' >&2
  exit 2
fi
override_path="/etc/systemd/system/ollama.service.d/agentic-router-acceleration.conf"
if [[ "$profile" != "vulkan" && -e "$override_path" ]]; then
  printf 'A prior Agentic Router Vulkan override exists at %s.\n' "$override_path" >&2
  printf 'Use the future controlled profile-reversal workflow before switching profiles.\n' >&2
  exit 4
fi
for command_name in curl tar sha256sum sudo; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required command is missing: %s\n' "$command_name" >&2
    exit 3
  fi
done

printf 'Agentic Router guided Ollama setup\n'
printf 'Profile: %s\n\n' "$profile"
printf 'This will perform these explicit system changes:\n'
printf '  1. Download the official Ollama Linux x64 base package from ollama.com.\n'
if [[ "$profile" == "rocm" ]]; then
  printf '  2. Download the official supplemental ROCm package from ollama.com.\n'
else
  printf '  2. No supplemental ROCm package will be installed.\n'
fi
printf '  3. Extract package files under /usr using sudo.\n'
printf '  4. Create the ollama service account and systemd unit when absent.\n'
printf '  5. Add the service account to existing render/video groups and your account to the ollama group.\n'
if [[ "$profile" == "vulkan" ]]; then
  printf '  6. Create an Agentic Router-owned systemd override with OLLAMA_VULKAN=1.\n'
else
  printf '  6. Do not force an Ollama acceleration backend through an environment override.\n'
fi
printf '\nNo GPU driver, model, container, capability, or unrestricted shell integration will be installed.\n'
printf 'The exact package file lists and SHA-256 values will be recorded in:\n  %s\n\n' "$manifest_dir"
read -r -p 'Continue with these changes? [y/N] ' answer
if [[ "$answer" != "y" && "$answer" != "Y" ]]; then
  cancelled=true
  printf 'Installation cancelled.\n'
  exit 0
fi

work_dir="$(mktemp -d)"
umask 077
mkdir -p -- "$manifest_dir"
base_package="$work_dir/ollama-linux-amd64.tar.zst"
rocm_package="$work_dir/ollama-linux-amd64-rocm.tar.zst"

printf '\nDownloading official Ollama base package...\n'
curl --fail --location --silent --show-error "$base_url" --output "$base_package"
tar --zstd -tf "$base_package" > "$manifest_dir/base.files"
base_hash="$(sha256sum "$base_package")"
printf '%s  ollama-linux-amd64.tar.zst\n' "${base_hash%% *}" > "$manifest_dir/base.sha256"

if [[ "$profile" == "rocm" ]]; then
  printf 'Downloading official Ollama ROCm supplemental package...\n'
  curl --fail --location --silent --show-error "$rocm_url" --output "$rocm_package"
  tar --zstd -tf "$rocm_package" > "$manifest_dir/rocm.files"
  rocm_hash="$(sha256sum "$rocm_package")"
  printf '%s  ollama-linux-amd64-rocm.tar.zst\n' "${rocm_hash%% *}" > "$manifest_dir/rocm.sha256"
fi

printf 'Requesting sudo authorization for the listed system changes...\n'
sudo -v
sudo tar --zstd -xf "$base_package" -C /usr
if [[ "$profile" == "rocm" ]]; then
  sudo tar --zstd -xf "$rocm_package" -C /usr
fi

if ! id -u ollama >/dev/null 2>&1; then
  sudo useradd -r -s /bin/false -U -m -d /usr/share/ollama ollama
fi
current_user="$(id -un)"
sudo usermod -a -G ollama "$current_user"
if getent group render >/dev/null 2>&1; then
  sudo usermod -a -G render ollama
fi
if getent group video >/dev/null 2>&1; then
  sudo usermod -a -G video ollama
fi

service_path="/etc/systemd/system/ollama.service"
if [[ ! -e "$service_path" ]]; then
  service_file="$work_dir/ollama.service"
  cat > "$service_file" <<'SERVICE'
[Unit]
Description=Ollama Service
After=network-online.target

[Service]
ExecStart=/usr/bin/ollama serve
User=ollama
Group=ollama
Restart=always
RestartSec=3
Environment="PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"

[Install]
WantedBy=multi-user.target
SERVICE
  sudo install -m 0644 "$service_file" "$service_path"
else
  printf 'Existing Ollama systemd unit was preserved: %s\n' "$service_path"
fi

if [[ "$profile" == "vulkan" ]]; then
  override_file="$work_dir/agentic-router-acceleration.conf"
  printf '[Service]\nEnvironment="OLLAMA_VULKAN=1"\n' > "$override_file"
  sudo install -d -m 0755 "$(dirname "$override_path")"
  sudo install -m 0644 "$override_file" "$override_path"
fi

{
  printf 'schemaVersion=1\n'
  printf 'requestedProfile=%s\n' "$profile"
  printf 'basePackageUrl=%s\n' "$base_url"
  if [[ "$profile" == "rocm" ]]; then
    printf 'supplementalPackageUrl=%s\n' "$rocm_url"
  fi
  printf 'recordedAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "$manifest_dir/install.properties"

if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  sudo systemctl daemon-reload
  sudo systemctl enable --now ollama
  sudo systemctl --no-pager --full status ollama || true
else
  printf '\nsystemd is not active. Start Ollama manually with: ollama serve\n'
fi

printf '\nLog out and back in before relying on the new ollama group membership.\n'
printf 'Agentic Router will independently verify the running backend after setup.\n'
