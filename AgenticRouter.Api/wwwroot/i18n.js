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
      "memory.model_vram_used": "Ollama Model VRAM Allocation",
      "memory.model_vram_note": "Allocation reported by Ollama. A combined Vulkan allocation can span more than one physical adapter.",
      "memory.adapter_vram_used": "Adapter VRAM Used",
      "memory.adapter_vram_note": "Total dedicated VRAM used on this physical adapter. It can include the Ollama model, other processes, and driver allocations.",
      "memory.allocated_context_window": "Allocated Context Window",
      "memory.allocated_context_note": "Capacity reported by Ollama for the loaded runner, not the number of tokens currently occupied by a prompt.",
      "memory.requested_context_window": "Requested Context Window",
      "memory.requested_context_note": "Context capacity requested by Agentic Router for the loaded runner.",
      "memory.context_runtime": "Estimated Context/Runtime Memory",
      "memory.context_runtime_note": "Estimated from loaded allocation minus installed model size; includes KV cache and other runtime buffers.",
      "memory.no_loaded_model": "No loaded model reported by Ollama.",
      "memory.system_ram_model": "Sys RAM Used by Model",
      "memory.allocated_context_windows": "Allocated Context Windows",
      "memory.model_gpu_summary": "{allocated} allocated by models · {capacity} physical VRAM",
      "memory.gpu_memory_unavailable": "GPU memory unavailable",
      "prompt.value": "Value",
      "toast.close": "Close notification",
      "setup.title": "Local setup",
      "setup.ready": "Core resources are ready",
      "setup.missing": "Complete the required local setup",
      "setup.description": "Install only what you need. Availability is verified from the running tools.",
      "setup.ollama": "Ollama runtime",
      "setup.acceleration_profile": "Acceleration profile",
      "setup.select_profile": "Select Vulkan or ROCm",
      "setup.profile_required": "Choose an acceleration profile before installing Ollama.",
      "setup.backend_evidence": "Observed Ollama backend",
      "setup.backend_not_observed": "not observed",
      "setup.change_profile": "Change acceleration profile",
      "setup.review_change": "Review change",
      "setup.apply_change": "Apply profile change",
      "setup.change_summary": "Change Ollama from {current} to {target}?",
      "setup.change_started": "The reviewed Ollama profile change opened in a terminal.",
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
      "setup.continue": "Continue to chat",
      "setup.hide_before_conversations": "Do not show before new conversations",
      "empty.ready_title": "Ready to chat",
      "empty.ready_description": "Use Auto to classify intent and choose the configured model.",
      "benchmark.custom_prompt.name": "Custom Prompt",
      "benchmark.custom_prompt.switch_aria": "Run Custom Prompt test",
      "benchmark.custom_prompt.switch_detail": "1 test · user review",
      "benchmark.custom_prompt.run_name": "Run name (optional)",
      "benchmark.custom_prompt.run_name_placeholder": "Galaga Test",
      "benchmark.custom_prompt.prompt_label": "Custom Benchmark Prompt",
      "benchmark.custom_prompt.default": "Build a playable Galaga-inspired arcade spaceship game for the browser in this workspace. Create the necessary files, preserve existing work, and validate the result.",
      "benchmark.custom_prompt.help": "This test sends the exact text above. Use any task, including text review or creative writing.",
      "benchmark.custom_prompt.required": "Enter a custom benchmark prompt.",
      "benchmark.custom_prompt.ranking_title": "Custom Prompt · User ranking",
      "benchmark.custom_prompt.ranking_note": "User-assigned quality scores only; independent of the calculated ranking above.",
      "benchmark.custom_prompt.review_column": "Review",
      "benchmark.custom_prompt.user_score_column": "User Score",
      "benchmark.custom_prompt.technical_column": "Technical",
      "benchmark.custom_prompt.technical_completed": "Technical completed",
      "benchmark.custom_prompt.technical_failure": "Technical failure",
      "benchmark.custom_prompt.user_score_hint": "Quality score supplied by the user; telemetry does not modify it.",
      "benchmark.custom_prompt.score_context_manual": "Execution health and telemetry are measured automatically. Quality ranking uses only user-provided scores.",
      "benchmark.custom_prompt.score_context_mixed": "Calculated score and ranking include only predefined tests. Custom Prompt quality uses the user score separately; time, tokens and recoveries never change it.",
      "benchmark.custom_prompt.review_heading": "User quality review",
      "benchmark.custom_prompt.technical_failure_note": "This result ended in a technical failure and does not require a quality score.",
      "benchmark.custom_prompt.score_label": "Score (0–100)",
      "benchmark.custom_prompt.notes_label": "Review / Notes (optional)",
      "benchmark.custom_prompt.save_review": "Save review",
      "benchmark.custom_prompt.update_review": "Update review",
      "benchmark.custom_prompt.score_invalid": "Enter an integer score from 0 to 100.",
      "benchmark.custom_prompt.review_saved": "User review saved.",
      "benchmark.custom_prompt.rerun": "Rerun exact configuration",
      "benchmark.custom_prompt.rerunning": "Rerunning the exact saved configuration…",
      "benchmark.custom_prompt.quality_score": "user score {score}",
      "benchmark.custom_prompt.manual_review": "manual review",
      "benchmark.custom_prompt.predefined_passed": "{passed}/{total} predefined passed",
      "benchmark.custom_prompt.status.awaiting": "Awaiting User Review",
      "benchmark.custom_prompt.status.reviewed": "Reviewed",
      "benchmark.custom_prompt.status.technical_failure": "Technical Failure",
      "benchmark.custom_prompt.status.not_applicable": "Not applicable",
      "benchmark.results.passed": "Passed",
      "benchmark.results.score": "Score",
      "benchmark.results.model_harness": "Model × Harness",
      "benchmark.results.duration": "Duration",
      "benchmark.results.calculated_score_hint": "Score calculated by the active profile from measured evidence.",
      "settings.native_file_creation.model_label": "Native file creation · local model",
      "settings.native_file_creation.limit_label": "File Creation Output Token Limit",
      "settings.native_file_creation.inherit_placeholder": "Inherit model default",
      "settings.native_file_creation.help": "Per-model budget for Native create_file/create_files. Blank inherits; normal output is unchanged. Save settings to apply.",
      "settings.native_file_creation.override_help": "Leave empty to inherit the normal model output limit."
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
      const translation = t(element.dataset.i18n);
      if (translation) {
        element.textContent = translation;
      }
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
        const translation = t(element.dataset[datasetName]);
        if (translation) {
          element.setAttribute(attribute, translation);
        }
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
