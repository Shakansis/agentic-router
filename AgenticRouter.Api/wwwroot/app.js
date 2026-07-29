const state = {
  models: [],
  devices: [],
  settings: null,
  history: [],
  requestController: null,
  autoFollow: true,
  runtimeTimer: null,
  activeAssistant: null,
  editingTurn: null
};

const elements = {};
let resizeObserver;

document.addEventListener("DOMContentLoaded", initialize);

async function initialize() {
  bindElements();
  bindEvents();
  initializeScrollFollowing();

  try {
    await loadApplicationState();
  } catch (error) {
    elements.providerBadge.textContent = "Erro";
    elements.providerBadge.className = "badge error";
    elements.providerDetail.textContent = error.message;
  }

  await refreshRuntimeStatus();
  scheduleRuntimeRefresh();
  elements.messageInput.focus();
}

function bindElements() {
  for (const id of [
    "messages",
    "empty-state",
    "composer",
    "message-input",
    "model-selector",
    "send-button",
    "composer-status",
    "provider-badge",
    "provider-detail",
    "model-count",
    "device-count",
    "device-diagnostic",
    "settings-dialog",
    "settings-form",
    "settings-errors",
    "save-status",
    "intentions-grid",
    "ollama-url",
    "router-model",
    "default-model",
    "default-gpu",
    "jump-latest",
    "runtime-summary",
    "runtime-memory-list",
    "runtime-model-list",
    "resident-model-status"
  ]) {
    elements[toCamelCase(id)] = document.querySelector(`#${id}`);
  }
}

function bindEvents() {
  elements.composer.addEventListener("submit", handleComposerSubmit);
  elements.composer.addEventListener("click", handleComposerClick);
  elements.messageInput.addEventListener("keydown", handleComposerKeyDown);
  elements.messageInput.addEventListener("input", resizeComposer);
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.messages.addEventListener("scroll", handleConversationScroll);
  elements.jumpLatest.addEventListener("click", resumeAutoFollow);
  document.addEventListener("visibilitychange", handleVisibilityChange);
  document.querySelector("#open-settings").addEventListener("click", openSettings);
  document.querySelector("#close-settings").addEventListener("click", closeSettings);
  document.querySelector("#cancel-settings").addEventListener("click", closeSettings);
}

function initializeScrollFollowing() {
  resizeObserver = new ResizeObserver(
    () => {
      if (state.autoFollow) {
        requestAnimationFrame(scrollToBottom);
      }
    }
  );
  resizeObserver.observe(elements.messages);
}

async function loadApplicationState() {
  const [settings, modelsResponse, devicesResponse] = await Promise.all([
    fetchJson("/api/settings"),
    fetchJson("/api/models"),
    fetchJson("/api/devices")
  ]);

  state.settings = settings;
  state.models = modelsResponse.models;
  state.devices = devicesResponse.devices;
  updateProviderStatus(modelsResponse);
  updateDeviceStatus(devicesResponse);
  renderComposerModels();
  renderSettings();
}

function updateProviderStatus(response) {
  elements.providerBadge.textContent = response.available ? "Online" : "Indisponível";
  elements.providerBadge.className = `badge ${response.available ? "success" : "error"}`;
  elements.providerDetail.textContent = response.available
    ? "HTTP conectado"
    : response.error?.message ?? "Falha de conexão";
  elements.modelCount.textContent =
    `${response.models.length} instalado${response.models.length === 1 ? "" : "s"}`;
}

function updateDeviceStatus(response) {
  const physicalDevices = response.devices.filter(device => !device.isAuto);
  elements.deviceCount.textContent =
    `${physicalDevices.length} detectado${physicalDevices.length === 1 ? "" : "s"}`;
  elements.deviceDiagnostic.textContent = response.diagnostic ?? "";
}

async function refreshRuntimeStatus() {
  if (document.hidden) {
    return;
  }

  try {
    renderRuntimeStatus(
      await fetchJson("/api/runtime/status")
    );
  } catch (error) {
    elements.runtimeSummary.textContent = "Memória indisponível";
    elements.residentModelStatus.textContent = error.message;
  }
}

