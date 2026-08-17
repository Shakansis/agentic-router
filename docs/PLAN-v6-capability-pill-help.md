# PLAN v6: Capability pill help

## Goal

Make each composer capability pill explain its meaning, current state, and authoritative documentation without adding permanent visual noise.

## Scope

- Show an accessible hover, focus, and click popover for every rendered pill.
- Open popovers above the pills and keep an invisible pointer bridge across the visual gap.
- State whether the provider, capability, or role is active, enabled, or only available.
- Link Ollama capabilities to official Ollama documentation and Router roles to the official project README.
- Open documentation in a new browser tab with safe external-link attributes.

## Ordered work

1. Add structured help metadata to capability rendering.
2. Add accessible disclosure and dismissal behavior.
3. Style the upward popovers and their pointer bridge without changing the compact pill layout.
4. Extend the existing deterministic composer E2E and validate the change.
