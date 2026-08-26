(() => {
  const catalogs = {
    en: Object.freeze({
      "action.cancel": "Cancel",
      "action.close": "Close",
      "action.confirm": "Confirm",
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
      "toast.close": "Close notification"
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