function renderRuntimeStatus(runtime) {
  const compact = [];
  const memoryRows = [];
  const ram = runtime.systemMemory;

  if (ram.status === "available") {
    compact.push(
      `RAM ${formatGiB(ram.usedBytes)} / ${formatGiB(ram.totalBytes)} ${formatPercent(ram.usedPercent)}`
    );
    memoryRows.push(
      memoryRow(
        "RAM do sistema",
        ram.usedBytes,
        ram.totalBytes,
        ram.usedPercent,
        ram.diagnostic,
        "system"
      )
    );
  } else {
    compact.push("RAM n/d");
    memoryRows.push(
      diagnosticRow(
        "RAM do sistema",
        ram.diagnostic
      )
    );
  }

  for (const device of runtime.devices) {
    compact.push(
      device.usedDedicatedMemoryBytes == null
        ? `${device.name} n/d`
        : `${device.name} ${formatGiB(device.usedDedicatedMemoryBytes)} / `
          + `${formatGiB(device.totalDedicatedMemoryBytes)} ${formatPercent(device.usedPercent)}`
    );
    memoryRows.push(
      device.usedDedicatedMemoryBytes == null
        ? diagnosticRow(
          device.name,
          device.diagnostic ?? `Total dedicado: ${formatGiB(device.totalDedicatedMemoryBytes)}`,
          "partial"
        )
        : memoryRow(
          device.name,
          device.usedDedicatedMemoryBytes,
          device.totalDedicatedMemoryBytes,
          device.usedPercent,
          device.diagnostic,
          "gpu"
        )
    );
  }

  if (runtime.devicesStatus === "unavailable") {
    memoryRows.push(
      diagnosticRow(
        "Dispositivos gráficos",
        runtime.devicesDiagnostic
      )
    );
  }

  compact.push(
    `Modelos ${runtime.loadedModels.length} carregado${runtime.loadedModels.length === 1 ? "" : "s"}`
  );
  elements.runtimeSummary.textContent = compact.join(" · ");
  elements.runtimeMemoryList.replaceChildren(...memoryRows);
  elements.runtimeModelList.replaceChildren(
    ...(runtime.loadedModels.length === 0
      ? [
        diagnosticRow(
          runtime.loadedModelsStatus === "unavailable"
            ? "Telemetria do Ollama"
            : "Nenhum modelo reportado",
          runtime.loadedModelsDiagnostic
            ?? "O Ollama não informou modelos carregados em /api/ps."
        )
      ]
      : runtime.loadedModels.map(model => loadedModelRow(model)))
  );
  elements.residentModelStatus.textContent =
    `${runtime.residentModel.configuredModel || "não configurado"} · `
    + `${runtime.residentModel.state}`
    + `${runtime.residentModel.loaded ? " · carregado" : ""}`
    + `${runtime.residentModel.diagnostic ? ` · ${runtime.residentModel.diagnostic}` : ""}`;
  elements.residentModelStatus.dataset.state = runtime.residentModel.state;
}

function memoryRow(name, used, total, percent, diagnostic, kind) {
  const row = document.createElement("div");
  row.className = `runtime-row memory ${kind}`;
  const header = document.createElement("div");
  header.className = "runtime-row-header";
  const label = document.createElement("strong");
  label.textContent = name;
  const value = document.createElement("span");
  value.textContent =
    `${formatGiB(used)} / ${formatGiB(total)} · ${formatPercent(percent)}`;
  const meter = document.createElement("div");
  meter.className = "runtime-meter";
  meter.setAttribute("role", "progressbar");
  meter.setAttribute("aria-label", `${name}: ${formatPercent(percent)}`);
  meter.setAttribute("aria-valuemin", "0");
  meter.setAttribute("aria-valuemax", "100");
  meter.setAttribute("aria-valuenow", String(Math.round(percent)));
  const fill = document.createElement("span");
  const normalized = Math.max(0, Math.min(100, percent));
  fill.className = normalized >= 90
    ? "critical"
    : normalized >= 75
      ? "warning"
      : "";
  fill.style.width = `${normalized}%`;
  meter.append(fill);
  row.title = diagnostic ?? "";
  header.append(label, value);
  row.append(header, meter);
  return row;
}

function diagnosticRow(name, diagnostic, status = "unavailable") {
  const row = document.createElement("div");
  row.className = `runtime-row diagnostic ${status}`;
  const label = document.createElement("strong");
  label.textContent = name;
  const value = document.createElement("span");
  value.textContent = diagnostic ?? "Indisponível";
  row.append(label, value);
  return row;
}

