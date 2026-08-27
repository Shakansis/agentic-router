(() => {
  const catalogs = {
    en: Object.freeze({
      "action.cancel": "Cancel",
      "action.cancel_response": "Cancel active response",
      "action.close": "Close",
      "action.confirm": "Confirm",
      "action.edit": "Edit",
      "action.remove": "Remove",
      "action.save": "Save",
      "buffer.title": "Queued messages",
      "buffer.empty": "No queued messages",
      "buffer.queue": "Queue",
      "buffer.queue_title": "Add this message to the browser-only queue",
      "buffer.run_next": "Run next",
      "buffer.edit_label": "Edit queued message",
      "buffer.editing": "Editing queued message",
      "buffer.empty_error": "A queued message cannot be empty.",
      "buffer.count": "{count} queued",
      "steer.action": "Steer",
      "steer.available": "Send this message into the active turn",
      "steer.unavailable": "Steer is available only for Codex and Qwen Code",
      "steer.unavailable_harness": "Steer is unavailable for {harness}. Use Codex or Qwen Code.",
      "steer.no_active": "Steer requires an active Codex or Qwen Code turn",
      "steer.empty": "Type a message to steer the active turn",
      "steer.sending": "Sending steering message",
      "steer.accepted": "Steering submitted to the active {harness} turn",
      "modal.confirm.eyebrow": "Confirmation",
      "modal.confirm.title": "Confirm action",
      "context.live": "live",
      "context.active": "Active context",
      "context.generated_output": "Generated output",
      "context.live_estimate_warning": "Live context is estimated because {harness} does not report exact token usage during this part of the turn.",
      "memory.gpu_auto": "Automatic GPU selection",
      "memory.gpu_unknown": "GPU not reported",
      "memory.cpu": "CPU",
      "memory.details": "Details",
      "memory.model_vram_used": "Model VRAM Used",
      "memory.system_driver_vram": "System/Driver VRAM",
      "memory.context_share": "Context Share",
      "memory.context_runtime": "Context/Runtime Memory",
      "memory.context_runtime_note": "Estimated from loaded allocation minus installed model size; includes KV cache and other runtime buffers.",
      "memory.no_loaded_model": "No loaded model reported by Ollama.",
      "memory.system_ram_model": "Sys RAM Used by Model",
      "memory.total_context_window": "Total Context Window",
      "memory.gpu_memory_unavailable": "GPU memory unavailable",
      "prompt.value": "Value",
      "toast.close": "Close notification",
      "setup.title": "Local setup",
      "setup.ready": "Core resources are ready",
      "setup.missing": "Complete the required local setup",
      "setup.description": "Install only what you need. Availability is verified from the running tools.",
      "setup.ollama": "Ollama runtime",
      "setup.models": "GPU-compatible models",
      "setup.harnesses": "Optional harnesses",
      "setup.install": "Install",
      "setup.pull": "Download",
      "setup.retry": "Retry",
      "setup.refresh": "Refresh",
      "setup.available": "Available",
      "setup.missing_status": "Missing",
      "setup.started": "Installer started",
      "setup.downloading": "Downloading",
      "setup.installed": "Installed",
      "setup.recommended": "Best fit",
      "setup.harness_recommended": "Recommended for Execute",
      "setup.native": "Built in",
      "setup.optional": "Optional",
      "setup.gpu": "Largest detected GPU: {memory}",
      "setup.gpu_unknown": "GPU memory could not be determined; showing a conservative model.",
      "setup.read_only": "Setup actions are disabled in safe mode.",
      "setup.action_started": "{resource} setup started. Availability will update automatically.",
      "setup.model_started": "{resource} download started.",
      "setup.refresh_failed": "Local setup status could not be refreshed.",
      "empty.ready_title": "Ready to chat",
      "empty.ready_description": "Use Auto to classify intent and choose the configured model."
    })
  };
  const fallbackLocale = "en";
  let locale = document.documentElement.dataset.locale || fallbackLocale;

  function t(key, values = {}) {
    const template = catalogs[locale]?.[key]
      ?? catalogs[fallbackLocale][key];

    if (typeof template !== "string") {
      console.warn(`Missing translation: ${key}`);
      return "";
    }

    return template.replace(
      /\{([a-zA-Z0-9_]+)\}/g,
      (_, name) => String(values[name] ?? `{${name}}`)
    );
  }

  function localizeDocument(root = document) {
    for (const element of root.querySelectorAll("[data-i18n]")) {
      element.textContent = t(element.dataset.i18n);
    }
    for (const attribute of ["aria-label", "placeholder", "title"]) {
      const datasetName = `i18n${attribute.replace(
        /-([a-z])/g,
        (_, letter) => letter.toUpperCase()
      ).replace(/^./, letter => letter.toUpperCase())}`;
      const selector = `[data-${datasetName.replace(
        /[A-Z]/g,
        letter => `-${letter.toLowerCase()}`
      )}]`;
      for (const element of root.querySelectorAll(selector)) {
        element.setAttribute(attribute, t(element.dataset[datasetName]));
      }
    }
    document.documentElement.lang = locale;
    document.documentElement.dataset.locale = locale;
  }

  function setLocale(nextLocale) {
    locale = catalogs[nextLocale] ? nextLocale : fallbackLocale;
    localizeDocument();
  }

  function registerCatalog(nextLocale, messages) {
    if (!nextLocale || nextLocale === fallbackLocale || !messages) {
      return false;
    }
    catalogs[nextLocale] = Object.freeze({
      ...catalogs[fallbackLocale],
      ...messages
    });
    return true;
  }

  window.AgenticRouterI18n = Object.freeze({
    fallbackLocale,
    get locale() {
      return locale;
    },
    localizeDocument,
    registerCatalog,
    setLocale,
    t
  });
})();
