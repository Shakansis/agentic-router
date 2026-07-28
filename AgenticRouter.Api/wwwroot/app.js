const state = {
  models: [],
  devices: [],
  settings: null,
  history: [],
  requestController: null
};

const elements = {};

document.addEventListener("DOMContentLoaded", initialize);

async function initialize() {
  bindElements();
  bindEvents();

  try {
    await loadApplicationState();
  } catch (error) {
    elements.providerBadge.textContent = "Erro";
    elements.providerBadge.className = "badge error";
    elements.providerDetail.textContent = error.message;
  }

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
    "default-gpu"
  ]) {
    elements[toCamelCase(id)] = document.querySelector(`#${id}`);
  }
}

function bindEvents() {
  elements.composer.addEventListener("submit", handleComposerSubmit);
  elements.messageInput.addEventListener("keydown", handleComposerKeyDown);
  elements.messageInput.addEventListener("input", resizeComposer);
  elements.settingsForm.addEventListener("submit", saveSettings);
  document.querySelector("#open-settings").addEventListener("click", openSettings);
  document.querySelector("#close-settings").addEventListener("click", closeSettings);
  document.querySelector("#cancel-settings").addEventListener("click", closeSettings);
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

function renderComposerModels() {
  const selected = elements.modelSelector.value || "auto";
  replaceOptions(
    elements.modelSelector,
    [
      {
        value: "auto",
        label: "Auto"
      },
      ...state.models.map(model => ({
        value: model.name,
        label: model.name
      }))
    ],
    selected
  );
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(
    elements.routerModel,
    modelOptions(),
    state.settings.routerModel
  );
  replaceOptions(
    elements.defaultModel,
    modelOptions(),
    state.settings.defaultModel
  );
  replaceOptions(
    elements.defaultGpu,
    gpuOptions(false),
    state.settings.defaultGpu
  );
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
    intentions
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
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    elements.composer.requestSubmit();
  }
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

  elements.emptyState?.remove();
  appendUserMessage(message);
  const assistant = appendAssistantMessage();
  elements.messageInput.value = "";
  resizeComposer();
  state.requestController = new AbortController();
  setStreamingState(true);

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
        true
      );
      assistant.answer.textContent ||= "Solicitação cancelada.";
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
      assistant.answer.textContent = "Não foi possível concluir a resposta.";
      assistant.answer.classList.add("error");
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Falhou", true);
    }
  } finally {
    state.requestController = null;
    setStreamingState(false);
    elements.messageInput.focus();
  }
}

function appendUserMessage(message) {
  const element = document.createElement("article");
  element.className = "message user";
  element.textContent = message;
  elements.messages.append(element);
}

function appendAssistantMessage() {
  const container = document.createElement("article");
  container.className = "message assistant";

  const answer = document.createElement("div");
  answer.className = "assistant-answer pending";

  const details = document.createElement("details");
  details.className = "activity";
  details.open = true;

  const summary = document.createElement("summary");
  summary.textContent = "Em andamento · 0 ms";
  summary.setAttribute("aria-label", "Atividade da solicitação");

  const activityList = document.createElement("div");
  activityList.className = "activity-list";

  details.append(summary, activityList);
  container.append(answer, details);
  elements.messages.append(container);

  const assistant = {
    container,
    answer,
    details,
    summary,
    activityList,
    startedAt: performance.now(),
    timer: null
  };
  assistant.timer = window.setInterval(
    () => {
      assistant.summary.textContent = `Em andamento · ${elapsedSince(assistant)} ms`;
    },
    100
  );
  return assistant;
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
      const shouldScroll = isNearBottom();

      if (streamEvent.type === "response.delta") {
        answer += streamEvent.delta ?? "";
        assistant.answer.textContent = answer;
      } else if (streamEvent.type === "response.completed") {
        completed = true;
        assistant.answer.classList.remove("pending");
        assistant.answer.innerHTML = streamEvent.renderedHtml ?? "";
        secureRenderedLinks(assistant.answer);
        addActivity(assistant, streamEvent, false);
        finishActivity(
          assistant,
          `Concluído · ${streamEvent.elapsedMilliseconds} ms`,
          false
        );
      } else if (streamEvent.type === "error") {
        assistant.answer.classList.remove("pending");
        assistant.answer.classList.add("error");
        assistant.answer.textContent =
          `${streamEvent.error.message} Referência: ${streamEvent.error.traceId}`;
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
          `Falhou · ${streamEvent.error.stage}`,
          true
        );
      } else if (streamEvent.message) {
        addActivity(
          assistant,
          streamEvent,
          streamEvent.type === "router.warning"
        );
      }

      scrollIfNeeded(shouldScroll);
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
  window.clearInterval(assistant.timer);
  assistant.summary.textContent = summary;
  assistant.details.open = keepOpen;
}

function secureRenderedLinks(container) {
  for (const link of container.querySelectorAll("a")) {
    link.rel = "noopener noreferrer";
    link.target = "_blank";
  }
}

function setStreamingState(isStreaming) {
  elements.sendButton.textContent = isStreaming ? "Cancelar" : "Enviar";
  elements.sendButton.classList.toggle("cancel", isStreaming);
  elements.composerStatus.textContent = isStreaming
    ? "Resposta em andamento"
    : "Enter para enviar";
  elements.modelSelector.disabled = isStreaming;
}

function elapsedSince(assistant) {
  return Math.round(performance.now() - assistant.startedAt);
}

function isNearBottom() {
  return elements.messages.scrollHeight
    - elements.messages.scrollTop
    - elements.messages.clientHeight < 120;
}

function scrollIfNeeded(shouldScroll) {
  if (shouldScroll) {
    elements.messages.scrollTop = elements.messages.scrollHeight;
  }
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