function loadedModelRow(model) {
  const row = document.createElement("div");
  row.className = "loaded-model-row";
  const name = document.createElement("strong");
  name.textContent = `${model.name}${model.isResidentModel ? " · residente" : ""}`;
  const details = document.createElement("span");
  details.textContent =
    `Total ${formatGiB(model.totalSizeBytes)} · VRAM ${formatGiB(model.vramSizeBytes)} · `
    + `RAM estimada ${formatGiB(model.estimatedRamSizeBytes)} · ${model.processor}`
    + `${model.expiresAt ? ` · expira ${new Date(model.expiresAt).toLocaleTimeString()}` : ""}`;
  row.append(name, details);
  return row;
}

function scheduleRuntimeRefresh() {
  clearTimeout(state.runtimeTimer);

  if (document.hidden || !state.settings) {
    return;
  }

  const seconds = state.requestController
    ? state.settings.runtime.runtimeStatusActiveRefreshSeconds
    : state.settings.runtime.runtimeStatusIdleRefreshSeconds;
  state.runtimeTimer = setTimeout(
    async () => {
      await refreshRuntimeStatus();
      scheduleRuntimeRefresh();
    },
    seconds * 1000
  );
}

function handleVisibilityChange() {
  if (document.hidden) {
    clearTimeout(state.runtimeTimer);
  } else {
    refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  }
}

function renderComposerModels() {
  const selected = elements.modelSelector.value || "auto";
  replaceOptions(
    elements.modelSelector,
    [
      {
        value: "auto",
        label: "Auto"
      },
      ...modelOptions()
    ],
    selected
  );
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(elements.routerModel, modelOptions(), state.settings.routerModel);
  replaceOptions(elements.defaultModel, modelOptions(), state.settings.defaultModel);
  replaceOptions(elements.defaultGpu, gpuOptions(false), state.settings.defaultGpu);
  elements.intentionsGrid.replaceChildren();

  for (const [name, intention] of Object.entries(state.settings.intentions)) {
    elements.intentionsGrid.append(createIntentionCard(name, intention));
  }
}

function modelOptions() {
  return state.models.map(model => ({
    value: model.name,
    label: model.name
  }));
}

function gpuOptions(includeDefault) {
  const options = includeDefault
    ? [
      {
        value: "default",
        label: "Default"
      }
    ]
    : [];

  for (const device of state.devices) {
    options.push({
      value: device.id,
      label: device.isAuto ? "Auto" : device.name
    });
  }

  return options;
}

function createIntentionCard(name, intention) {
  const card = document.createElement("article");
  card.className = "intention-card";
  card.dataset.intention = name;
  const heading = document.createElement("h4");
  heading.textContent = name;
  const selects = document.createElement("div");
  selects.className = "intention-selects";
  selects.append(
    createSelectField(
      "Modelo",
      "intention-model",
      [
        {
          value: "default",
          label: "Default"
        },
        ...modelOptions()
      ],
      intention.model
    ),
    createSelectField(
      "GPU",
      "intention-gpu",
      gpuOptions(true),
      intention.gpu
    )
  );
  const promptField = document.createElement("label");
  const promptLabel = document.createElement("span");
  promptLabel.textContent = "System prompt";
  const prompt = document.createElement("textarea");
  prompt.className = "intention-prompt";
  prompt.required = true;
  prompt.value = intention.systemPrompt;
  promptField.append(promptLabel, prompt);
  card.append(heading, selects, promptField);
  return card;
}

function createSelectField(labelText, className, options, selected) {
  const label = document.createElement("label");
  const text = document.createElement("span");
  text.textContent = labelText;
  const select = document.createElement("select");
  select.className = className;
  replaceOptions(select, options, selected);
  label.append(text, select);
  return label;
}

function replaceOptions(select, options, selected) {
  const normalized = [...options];

  if (selected && !normalized.some(option => option.value === selected)) {
    normalized.push({
      value: selected,
      label: `${selected} (indisponível)`
    });
  }

  select.replaceChildren(
    ...normalized.map(option => {
      const element = document.createElement("option");
      element.value = option.value;
      element.textContent = option.label;
      return element;
    })
  );
  select.value = selected;
}

function openSettings() {
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "";
  renderSettings();
  elements.settingsDialog.showModal();
}

function closeSettings() {
  elements.settingsDialog.close();
}

