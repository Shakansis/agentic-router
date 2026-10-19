# Model library

Settings > Models and routing > Model library groups installation, filters and per-model preferences. Existing automatic routing, model profiles, favorites, hidden models, aliases, notes and GPU affinity are preserved.

- Browse Ollama opens https://ollama.com/search in a separate tab.
- Install model accepts an exact library name (including namespace and tag). A missing tag means `latest`. Downloads start only on an explicit button/Enter action. The existing Ollama streaming pull path reports provider progress. The original onboarding endpoint continues to restrict downloads to its recommendation set.
- Remove from Ollama asks for confirmation of the exact model. The Host invokes the installed `ollama rm <model>` with separate arguments, no shell, and `OLLAMA_HOST` set to the configured server. Removal waits for active AR inference through the existing request tracker. It does not stop Ollama or rewrite routing, history or preferences. Success requires a zero exit code and absence from the subsequent installed-model listing. Shared blobs are managed by Ollama.
- Both model operations honor the existing safe-mode mutation restriction. Same-model operations cannot overlap. Failure is visible and retryable; a successful CLI exit with an unchanged registry is reported as unverified, never as removed.
- Last used in AR is the latest successful, nonempty inference event for the exact provider/model in retained usage history, including Chat, Execute and model tests. Dates use the browser's locale/time zone. It is not the Ollama file modification date. No recorded use does not mean never used: usage outside AR and expired/purged history are unavailable. No additional persistence or telemetry is introduced.
- Alias/note save with their card action; favorites and hiding save on click. GPU affinity keeps the existing settings footer save behavior. Installation/removal refresh model availability without replacing the user's unsaved settings form.

The library uses the existing settings controls and confirmation modal, with persistent field labels, explicit hints, status feedback, responsive cards, and a single surrounding settings scroll area.

Validation uses browser + Host + an external fake Ollama HTTP/CLI boundary. No user-owned model is downloaded or removed by automated tests.
