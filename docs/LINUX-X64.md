# Linux x64 support

Agentic Router ships a self-contained `linux-x64` package from the same source
and Host policy used by Windows. This release does not support Linux ARM64 or
macOS, and it does not coordinate heterogeneous AMD + NVIDIA workloads.

## Start the portable package

```bash
tar -xzf AgenticRouter-0.9.18_alpha-linux-x64.tar.gz
cd AgenticRouter-0.9.18_alpha-linux-x64
chmod +x AgenticRouter run-agentic-router.sh
./run-agentic-router.sh
```

Open the local URL printed in the terminal. Application settings and optional
history are stored in the adjacent `data/` directory.

## Ollama acceleration profiles

The first-run setup offers only profiles compatible with detected hardware. A
mixed AMD + NVIDIA machine can choose the server-wide package/backend profile,
but Agentic Router does not yet coordinate work across those vendors.

### Standard / CUDA

Installs the official Linux x64 base package. Ollama selects CPU or NVIDIA CUDA
without an Agentic Router backend override.

### Vulkan

Installs the official base package and creates an Agentic Router-owned systemd
override containing `OLLAMA_VULKAN=1`. Ollama currently documents Vulkan as
experimental. A working vendor/Mesa Vulkan driver is required; Agentic Router
does not install it or grant `cap_perfmon` automatically.

### ROCm

Installs the official base package plus
`ollama-linux-amd64-rocm.tar.zst`. ROCm requires supported AMD hardware and a
compatible ROCm v7 driver. Agentic Router does not install or upgrade drivers.

The visible installer lists package extraction, service-account, group, and
systemd changes before requesting confirmation and `sudo`. It records package
file manifests and SHA-256 values for later review.

## Evidence and fallback

Setup keeps these facts separate:

- **requested profile**: the user's explicit setup choice;
- **manifest profile**: the package combination recorded by the managed installer;
- **observed backend**: CUDA, Vulkan, or ROCm libraries mapped by an active Ollama runner;
- **fallback**: a requested accelerated profile with a running `/api/ps` model reporting zero VRAM.

No running model means the backend remains `not-observed`; configuration alone
does not count as verification.

## Changing profiles

A managed installation can review a change to another hardware-compatible
profile from the setup surface. The Host computes an expiring plan from the
saved manifests. Applying it requires confirmation in the browser and again in
the visible terminal.

The change workflow:

1. downloads the current official base package and, for ROCm, its supplement;
2. temporarily stops the systemd service;
3. removes only ROCm-exclusive files proven by `rocm.files - base.files`, and
   only below `/usr/lib/ollama`;
4. re-extracts the base package to restore shared files;
5. updates only the Agentic Router-owned Vulkan override;
6. restarts Ollama for independent backend observation.

Models, model storage, Agentic Router data, GPU drivers, and unrelated files
are excluded. Missing, stale, or unsafe manifests stop the workflow.

## Desktop integrations

- Protected cloud keys require `secret-tool` (`libsecret-tools`) and an active
  Secret Service-compatible desktop keyring. Keys are never stored as plaintext
  fallback files.
- Folder selection uses `zenity`, then `kdialog`; manual path entry remains
  available when neither exists.
- **View folder** uses `xdg-open` from `xdg-utils`.
- Ollama startup-service management requires systemd. Without active systemd,
  the installer leaves the package installed and instructs the user to run
  `ollama serve` manually.

## Validation boundary

Automated validation covers Release builds, all Windows E2E tests, Linux x64
publish, script syntax, Linux process startup, setup/profile API contracts,
backend evidence, approval gating, cancellation, and unsafe-manifest rejection.
Physical Linux GPU behavior remains a manual acceptance gate before merge or
GitHub publication.