async function saveSettings(event) {
  event.preventDefault();
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "Salvando…";
  const intentions = {};

  for (const card of elements.intentionsGrid.querySelectorAll(".intention-card")) {
    intentions[card.dataset.intention] = {
      model: card.querySelector(".intention-model").value,
      gpu: card.querySelector(".intention-gpu").value,
      systemPrompt: card.querySelector(".intention-prompt").value
    };
  }

  const nextSettings = {
    schemaVersion: state.settings.schemaVersion,
    ollamaUrl: elements.ollamaUrl.value.trim(),
    routerModel: elements.routerModel.value,
    defaultModel: elements.defaultModel.value,
    defaultGpu: elements.defaultGpu.value,
    intentions,
    runtime: state.settings.runtime
  };

  try {
    state.settings = await fetchJson(
      "/api/settings",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(nextSettings)
      }
    );
    elements.saveStatus.textContent = "Salvo";
    renderSettings();
    elements.settingsDialog.close();
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  } catch (error) {
    const errors = error.payload?.errors;
    elements.settingsErrors.textContent = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsErrors.hidden = false;
    elements.saveStatus.textContent = "";
  }
}

function handleComposerKeyDown(event) {
  if (event.key === "Escape" && state.editingTurn && !state.requestController) {
    event.preventDefault();
    cancelMessageEdit();
    return;
  }

  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    elements.composer.requestSubmit();
  }
}

function handleComposerClick(event) {
  if (event.target.closest("button, select, option, label")) {
    return;
  }

  elements.messageInput.focus();
}

function resizeComposer() {
  elements.messageInput.style.height = "auto";
  elements.messageInput.style.height = `${elements.messageInput.scrollHeight}px`;
}

async function handleComposerSubmit(event) {
  event.preventDefault();

  if (state.requestController) {
    state.requestController.abort();
    return;
  }

  const message = elements.messageInput.value.trim();

  if (!message) {
    return;
  }

  state.autoFollow = true;
  updateJumpControl();
  elements.emptyState?.remove();
  const historyIndex = state.editingTurn?.historyIndex ?? state.history.length;

  if (state.editingTurn) {
    removeConversationFrom(state.editingTurn.element);
    state.history = state.history.slice(
      0,
      historyIndex
    );
    state.editingTurn = null;
    elements.composer.classList.remove("editing");
  }

  appendUserMessage(
    message,
    historyIndex
  );
  const assistant = appendAssistantMessage();
  state.activeAssistant = assistant;
  elements.messageInput.value = "";
  resizeComposer();
  state.requestController = new AbortController();
  setStreamingState(true);
  requestAnimationFrame(scrollToBottom);
  await refreshRuntimeStatus();
  scheduleRuntimeRefresh();

  try {
    const response = await fetch(
      "/api/chat/stream",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          message,
          model: elements.modelSelector.value,
          history: state.history
        }),
        signal: state.requestController.signal
      }
    );

    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    const outcome = await consumeEventStream(response.body, assistant);

    if (outcome.completed) {
      state.history.push(
        {
          role: "user",
          content: message
        },
        {
          role: "assistant",
          content: outcome.answer
        }
      );
    }
  } catch (error) {
    if (error.name === "AbortError") {
      addActivity(
        assistant,
        {
          type: "request.cancelled",
          message: "Solicitação cancelada pelo usuário.",
          elapsedMilliseconds: elapsedSince(assistant)
        },
        false
      );
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Cancelado", false);
    } else {
      addActivity(
        assistant,
        {
          type: "client.error",
          message: error.message,
          elapsedMilliseconds: elapsedSince(assistant)
        },
        true
      );
      assistant.answer.textContent ||= "Não foi possível concluir a resposta.";
      assistant.answer.classList.add("error");
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Falhou", true);
    }
  } finally {
    state.requestController = null;
    state.activeAssistant = null;
    setStreamingState(false);
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();

    if (state.autoFollow) {
      requestAnimationFrame(scrollToBottom);
    }

    elements.messageInput.focus();
  }
}

function appendUserMessage(message, historyIndex) {
  const element = document.createElement("article");
  element.className = "message user";
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;
  const actions = document.createElement("div");
  actions.className = "message-actions";
  const editButton = createMessageActionButton(
    "Editar",
    "Editar mensagem"
  );
  editButton.classList.add("edit-message");
  editButton.addEventListener(
    "click",
    () => startMessageEdit(
      element,
      message,
      historyIndex
    )
  );
  actions.append(editButton);
  element.append(content, actions);
  elements.messages.append(element);
  resizeObserver.observe(element);
}

function appendAssistantMessage() {
  const container = document.createElement("article");
  container.className = "message assistant";

  const details = document.createElement("details");
  details.className = "activity";
  details.open = true;
  const summary = document.createElement("summary");
  summary.textContent = "Em andamento · 0 ms";
  summary.setAttribute("aria-label", "Atividade da solicitação");
  const activityList = document.createElement("div");
  activityList.className = "activity-list";
  details.append(summary, activityList);

  const answer = document.createElement("div");
  answer.className = "assistant-answer pending";
  const actions = document.createElement("div");
  actions.className = "message-actions assistant-actions";
  const copyButton = createMessageActionButton(
    "Copiar",
    "Copiar resposta"
  );
  copyButton.classList.add("copy-message");
  copyButton.disabled = true;
  actions.append(copyButton);
  container.append(details, answer, actions);
  elements.messages.append(container);

  const assistant = {
    container,
    answer,
    details,
    summary,
    activityList,
    startedAt: performance.now(),
    clockFrame: null,
    lastClockUpdate: 0,
    recovered: false,
    rawAnswer: "",
    copyButton
  };
  copyButton.addEventListener(
    "click",
    () => copyText(
      assistant.rawAnswer,
      copyButton,
      "Resposta copiada"
    )
  );
  details.addEventListener(
    "toggle",
    () => {
      if (state.autoFollow) {
        requestAnimationFrame(scrollToBottom);
      }
    }
  );
  resizeObserver.observe(container);
  startElapsedClock(assistant);
  return assistant;
}

function startElapsedClock(assistant) {
  const update = timestamp => {
    if (timestamp - assistant.lastClockUpdate >= 250) {
      assistant.summary.textContent = `Em andamento · ${elapsedSince(assistant)} ms`;
      assistant.lastClockUpdate = timestamp;
    }

    assistant.clockFrame = requestAnimationFrame(update);
  };
  assistant.clockFrame = requestAnimationFrame(update);
}

async function consumeEventStream(stream, assistant) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let answer = "";
  let completed = false;

  while (true) {
    const result = await reader.read();

    if (result.done) {
      break;
    }

    buffer += decoder.decode(result.value, { stream: true });
    const blocks = buffer.split("\n\n");
    buffer = blocks.pop() ?? "";

    for (const block of blocks) {
      const data = block
        .split("\n")
        .filter(line => line.startsWith("data:"))
        .map(line => line.slice(5).trimStart())
        .join("\n");

      if (!data) {
        continue;
      }

      const streamEvent = JSON.parse(data);

      if (streamEvent.type === "response.delta") {
        answer += streamEvent.delta ?? "";
        renderAssistantAnswer(
          assistant,
          streamEvent.renderedHtml ?? "",
          answer
        );
      } else if (streamEvent.type === "response.completed") {
        completed = true;
        assistant.answer.classList.remove("pending");
        renderAssistantAnswer(
          assistant,
          streamEvent.renderedHtml ?? "",
          answer
        );
        addActivity(assistant, streamEvent, false);
        finishActivity(
          assistant,
          `${assistant.recovered ? "Recuperado" : "Concluído"} · `
            + `${streamEvent.elapsedMilliseconds} ms`,
          assistant.recovered
        );
      } else if (streamEvent.type === "error") {
        assistant.answer.classList.remove("pending");
        assistant.answer.classList.add("error");
        assistant.answer.textContent ||= `${streamEvent.error.message}\n`
          + `Referência: ${streamEvent.error.traceId}`;
        addActivity(
          assistant,
          {
            ...streamEvent,
            message:
              `${streamEvent.error.stage}: `
              + `${streamEvent.error.technicalMessage ?? streamEvent.error.message} `
              + `Trace: ${streamEvent.error.traceId}`
          },
          true
        );
        finishActivity(
          assistant,
          `Falhou · ${streamEvent.error.traceId}`,
          true
        );
      } else if (streamEvent.type === "request.cancelled") {
        addActivity(assistant, streamEvent, false);
        assistant.answer.classList.remove("pending");
        finishActivity(assistant, "Cancelado", false);
      } else if (streamEvent.message) {
        if (streamEvent.type === "target-request-recovered") {
          assistant.recovered = true;
        }

        addActivity(
          assistant,
          streamEvent,
          streamEvent.type.includes("failed")
            || streamEvent.type.includes("warning")
            || streamEvent.type === "memory-pressure-detected"
        );
      }

      if (
        streamEvent.type === "memory-pressure-detected"
        || streamEvent.type === "target-request-recovered"
        || streamEvent.type.startsWith("resident-model-")
      ) {
        void refreshRuntimeStatus();
      }
    }
  }

  return {
    answer,
    completed
  };
}

function addActivity(assistant, streamEvent, isWarningOrError) {
  if (!streamEvent.message) {
    return;
  }

  const row = document.createElement("div");
  row.className = `activity-row${isWarningOrError ? " warning" : ""}`;
  row.dataset.eventType = streamEvent.type;
  const time = document.createElement("span");
  time.className = "activity-time";
  time.textContent = `${streamEvent.elapsedMilliseconds ?? 0} ms`;
  const message = document.createElement("span");
  message.className = "activity-message";
  message.textContent = streamEvent.message;
  row.append(time, message);
  assistant.activityList.append(row);
}

function finishActivity(assistant, summary, keepOpen) {
  cancelAnimationFrame(assistant.clockFrame);
  assistant.summary.textContent = summary;
  assistant.details.open = keepOpen;
}

function startMessageEdit(element, message, historyIndex) {
  if (state.requestController) {
    return;
  }

  state.editingTurn = {
    element,
    historyIndex
  };
  elements.composer.classList.add("editing");
  elements.messageInput.value = message;
  resizeComposer();
  setStreamingState(false);
  elements.messageInput.focus();
  elements.messageInput.setSelectionRange(
    message.length,
    message.length
  );
}

function cancelMessageEdit() {
  state.editingTurn = null;
  elements.composer.classList.remove("editing");
  elements.messageInput.value = "";
  resizeComposer();
  setStreamingState(false);
  elements.messageInput.focus();
}

function removeConversationFrom(element) {
  let current = element;

  while (current) {
    const next = current.nextElementSibling;
    resizeObserver.unobserve(current);
    current.remove();
    current = next;
  }
}

function createMessageActionButton(text, accessibleName) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "message-action-button";
  button.textContent = text;
  button.setAttribute(
    "aria-label",
    accessibleName
  );
  button.dataset.originalLabel = accessibleName;
  return button;
}

function renderAssistantAnswer(assistant, renderedHtml, markdown) {
  assistant.rawAnswer = markdown;
  assistant.copyButton.disabled = !markdown;
  assistant.answer.innerHTML = renderedHtml;
  secureRenderedLinks(assistant.answer);
  enhanceCodeBlocks(
    assistant.answer,
    markdown
  );
}

function enhanceCodeBlocks(container, markdown) {
  const fencedLanguages = extractFencedLanguages(markdown);
  const blocks = Array.from(
    container.querySelectorAll("pre")
  );

  blocks.forEach(
    (pre, index) => {
      const parent = pre.parentElement;
      const parentLanguage = parent?.tagName === "DIV"
        ? Array.from(parent.classList).find(name => name !== "code-block")
        : null;
      const codeLanguage = pre.querySelector("code")?.className
        .split(/\s+/)
        .find(name => name.startsWith("language-"))
        ?.slice("language-".length);
      const language = parentLanguage
        ?? codeLanguage
        ?? fencedLanguages[index]
        ?? "code";
      const block = parentLanguage
        ? parent
        : document.createElement("div");

      if (!parentLanguage) {
        pre.replaceWith(block);
        block.append(pre);
      }

      block.classList.add("code-block");
      const header = document.createElement("div");
      header.className = "code-block-header";
      const label = document.createElement("span");
      label.className = "code-language";
      label.textContent = formatLanguageName(language);
      const copyButton = document.createElement("button");
      copyButton.type = "button";
      copyButton.className = "code-copy-button";
      copyButton.setAttribute(
        "aria-label",
        `Copiar código ${label.textContent}`
      );
      copyButton.dataset.originalLabel = copyButton.getAttribute("aria-label");
      const icon = document.createElement("span");
      icon.className = "copy-icon";
      icon.setAttribute(
        "aria-hidden",
        "true"
      );
      copyButton.append(icon);
      copyButton.addEventListener(
        "click",
        () => copyText(
          pre.textContent.replace(/\n$/, ""),
          copyButton,
          "Código copiado"
        )
      );
      header.append(label, copyButton);
      block.prepend(header);
    }
  );
}

function extractFencedLanguages(markdown) {
  return Array.from(
    markdown.matchAll(
      /^```([^\s`]*)/gm
    ),
    match => match[1] || "code"
  );
}

function formatLanguageName(language) {
  const normalized = language.toLowerCase();
  const names = {
    csharp: "C#",
    cs: "C#",
    css: "CSS",
    html: "HTML",
    javascript: "JavaScript",
    js: "JavaScript",
    json: "JSON",
    markdown: "Markdown",
    md: "Markdown",
    powershell: "PowerShell",
    ps1: "PowerShell",
    typescript: "TypeScript",
    ts: "TypeScript",
    xml: "XML"
  };
  return names[normalized]
    ?? language.toUpperCase();
}

async function copyText(text, button, successLabel) {
  if (!text) {
    return;
  }

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      fallbackCopyText(text);
    }

    showCopySuccess(
      button,
      successLabel
    );
  } catch {
    fallbackCopyText(text);
    showCopySuccess(
      button,
      successLabel
    );
  }
}

function fallbackCopyText(text) {
  const temporary = document.createElement("textarea");
  temporary.value = text;
  temporary.style.position = "fixed";
  temporary.style.opacity = "0";
  document.body.append(temporary);
  temporary.select();
  document.execCommand("copy");
  temporary.remove();
}

function showCopySuccess(button, label) {
  const originalLabel = button.dataset.originalLabel;
  button.classList.add("copied");
  button.setAttribute(
    "aria-label",
    label
  );
  clearTimeout(
    Number(button.dataset.resetTimer)
  );
  button.dataset.resetTimer = String(
    setTimeout(
      () => {
        button.classList.remove("copied");
        button.setAttribute(
          "aria-label",
          originalLabel
        );
      },
      1600
    )
  );
}

function secureRenderedLinks(container) {
  for (const link of container.querySelectorAll("a")) {
    link.rel = "noopener noreferrer";
    link.target = "_blank";
  }
}

function handleConversationScroll() {
  state.autoFollow = isNearBottom();
  updateJumpControl();
}

function resumeAutoFollow() {
  state.autoFollow = true;
  updateJumpControl();
  scrollToBottom();
}

function updateJumpControl() {
  elements.jumpLatest.hidden = state.autoFollow;
}

function scrollToBottom() {
  elements.messages.scrollTo(
    {
      top: elements.messages.scrollHeight,
      behavior: "instant"
    }
  );
}

function setStreamingState(isStreaming) {
  elements.sendButton.textContent = isStreaming
    ? "Cancelar"
    : state.editingTurn
      ? "Enviar edição"
      : "Enviar";
  elements.sendButton.setAttribute(
    "aria-label",
    isStreaming
      ? "Cancelar solicitação"
      : state.editingTurn
        ? "Enviar mensagem editada"
        : "Enviar mensagem"
  );
  elements.sendButton.classList.toggle("cancel", isStreaming);
  elements.modelSelector.disabled = isStreaming;
  elements.messages.querySelectorAll(".edit-message").forEach(
    button => {
      button.disabled = isStreaming;
    }
  );
  updateComposerStatus();
}

function updateComposerStatus() {
  if (state.requestController) {
    elements.composerStatus.textContent = "Resposta em andamento";
  } else if (state.editingTurn) {
    elements.composerStatus.textContent = "Editando mensagem · Esc para cancelar";
  } else {
    elements.composerStatus.textContent = "Enter para enviar";
  }
}

function elapsedSince(assistant) {
  return Math.round(performance.now() - assistant.startedAt);
}

function isNearBottom() {
  return elements.messages.scrollHeight
    - elements.messages.scrollTop
    - elements.messages.clientHeight <= 120;
}

function formatGiB(bytes) {
  return bytes == null
    ? "n/d"
    : `${(bytes / 1073741824).toFixed(1)} GB`;
}

function formatPercent(value) {
  return value == null
    ? "n/d"
    : `${Math.round(value)}%`;
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  const payload = await response.json();

  if (!response.ok) {
    const error = new Error(payload.message ?? `HTTP ${response.status}`);
    error.payload = payload;
    throw error;
  }

  return payload;
}

function toCamelCase(value) {
  return value.replace(
    /-([a-z])/g,
    (_, letter) => letter.toUpperCase()
  );
}
