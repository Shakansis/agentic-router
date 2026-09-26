async function openBenchmarks() {
  elements.runtimeDetails.open = false;
  elements.conversationView.hidden = true;
  elements.benchmarkView.hidden = false;
  elements.closeBenchmarks.focus();
  elements.benchmarkStatus.textContent = "Loading catalog and history…";

  try {
    const [
      catalog,
      history,
      modelsResponse,
      scoringProfile,
      recommendationCatalog,
      liveRuns
    ] = await Promise.all([
      fetchJson("/api/benchmarks/catalog"),
      fetchJson("/api/benchmarks/history?limit=100"),
      fetchJson("/api/models"),
      fetchJson("/api/benchmarks/scoring-profile"),
      fetchJson("/api/benchmarks/recommendation-catalog"),
      fetchJson("/api/benchmarks/suite-runs/live")
    ]);
    state.models = modelsResponse.models;
    const retainedLive = state.benchmark?.live?.terminal
      ? null
      : state.benchmark?.live ?? null;
    const initialResult = retainedLive
      ? state.benchmark?.result ?? null
      : history[0]
        ? await fetchJson(`/api/benchmarks/suite-runs/${encodeURIComponent(history[0].runId)}`)
        : null;
    state.benchmark = {
      catalog,
      history,
      result: initialResult,
      scoringProfile,
      scoringProjection: null,
      scoringUpdateTimer: null,
      historyUpdateTimer: null,
      comparison: null,
      recommendationCatalog,
      recommendation: null,
      live: retainedLive,
      selectedDefaultGpu: state.benchmarkBatch?.request?.defaultGpu
        ?? (state.benchmark?.selectedDefaultGpu !== state.benchmark?.catalog?.defaultGpu
          ? state.benchmark?.selectedDefaultGpu
          : null)
        ?? catalog.defaultGpu
    };
    renderBenchmarkControls();
    renderBenchmarkHistory();
    renderBenchmarkRecommendationControls();
    const storedRunId = sessionStorage.getItem(benchmarkLiveRunStorageKey)
      ?? liveRuns.find(run => !run.terminal)?.runId
      ?? null;
    if (storedRunId) {
      sessionStorage.setItem(benchmarkLiveRunStorageKey, storedRunId);
    }
    if (retainedLive && state.activeBenchmarkRunId) {
      renderBenchmarkLive();
      setBenchmarkRunning(true);
      connectBenchmarkEvents(state.activeBenchmarkRunId, retainedLive.lastSequence);
    } else if (storedRunId) {
      await resumeLiveBenchmark(storedRunId);
    } else if (
      state.benchmarkBatch
      && !state.benchmarkBatch.cancelRequested
      && state.benchmarkBatch.started < state.benchmarkBatch.total
    ) {
      setBenchmarkRunning(true);
      await startNextBenchmarkRun();
    } else {
      clearBenchmarkBatch();
      await rescoreBenchmarkResult();
      await generateGeneralBenchmarkRecommendation();
      if (!state.benchmark?.live?.terminal) {
        elements.benchmarkStatus.textContent = "Ready.";
      }
    }
  } catch (error) {
    elements.benchmarkStatus.textContent = error.message;
  }
}

async function loadProviderBootstrapState({ reportProgress = false } = {}) {
  let setup = await fetchJson("/api/setup/status");
  let modelsResponse = await fetchJson("/api/models");
  let providerHealth = await fetchJson("/api/provider-health");

  if (hasContradictoryOllamaEvidence(setup, modelsResponse, providerHealth)) {
    if (reportProgress) {
      setApplicationLoaderStep(
        "providers",
        "loading",
        "Ollama changed state during startup · verifying the latest status…"
      );
    }
    setup = await fetchJson("/api/setup/status");
    modelsResponse = await fetchJson("/api/models");
    providerHealth = await fetchJson("/api/provider-health");
  }

  return { setup, modelsResponse, providerHealth };
}

function hasContradictoryOllamaEvidence(
  setup,
  modelsResponse,
  providerHealth
) {
  const localModelsAvailable = modelsResponse.models.some(
    model => model.provider === "ollama-local"
  );
  const localHealth = providerHealth.providers.find(
    provider => provider.providerId === "ollama-local"
  );
  const latestEvidenceAvailable = localModelsAvailable
    || localHealth?.connectionState === "healthy";
  const latestEvidenceUnavailable = localHealth?.healthSource === "provider-model-refresh"
    && (localHealth?.connectionState === "unavailable"
      || Boolean(localHealth?.currentDiagnostic));

  return !setup.ollama.available && latestEvidenceAvailable
    || setup.ollama.available && latestEvidenceUnavailable;
}

function closeBenchmarks() {
  hideBenchmarkTooltip();
  elements.benchmarkView.hidden = true;
  elements.conversationView.hidden = false;
  elements.openBenchmarks.focus();
}

function handleBenchmarkTooltipShow(event) {
  const trigger = event.target.closest(".information-button[data-tooltip]");
  if (!trigger || !elements.benchmarkView.contains(trigger)) {
    return;
  }
  if (event.relatedTarget && trigger.contains(event.relatedTarget)) {
    return;
  }
  showBenchmarkTooltip(trigger);
}

function handleBenchmarkTooltipHide(event) {
  const trigger = event.target.closest(".information-button[data-tooltip]");
  if (!trigger || trigger !== benchmarkTooltipTrigger) {
    return;
  }
  if (event.relatedTarget && trigger.contains(event.relatedTarget)) {
    return;
  }
  hideBenchmarkTooltip();
}

function showBenchmarkTooltip(trigger) {
  hideBenchmarkTooltip();
  const tooltip = document.createElement("div");
  tooltip.id = "benchmark-floating-tooltip";
  tooltip.className = "benchmark-floating-tooltip";
  tooltip.role = "tooltip";
  tooltip.textContent = trigger.dataset.tooltip;
  tooltip.style.visibility = "hidden";
  tooltip.style.maxWidth = `${Math.max(180, Math.min(320, window.innerWidth - 24))}px`;
  document.body.append(tooltip);

  const triggerRect = trigger.getBoundingClientRect();
  const tooltipRect = tooltip.getBoundingClientRect();
  const margin = 12;
  const left = Math.min(
    Math.max(margin, triggerRect.left),
    Math.max(margin, window.innerWidth - tooltipRect.width - margin)
  );
  let top = triggerRect.bottom + 8;
  if (top + tooltipRect.height > window.innerHeight - margin) {
    top = triggerRect.top - tooltipRect.height - 8;
  }
  top = Math.max(margin, top);
  tooltip.style.left = `${left}px`;
  tooltip.style.top = `${top}px`;
  tooltip.style.visibility = "visible";
  trigger.setAttribute("aria-describedby", tooltip.id);
  benchmarkTooltip = tooltip;
  benchmarkTooltipTrigger = trigger;
}

function hideBenchmarkTooltip() {
  benchmarkTooltipTrigger?.removeAttribute("aria-describedby");
  benchmarkTooltip?.remove();
  benchmarkTooltip = null;
  benchmarkTooltipTrigger = null;
}

function renderBenchmarkControls() {
  const localModels = modelOptions().filter(option =>
    option.provider === "ollama-local" && !option.disabled
  );
  const selectedModel = localModels.some(option => option.value === state.settings?.defaultModel)
    ? state.settings.defaultModel
    : localModels[0]?.value;
  elements.benchmarkModel.replaceChildren();
  elements.benchmarkModelList.replaceChildren();
  for (const model of localModels) {
    const option = document.createElement("option");
    option.value = model.value;
    option.textContent = model.label;
    option.selected = model.value === selectedModel;
    elements.benchmarkModel.append(option);

    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-model-option";
    const identity = document.createElement("span");
    identity.className = "benchmark-switch-identity";
    const name = document.createElement("strong");
    name.textContent = model.label;
    const detail = document.createElement("small");
    detail.textContent = model.label === model.value
      ? "Ollama Local"
      : model.value;
    identity.append(name, detail);
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-model-toggle";
    input.value = model.value;
    input.checked = model.value === selectedModel;
    input.setAttribute("role", "switch");
    input.setAttribute("aria-label", `Use model ${model.label}`);
    input.addEventListener("change", syncBenchmarkModelSelection);
    label.append(identity, input);
    elements.benchmarkModelList.append(label);
  }

  const catalog = state.benchmark?.catalog;
  const suites = catalog?.suites ?? (catalog?.suite ? [catalog.suite] : []);
  const persistedSelections = state.benchmark?.result?.selectedSuites
    ?? (state.benchmark?.result && state.benchmark.result.suiteId !== "combined"
      ? [{ id: state.benchmark.result.suiteId, version: state.benchmark.result.suiteVersion }]
      : []);
  elements.benchmarkSuiteList.replaceChildren();
  for (const suite of suites) {
    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-test-group-option";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-suite";
    input.value = suite.id;
    input.dataset.version = String(suite.version);
    input.setAttribute("role", "switch");
    input.checked = persistedSelections.length > 0
      ? persistedSelections.some(item => item.id === suite.id && item.version === suite.version)
      : true;
    const text = document.createElement("span");
    text.className = "benchmark-switch-identity";
    const name = document.createElement("strong");
    name.textContent = benchmarkSuiteLabel(suite.id);
    input.setAttribute("aria-label", `Run tests ${name.textContent}`);
    const detail = document.createElement("small");
    detail.textContent = `${suite.tests.length} tests`;
    text.title = `${suite.name}; internal version ${suite.version}.`;
    text.append(name, detail);
    label.append(text, input);
    elements.benchmarkSuiteList.append(label);
  }
  const manualLabel = document.createElement("label");
  manualLabel.className = "benchmark-switch benchmark-test-group-option";
  const manualToggle = document.createElement("input");
  manualToggle.type = "checkbox";
  manualToggle.name = "benchmark-suite";
  manualToggle.value = "manual";
  manualToggle.dataset.version = "1";
  manualToggle.dataset.i18nAriaLabel = "benchmark.custom_prompt.switch_aria";
  manualToggle.setAttribute("role", "switch");
  manualToggle.setAttribute("aria-label", t("benchmark.custom_prompt.switch_aria"));
  manualToggle.checked = persistedSelections.some(item => item.id === "manual");
  const manualIdentity = document.createElement("span");
  manualIdentity.className = "benchmark-switch-identity";
  const manualName = document.createElement("strong");
  manualName.dataset.i18n = "benchmark.custom_prompt.name";
  manualName.textContent = t("benchmark.custom_prompt.name");
  const manualDetail = document.createElement("small");
  manualDetail.dataset.i18n = "benchmark.custom_prompt.switch_detail";
  manualDetail.textContent = t("benchmark.custom_prompt.switch_detail");
  manualIdentity.append(manualName, manualDetail);
  manualLabel.append(manualIdentity, manualToggle);
  elements.benchmarkSuiteList.append(manualLabel);
  elements.benchmarkTimeout.value = String(catalog?.defaultTimeoutSeconds ?? 120);
  elements.benchmarkTimeout.min = String(catalog?.minimumTimeoutSeconds ?? 5);
  elements.benchmarkTimeout.max = String(catalog?.maximumTimeoutSeconds ?? 1600);
  const selectedContext = state.benchmarkBatch?.request?.contextTokens
    ?? catalog?.defaultContextTokens
    ?? 32768;
  elements.benchmarkContextTokens.min = String(catalog?.minimumContextTokens ?? 4096);
  elements.benchmarkContextTokens.max = String(catalog?.maximumContextTokens ?? 131072);
  elements.benchmarkContextTokens.value = String(selectedContext);
  elements.benchmarkContextPresets.replaceChildren(...(catalog?.contextPresets ?? []).map(value => {
    const option = document.createElement("option");
    option.value = String(value);
    return option;
  }));
  const defaultGpu = state.benchmark?.selectedDefaultGpu
    ?? catalog?.defaultGpu
    ?? "Unavailable";
  replaceOptions(
    elements.benchmarkDefaultGpu,
    catalog ? gpuOptions(false, defaultGpu) : [{ value: defaultGpu, label: defaultGpu }],
    defaultGpu
  );
  elements.benchmarkDefaultGpu.title =
    elements.benchmarkDefaultGpu.selectedOptions[0]?.textContent ?? defaultGpu;
  elements.benchmarkHarnessList.replaceChildren();
  for (const status of catalog?.harnesses ?? []) {
    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-harness-option";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-harness";
    input.value = status.definition.id;
    input.disabled = !status.availability.available;
    input.dataset.available = String(status.availability.available);
    input.checked = status.availability.available;
    input.setAttribute("role", "switch");
    const text = document.createElement("span");
    text.className = "benchmark-switch-identity benchmark-harness-identity";
    const harnessLabel = harnessDisplayLabel(status.definition);
    const name = document.createElement("strong");
    name.textContent = harnessLabel;
    input.setAttribute("aria-label", `Use harness ${harnessLabel}`);
    const version = document.createElement("small");
    version.textContent = status.availability.available
      ? status.availability.version ?? "Version not reported"
      : "Unavailable";
    text.append(name, version);
    if (!status.availability.available && status.availability.message) {
      label.title = status.availability.message;
    }
    label.append(text, input);
    elements.benchmarkHarnessList.append(label);
  }
  elements.benchmarkScoringProfileChoice.value = state.benchmark?.scoringProfile?.id === "custom"
    ? "custom"
    : "default";
  replaceOptions(
    elements.benchmarkHistoryHarnessFilter,
    [
      { value: "", label: "All harnesses" },
      ...(catalog?.harnesses ?? []).map(status => ({
        value: status.definition.id,
        label: harnessDisplayLabel(status.definition)
      }))
    ],
    elements.benchmarkHistoryHarnessFilter.value
  );
  replaceOptions(
    elements.benchmarkHistorySuiteFilter,
    [
      { value: "", label: "All suites" },
      ...suites.map(suite => ({
        value: suite.id,
        label: benchmarkSuiteLabel(suite.id)
      })),
      { value: "manual", label: benchmarkSuiteLabel("manual") }
    ],
    elements.benchmarkHistorySuiteFilter.value
  );
  renderBenchmarkScoringProfile();
  updateBenchmarkManualSelection();
  updateBenchmarkSuiteSelection();
  renderBenchmarkSelectionSummary();
}

function isManualBenchmarkSelected() {
  return Boolean(elements.benchmarkSuiteList.querySelector(
    'input[name="benchmark-suite"][value="manual"]:checked'
  ));
}

function updateBenchmarkManualSelection() {
  const manual = isManualBenchmarkSelected();
  elements.benchmarkManualPromptFields.hidden = !manual;
  elements.benchmarkCustomPrompt.required = manual;
  updateBenchmarkSuiteSelection();
  renderBenchmarkSelectionSummary();
}

function selectedBenchmarkModels() {
  return [...elements.benchmarkModelList.querySelectorAll(
    'input[name="benchmark-model-toggle"]:checked:not(:disabled)'
  )].map(input => input.value);
}

function syncBenchmarkModelSelection() {
  const selected = new Set(selectedBenchmarkModels());
  for (const option of elements.benchmarkModel.options) {
    option.selected = selected.has(option.value);
  }
}

function selectedBenchmarkSuites() {
  const selected = [...elements.benchmarkSuiteList.querySelectorAll(
    'input[name="benchmark-suite"]:checked'
  )].map(input => ({ id: input.value, version: Number(input.dataset.version) }));
  const suites = state.benchmark?.catalog?.suites ?? [];
  return selected.map(selection => selection.id === "manual"
    ? {
      id: "manual",
      version: 1,
      tests: [{ id: "MANUAL-CUSTOM-001", timeoutSeconds: 1600, turnBudget: 1 }]
    }
    : suites.find(suite =>
      suite.id === selection.id && suite.version === selection.version
    )).filter(Boolean);
}

function updateBenchmarkSuiteSelection() {
  elements.benchmarkManualPromptFields.hidden = !isManualBenchmarkSelected();
  elements.benchmarkCustomPrompt.required = isManualBenchmarkSelected();
  const suites = selectedBenchmarkSuites();
  elements.benchmarkScoringProfileChoice.disabled = Boolean(state.activeBenchmarkRunId)
    || (suites.length > 0 && suites.every(suite => suite.id === "manual"));
  if (suites.length === 0) {
    elements.runBenchmark.textContent = "Select tests";
    return;
  }
  const scenarioTimeout = Math.max(
    ...suites.flatMap(suite => suite.tests)
      .map(test => Number(test.timeoutSeconds || 120))
  );
  elements.benchmarkTimeout.value = String(Math.min(
    Number(elements.benchmarkTimeout.max || 1600),
    scenarioTimeout
  ));
  elements.runBenchmark.textContent = "Run benchmark";
}

function benchmarkIncludesCustomPrompt(result) {
  return result?.benchmarkMode === "manual"
    || result?.selectedSuites?.some(suite => suite.id === "manual")
    || result?.suiteId === "manual";
}

function benchmarkIsCustomPromptOnly(result) {
  if (!benchmarkIncludesCustomPrompt(result)) return false;
  return result.suiteId === "manual"
    || (result.selectedSuites?.length > 0
      && result.selectedSuites.every(suite => suite.id === "manual"));
}

function benchmarkWeightInputs() {
  return [
    elements.benchmarkWeightObjective,
    elements.benchmarkWeightCorrectness,
    elements.benchmarkWeightTerminality,
    elements.benchmarkWeightWorkspace,
    elements.benchmarkWeightEfficiency
  ];
}

function benchmarkWeightsFromInputs() {
  return {
    objectiveSuccess: Number(elements.benchmarkWeightObjective.value),
    correctness: Number(elements.benchmarkWeightCorrectness.value),
    terminality: Number(elements.benchmarkWeightTerminality.value),
    workspaceAccuracy: Number(elements.benchmarkWeightWorkspace.value),
    efficiency: Number(elements.benchmarkWeightEfficiency.value)
  };
}

function renderBenchmarkScoringProfile() {
  const profile = state.benchmark?.scoringProfile;
  if (!profile) {
    return;
  }
  const weights = profile.weights;
  elements.benchmarkScoreProfile.textContent = `${profile.displayName} v${profile.version}`;
  elements.benchmarkWeightObjective.value = String(weights.objectiveSuccess);
  elements.benchmarkWeightCorrectness.value = String(weights.correctness);
  elements.benchmarkWeightTerminality.value = String(weights.terminality);
  elements.benchmarkWeightWorkspace.value = String(weights.workspaceAccuracy);
  elements.benchmarkWeightEfficiency.value = String(weights.efficiency);
  renderBenchmarkWeightTotal(weights);
}

function renderBenchmarkWeightTotal(weights) {
  const total = Number(weights.objectiveSuccess)
    + Number(weights.correctness)
    + Number(weights.terminality)
    + Number(weights.workspaceAccuracy)
    + Number(weights.efficiency);
  elements.benchmarkWeightTotal.textContent = total <= 0
    ? "Total 0 · invalid configuration"
    : total === 100
      ? "Total 100 · no normalization"
      : `Total ${total} · normalized to 100%`;
  elements.benchmarkWeightTotal.classList.toggle("error", total <= 0);
}

function scheduleBenchmarkScoringUpdate() {
  const weights = benchmarkWeightsFromInputs();
  renderBenchmarkWeightTotal(weights);
  clearTimeout(state.benchmark?.scoringUpdateTimer);
  if (!state.benchmark) {
    return;
  }
  state.benchmark.scoringUpdateTimer = setTimeout(saveBenchmarkScoringProfile, 150);
}

async function saveBenchmarkScoringProfile() {
  const weights = benchmarkWeightsFromInputs();
  try {
    const profile = await fetchJson("/api/benchmarks/scoring-profile", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(weights)
    });
    state.benchmark.scoringProfile = profile;
    if (state.benchmark.recommendationCatalog) {
      state.benchmark.recommendationCatalog.activeScoringProfile = profile;
    }
    state.benchmark.scoringUpdateTimer = null;
    renderBenchmarkScoringProfile();
    renderBenchmarkRecommendationControls();
    await rescoreBenchmarkResult();
    await refreshBenchmarkHistory();
    if (state.benchmark.comparison) {
      await compareBenchmarkRuns();
    }
    elements.benchmarkStatus.textContent = "Custom profile saved; ranking recalculated without running the benchmark.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function resetBenchmarkScoringProfile() {
  clearTimeout(state.benchmark?.scoringUpdateTimer);
  try {
    const profile = await fetchJson("/api/benchmarks/scoring-profile/reset", {
      method: "POST"
    });
    state.benchmark.scoringProfile = profile;
    if (state.benchmark.recommendationCatalog) {
      state.benchmark.recommendationCatalog.activeScoringProfile = profile;
    }
    renderBenchmarkScoringProfile();
    renderBenchmarkRecommendationControls();
    await rescoreBenchmarkResult();
    await refreshBenchmarkHistory();
    if (state.benchmark.comparison) {
      await compareBenchmarkRuns();
    }
    elements.benchmarkStatus.textContent = "Default profile restored; original ranking recalculated.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function rescoreBenchmarkResult() {
  const result = state.benchmark?.result;
  if (!result) {
    if (state.benchmark) {
      state.benchmark.scoringProjection = null;
    }
    renderBenchmarkResult(null);
    return;
  }
  if (benchmarkIsCustomPromptOnly(result)) {
    state.benchmark.scoringProjection = null;
    renderBenchmarkResult(result);
    return;
  }
  state.benchmark.scoringProjection = await fetchJson(
    `/api/benchmarks/suite-runs/${encodeURIComponent(result.runId)}/rescore`,
    { method: "POST" }
  );
  renderBenchmarkResult(result);
}

function renderBenchmarkHistory() {
  const options = [
    { value: "", label: "Select a persisted result" },
    ...(state.benchmark?.history ?? []).map(result => ({
      value: result.runId,
      label: benchmarkHistoryRunLabel(result)
    }))
  ];
  replaceOptions(
    elements.benchmarkHistory,
    options,
    state.benchmark?.result?.runId ?? ""
  );
  const compareOptions = [
    { value: "", label: "Select a run" },
    ...(state.benchmark?.history ?? []).map(result => ({
      value: result.runId,
      label: benchmarkHistoryRunLabel(result)
    }))
  ];
  const previousBaseline = elements.benchmarkCompareBaseline.value;
  const previousCandidate = elements.benchmarkCompareCandidate.value;
  const history = state.benchmark?.history ?? [];
  replaceOptions(
    elements.benchmarkCompareBaseline,
    compareOptions,
    previousBaseline || history[1]?.runId || ""
  );
  replaceOptions(
    elements.benchmarkCompareCandidate,
    compareOptions,
    previousCandidate || history[0]?.runId || ""
  );
  elements.compareBenchmarkRuns.disabled = history.length < 2;
  elements.benchmarkDeleteResult.disabled = Boolean(state.activeBenchmarkRunId)
    || !elements.benchmarkHistory.value;
  elements.benchmarkDeleteAllResults.disabled = Boolean(state.activeBenchmarkRunId)
    || history.length === 0;
}

async function deleteSelectedBenchmarkResult() {
  const runId = elements.benchmarkHistory.value;
  if (!runId || !await showAppConfirm(
    "Delete this saved benchmark result and any retained workspaces that belong to it?",
    { title: "Delete benchmark result", confirmLabel: "Delete result", tone: "danger" }
  )) {
    return;
  }
  try {
    await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}?confirmed=true`,
      { method: "DELETE" }
    );
    if (state.benchmark.result?.runId === runId) {
      state.benchmark.result = null;
      state.benchmark.scoringProjection = null;
      renderBenchmarkResult(null);
    }
    state.benchmark.comparison = null;
    renderBenchmarkComparison(null);
    await refreshBenchmarkHistory();
    elements.benchmarkHistory.value = "";
    renderBenchmarkHistory();
    elements.benchmarkStatus.textContent = "Benchmark result, retained workspaces, and derived recommendations deleted.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function deleteAllBenchmarkResults() {
  if ((state.benchmark?.history?.length ?? 0) === 0 || !await showAppConfirm(
    "Delete all saved benchmark results and their retained workspaces?",
    { title: "Delete all benchmark results", confirmLabel: "Delete all", tone: "danger" }
  )) {
    return;
  }
  try {
    const outcome = await fetchJson(
      "/api/benchmarks/suite-runs?confirmed=true",
      { method: "DELETE" }
    );
    state.benchmark.result = null;
    state.benchmark.scoringProjection = null;
    state.benchmark.comparison = null;
    renderBenchmarkResult(null);
    renderBenchmarkComparison(null);
    await refreshBenchmarkHistory();
    elements.benchmarkStatus.textContent = `${outcome.deleted} benchmark result${outcome.deleted === 1 ? "" : "s"} and ${outcome.recommendationsDeleted} derived recommendation${outcome.recommendationsDeleted === 1 ? "" : "s"} deleted.`;
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function benchmarkHistoryRunLabel(result) {
  const timestamp = new Date(result.startedAt).toLocaleString(window.AgenticRouterI18n.locale, {
    dateStyle: "short",
    timeStyle: "short"
  });
  return `${timestamp} · ${benchmarkSuiteLabel(result.suiteId)} · `
    + `${result.models.length}M × ${result.harnesses.length}H · ${result.finalStatus}`;
}

function benchmarkSuiteLabel(suiteId) {
  if (suiteId === "manual") {
    return t("benchmark.custom_prompt.name");
  }
  if (suiteId === "basic-crud") {
    return "CRUD";
  }
  if (suiteId === "agent-behavior") {
    return "Agent Behavior";
  }
  if (suiteId === "real-life-problem") {
    return "Real Life Problem";
  }
  if (suiteId === "combined") {
    return "Multiple suites";
  }
  return suiteId || "Tests";
}

function scheduleBenchmarkHistoryRefresh() {
  if (!state.benchmark) {
    return;
  }
  clearTimeout(state.benchmark.historyUpdateTimer);
  state.benchmark.historyUpdateTimer = setTimeout(refreshBenchmarkHistory, 180);
}

async function refreshBenchmarkHistory(options = {}) {
  if (!state.benchmark) {
    return;
  }
  clearTimeout(state.benchmark.historyUpdateTimer);
  state.benchmark.historyUpdateTimer = null;
  const query = new URLSearchParams({ limit: "100" });
  const model = elements.benchmarkHistoryModelFilter.value.trim();
  const harness = elements.benchmarkHistoryHarnessFilter.value;
  const suite = elements.benchmarkHistorySuiteFilter.value;
  if (model) {
    query.set("model", model);
  }
  if (harness) {
    query.set("harness", harness);
  }
  if (suite) {
    query.set("suite", suite);
  }
  try {
    state.benchmark.history = await fetchJson(`/api/benchmarks/history?${query}`);
    renderBenchmarkHistory();
    if (options.selectRunId) {
      elements.benchmarkHistory.value = options.selectRunId;
    }
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function compareBenchmarkRuns() {
  const baselineRunId = elements.benchmarkCompareBaseline.value;
  const candidateRunId = elements.benchmarkCompareCandidate.value;
  if (!baselineRunId || !candidateRunId) {
    elements.benchmarkStatus.textContent = "Select two historical runs.";
    return;
  }
  if (baselineRunId === candidateRunId) {
    elements.benchmarkStatus.textContent = "Select different runs to compare.";
    return;
  }
  try {
    const query = new URLSearchParams({ baselineRunId, candidateRunId });
    const comparison = await fetchJson(`/api/benchmarks/comparisons?${query}`);
    state.benchmark.comparison = comparison;
    renderBenchmarkComparison(comparison);
    elements.benchmarkStatus.textContent = "Historical comparison calculated without changing the original evidence.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function renderBenchmarkComparison(comparison) {
  elements.benchmarkComparison.hidden = !comparison;
  elements.benchmarkComparison.replaceChildren();
  if (!comparison) {
    return;
  }
  const heading = document.createElement("div");
  heading.className = "benchmark-comparison-heading";
  const title = document.createElement("strong");
  title.textContent = benchmarkComparabilityLabel(comparison.comparability);
  const score = document.createElement("span");
  score.textContent = `Original ${Number(comparison.baseline.originalScore).toFixed(2)} → ${Number(comparison.candidate.originalScore).toFixed(2)} · `
    + `Current-profile ${Number(comparison.baseline.currentProfileScore).toFixed(2)} → ${Number(comparison.candidate.currentProfileScore).toFixed(2)}`;
  heading.append(title, score);
  elements.benchmarkComparison.append(heading);

  if (comparison.reasons.length > 0) {
    const reasons = document.createElement("ul");
    reasons.className = "benchmark-comparison-reasons";
    for (const reason of comparison.reasons) {
      const item = document.createElement("li");
      item.textContent = reason;
      reasons.append(item);
    }
    elements.benchmarkComparison.append(reasons);
  }

  const deltas = createBenchmarkDataTable(["Metric", "Baseline", "Candidate", "Change"], "Measured changes", "benchmark-comparison-deltas");
  for (const delta of comparison.deltas) {
    const numericDelta = Number(delta.delta);
    appendBenchmarkDataRow(deltas.body, [benchmarkEvidenceLabel(delta.metric), Number(delta.baseline).toFixed(2), Number(delta.candidate).toFixed(2),
      `${numericDelta >= 0 ? "+" : ""}${numericDelta.toFixed(2)} ${delta.unit ?? ""}`]);
  }
  elements.benchmarkComparison.append(deltas.shell);

  if (comparison.comparability !== "comparable") {
    const warning = document.createElement("p");
    warning.className = "benchmark-comparison-warning";
    warning.textContent = "Numeric deltas are evidence only; no regression or improvement was classified because the conditions are not directly comparable.";
    elements.benchmarkComparison.append(warning);
  } else if (comparison.signals.length > 0) {
    const signals = document.createElement("ul");
    signals.className = "benchmark-comparison-signals";
    for (const signal of comparison.signals) {
      const item = document.createElement("li");
      item.dataset.direction = signal.direction;
      const scope = [signal.model, signal.harness, signal.testId].filter(Boolean).join(" × ");
      item.textContent = `${signal.direction}: ${signal.message}${scope ? ` · ${scope}` : ""}`;
      signals.append(item);
    }
    elements.benchmarkComparison.append(signals);
  }

  if (comparison.changedMetadata.length > 0) {
    const metadata = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = `Changed metadata (${comparison.changedMetadata.length})`;
    const list = createBenchmarkDataTable(["Property", "Baseline", "Candidate"], "Changed metadata", "benchmark-comparison-metadata");
    for (const change of comparison.changedMetadata) {
      appendBenchmarkDataRow(list.body, [benchmarkEvidenceLabel(change.field), change.baseline, change.candidate]);
    }
    metadata.append(summary, list.shell);
    elements.benchmarkComparison.append(metadata);
  }
}

function benchmarkComparabilityLabel(value) {
  return {
    comparable: "Comparable",
    "partially-comparable": "Partially comparable",
    "not-directly-comparable": "Not directly comparable"
  }[value] ?? value;
}

function benchmarkAggregateOriginalScore(result) {
  const scores = (result.cells?.length ? result.cells : result.harnessResults ?? [])
    .map(item => Number(item.score));
  return scores.length
    ? scores.reduce((total, value) => total + value, 0) / scores.length
    : 0;
}

function benchmarkAggregateCurrentScore(projection, result) {
  const scores = projection?.matrixCellScores?.length
    ? projection.matrixCellScores.map(item => Number(item.score))
    : projection?.harnessScores?.length
      ? projection.harnessScores.map(item => Number(item.score))
      : [];
  return scores.length
    ? scores.reduce((total, value) => total + value, 0) / scores.length
    : benchmarkAggregateOriginalScore(result);
}

function renderBenchmarkRecommendationControls() {
  const catalog = state.benchmark?.recommendationCatalog;
  if (!catalog) {
    return;
  }
  elements.benchmarkRecommendationVersion.textContent = catalog.algorithmVersion;
  replaceOptions(
    elements.benchmarkRecommendationCategory,
    catalog.categories.map(category => ({
      value: category.id,
      label: category.name
    })),
    elements.benchmarkRecommendationCategory.value || catalog.categories[0]?.id
  );
  const active = catalog.activeScoringProfile;
  replaceOptions(
    elements.benchmarkRecommendationProfile,
    [
      {
        value: "active",
        label: `Active profile · ${active.displayName} v${active.version}`
      },
      { value: "default", label: "Default v1" }
    ],
    elements.benchmarkRecommendationProfile.value || "active"
  );
  elements.researchBenchmarkRecommendation.disabled = !catalog.externalResearchAvailable;
  elements.researchBenchmarkRecommendation.title = catalog.externalResearchAvailable
    ? "Request explicit external research separate from local evidence."
    : "Configure Ollama Web Search to enable optional external research.";
}

async function generateBenchmarkRecommendation(includeExternalEvidence) {
  if (!state.benchmark || state.activeBenchmarkRunId) {
    return;
  }
  elements.generateBenchmarkRecommendation.disabled = true;
  elements.researchBenchmarkRecommendation.disabled = true;
  elements.benchmarkRecommendationStatus.textContent = includeExternalEvidence
    ? "Explicitly researching external sources; local data will not be sent."
    : "Calculating recommendation using persisted local evidence only.";
  try {
    const recommendation = await fetchJson("/api/benchmarks/recommendations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        category: elements.benchmarkRecommendationCategory.value,
        scoringProfile: elements.benchmarkRecommendationProfile.value,
        includeExternalEvidence
      })
    });
    state.benchmark.recommendation = recommendation;
    renderBenchmarkRecommendation(recommendation);
    elements.benchmarkRecommendationStatus.textContent = recommendation.summary;
  } catch (error) {
    elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
  } finally {
    elements.generateBenchmarkRecommendation.disabled = false;
    elements.researchBenchmarkRecommendation.disabled =
      !state.benchmark?.recommendationCatalog?.externalResearchAvailable;
  }
}

async function generateGeneralBenchmarkRecommendation() {
  if (!state.benchmark?.recommendationCatalog) {
    return;
  }
  elements.benchmarkRecommendationCategory.value = "general-coding";
  elements.benchmarkRecommendationProfile.value = "active";
  await generateBenchmarkRecommendation(false);
}

function renderBenchmarkRecommendation(recommendation) {
  preserveBenchmarkDisclosures(elements.benchmarkRecommendationResults, () =>
    renderBenchmarkRecommendationContent(recommendation));
  const best = recommendation?.candidates?.[0];
  elements.benchmarkRecommendationPreviewTitle.textContent = best
    ? `${best.model} × ${benchmarkHarnessLabel(best.harness)}` : "No recommendation loaded.";
  elements.benchmarkRecommendationPreviewSummary.textContent = best
    ? `Score ${Number(best.score).toFixed(2)} / 100 · ${best.confidence} confidence · independent of the current run ranking.`
    : "Independent of the current run ranking.";
}

function preserveBenchmarkDisclosures(container, render) {
  const key = details => details.dataset.disclosureKey
    ?? `${details.className}:${details.querySelector(":scope > summary")?.textContent}`;
  const previous = new Map([...container.querySelectorAll("details")].map(details => [key(details), details.open]));
  const focused = document.activeElement?.closest("details");
  const focusKey = focused && container.contains(focused) && document.activeElement === focused.querySelector(":scope > summary")
    ? key(focused) : null;
  render();
  for (const details of container.querySelectorAll("details")) {
    const id = key(details);
    if (previous.has(id)) details.open = previous.get(id);
    if (id === focusKey) details.querySelector("summary")?.focus({ preventScroll: true });
  }
}

function renderBenchmarkRecommendationContent(recommendation) {
  const container = elements.benchmarkRecommendationResults;
  container.hidden = !recommendation;
  container.replaceChildren();
  if (!recommendation) {
    return;
  }
  const trace = document.createElement("details");
  trace.className = "benchmark-recommendation-trace";
  trace.dataset.disclosureKey = "recommendation-trace";
  const traceSummary = document.createElement("summary");
  traceSummary.textContent = "Recommendation details";
  const traceBody = document.createElement("p");
  traceBody.textContent = `${recommendation.algorithmVersion} · ${recommendation.category} · `
    + `${recommendation.scoringProfile.displayName} v${recommendation.scoringProfile.version} · `
    + `ID ${recommendation.recommendationId.slice(0, 12)}`;
  trace.append(traceSummary, traceBody);
  container.append(trace);

  let alternatives = null;
  let alternativesBody = null;
  for (const candidate of recommendation.candidates) {
    const card = document.createElement("article");
    card.className = "benchmark-recommendation-card";
    const heading = document.createElement("div");
    heading.className = "benchmark-recommendation-card-heading";
    const title = document.createElement("h4");
    title.textContent = `#${candidate.rank} ${candidate.model} × ${benchmarkHarnessLabel(candidate.harness)}`;
    const label = document.createElement("span");
    label.className = "badge";
    label.textContent = candidate.recommendation;
    heading.append(title, label);
    const summary = document.createElement("p");
    summary.className = "benchmark-recommendation-card-summary";
    const score = document.createElement("strong");
    score.textContent = `${Number(candidate.score).toFixed(2)} / 100`;
    const confidence = document.createElement("span");
    confidence.className = "benchmark-recommendation-confidence";
    confidence.textContent = `${candidate.confidence} confidence`;
    const provenance = document.createElement("span");
    provenance.textContent = candidate.evidenceStrength;
    summary.append(score, confidence, provenance);
    const comparability = document.createElement("p");
    comparability.className = "benchmark-recommendation-card-summary";
    comparability.textContent = `${candidate.comparableHistoricalRunCount} comparable historical runs · `
      + `${candidate.partialHistoricalRunCount} partial · ${candidate.incompatibleHistoricalRunCount} incompatible`;
    card.append(heading, summary);
    card.append(
      recommendationList("Strengths", candidate.strengths, "strengths"),
      recommendationList("Limitations", candidate.weaknesses, "weaknesses")
    );
    const evidence = document.createElement("details");
    evidence.dataset.disclosureKey = benchmarkCellKey(candidate.model, candidate.harness);
    const evidenceSummary = document.createElement("summary");
    evidenceSummary.textContent = `Local evidence (${candidate.evidence.length})`;
    const links = document.createElement("div");
    links.className = "benchmark-recommendation-evidence-links";
    for (const item of candidate.evidence) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "benchmark-result-link";
      button.dataset.recommendationRunId = item.runId;
      button.textContent = `${new Date(item.startedAt).toLocaleDateString(window.AgenticRouterI18n.locale)} · ${benchmarkSuiteLabel(item.suiteId)} · `
        + `${item.source} · ${item.comparability} · ${Number(item.categoryScore).toFixed(2)}`;
      links.append(button);
    }
    evidence.append(evidenceSummary, comparability, links);
    card.append(evidence);
    if (candidate.rank === 1) {
      container.append(card);
    } else {
      if (!alternatives) {
        alternatives = document.createElement("details");
        alternatives.className = "benchmark-recommendation-alternatives";
        alternatives.dataset.disclosureKey = "recommendation-alternatives";
        const alternativesSummary = document.createElement("summary");
        alternativesSummary.textContent =
          `Ranked alternatives (${recommendation.candidates.length - 1})`;
        alternativesBody = document.createElement("div");
        alternativesBody.className = "benchmark-recommendation-alternatives-body";
        alternatives.append(alternativesSummary, alternativesBody);
        container.append(alternatives);
      }
      alternativesBody.append(card);
    }
  }

  if (recommendation.candidates.length === 0) {
    const insufficient = document.createElement("p");
    insufficient.className = "benchmark-recommendation-warning";
    insufficient.textContent = "Not enough local evidence. Benchmark these combinations?";
    container.append(insufficient);
  }
  if (recommendation.missingEvidence.length > 0) {
    const missing = document.createElement("details");
    missing.className = "benchmark-recommendation-missing";
    missing.dataset.disclosureKey = "recommendation-missing";
    const summary = document.createElement("summary");
    summary.textContent = `Evidence that would most increase confidence (${recommendation.missingEvidence.length})`;
    const list = document.createElement("ul");
    for (const item of recommendation.missingEvidence) {
      const row = document.createElement("li");
      row.textContent = `${item.model} × ${benchmarkHarnessLabel(item.harness)} · ${item.reason} · suggested suite: ${item.suggestedSuite}`;
      list.append(row);
    }
    missing.append(summary, list);
    container.append(missing);
  }
  if (
    recommendation.externalResearchStatus === "not-requested"
    && recommendation.externalEvidence.length === 0
  ) {
    return;
  }
  const external = document.createElement("section");
  external.className = "benchmark-recommendation-external";
  const externalTitle = document.createElement("h4");
  externalTitle.textContent = "External evidence (separate)";
  const externalStatus = document.createElement("p");
  externalStatus.textContent = `Status: ${recommendation.externalResearchStatus}`;
  external.append(externalTitle, externalStatus);
  if (recommendation.externalEvidence.length > 0) {
    const sources = document.createElement("ul");
    for (const source of recommendation.externalEvidence) {
      const item = document.createElement("li");
      const link = document.createElement("a");
      link.href = source.url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      link.textContent = source.title;
      const status = document.createElement("span");
      status.textContent = ` · ${source.status}`;
      item.append(link, status);
      sources.append(item);
    }
    external.append(sources);
  }
  container.append(external);
}

function recommendationList(title, items, kind) {
  const section = document.createElement("section");
  section.className = `benchmark-recommendation-${kind}`;
  const heading = document.createElement("h5");
  heading.textContent = title;
  const list = document.createElement("ul");
  for (const value of items) {
    const item = document.createElement("li");
    item.textContent = value;
    list.append(item);
  }
  section.append(heading, list);
  return section;
}

async function openBenchmarkRecommendationEvidence(event) {
  const button = event.target.closest("[data-recommendation-run-id]");
  if (!button) {
    return;
  }
  try {
    const runId = button.dataset.recommendationRunId;
    const selected = await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}`
    );
    state.benchmark.result = selected;
    state.benchmark.scoringProjection = null;
    elements.benchmarkHistory.value = runId;
    await rescoreBenchmarkResult();
    showBenchmarkTab("results");
    elements.benchmarkResultDetail.scrollIntoView({ behavior: "smooth", block: "start" });
    elements.benchmarkStatus.textContent = "Supporting local evidence opened.";
  } catch (error) {
    elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
  }
}

function revealBenchmarkInvalidControl(control) {
  const pane = control.closest(".benchmark-tab-pane");
  if (pane?.hidden) showBenchmarkTab(pane.id.replace("benchmark-pane-", ""));
  for (let parent = control.parentElement; parent && parent !== elements.benchmarkView; parent = parent.parentElement) {
    if (parent instanceof HTMLDetailsElement) parent.open = true;
  }
}

async function runBenchmarkSuite(event) {
  event.preventDefault();
  if (state.activeBenchmarkRunId) {
    return;
  }
  if (elements.benchmarkScoringProfileChoice.value === "custom") {
    const invalidWeight = benchmarkWeightInputs().find(input => !input.validity.valid);
    if (invalidWeight) {
      invalidWeight.closest(".benchmark-scoring-advanced").hidden = false;
      revealBenchmarkInvalidControl(invalidWeight);
      invalidWeight.reportValidity();
      return;
    }
  }
  const harnesses = [...elements.benchmarkHarnessList.querySelectorAll(
    'input[name="benchmark-harness"]:checked:not(:disabled)'
  )].map(input => input.value);
  if (harnesses.length === 0) {
    elements.benchmarkStatus.textContent = "Select at least one available harness.";
    return;
  }
  const models = selectedBenchmarkModels();
  if (models.length === 0) {
    elements.benchmarkStatus.textContent = "Select at least one installed local model.";
    return;
  }
  const suites = selectedBenchmarkSuites();
  const manual = suites.some(suite => suite.id === "manual");
  if (suites.length === 0) {
    elements.benchmarkStatus.textContent = "Select at least one test suite.";
    return;
  }
  if (manual && elements.benchmarkCustomPrompt.value.trim() === "") {
    elements.benchmarkStatus.textContent = t("benchmark.custom_prompt.required");
    revealBenchmarkInvalidControl(elements.benchmarkCustomPrompt);
    elements.benchmarkCustomPrompt.focus();
    return;
  }
  const repetitions = Number(elements.benchmarkRepetitions.value);
  if (!Number.isInteger(repetitions) || repetitions < 1 || repetitions > 20) {
    elements.benchmarkStatus.textContent = "Sequential runs must be between 1 and 20.";
    return;
  }
  const contextTokens = Number(elements.benchmarkContextTokens.value);
  const minimumContext = Number(elements.benchmarkContextTokens.min);
  const maximumContext = Number(elements.benchmarkContextTokens.max);
  if (!Number.isInteger(contextTokens)
    || contextTokens < minimumContext
    || contextTokens > maximumContext) {
    elements.benchmarkStatus.textContent =
      `Context window must be between ${minimumContext} and ${maximumContext} tokens.`;
    return;
  }
  state.benchmarkBatch = {
    total: repetitions,
    started: 0,
    completed: 0,
    runIds: [],
    cancelRequested: false,
    request: {
      model: models[0],
      models,
      harnesses,
      suiteId: suites[0].id,
      suiteVersion: suites[0].version,
      suites: suites.map(suite => ({ id: suite.id, version: suite.version })),
      timeoutSeconds: Number(elements.benchmarkTimeout.value),
      contextTokens,
      defaultGpu: elements.benchmarkDefaultGpu.value,
      scoringProfileId: elements.benchmarkScoringProfileChoice.value,
      scoreWeights: elements.benchmarkScoringProfileChoice.value === "custom"
        ? benchmarkWeightsFromInputs()
        : state.benchmark.catalog.scoreWeights,
      benchmarkMode: manual ? "manual" : "predefined",
      customPrompt: manual ? elements.benchmarkCustomPrompt.value : null,
      runName: manual ? elements.benchmarkRunName.value || null : null
    }
  };
  persistBenchmarkBatch();
  setBenchmarkRunning(true);
  showBenchmarkTab("execution");
  elements.benchmarkResultsBody.replaceChildren();
  await startNextBenchmarkRun();
}

async function startNextBenchmarkRun() {
  const batch = state.benchmarkBatch;
  if (
    !batch
    || batch.cancelRequested
    || batch.started >= batch.total
  ) {
    return;
  }

  const clientRunId = globalThis.crypto?.randomUUID?.() ?? createSessionId();
  const iteration = batch.started + 1;
  batch.started = iteration;
  persistBenchmarkBatch();
  state.activeBenchmarkRunId = clientRunId;
  sessionStorage.setItem(benchmarkLiveRunStorageKey, clientRunId);
  initializeBenchmarkLive(
    clientRunId,
    batch.request.models,
    batch.request.harnesses,
    batch.request.suites
  );
  elements.benchmarkStatus.textContent =
    `Starting benchmark loop ${iteration}/${batch.total}…`;
  renderBenchmarkLive();

  try {
    const started = await fetchJson("/api/benchmarks/suite-runs/live", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        ...batch.request,
        modelExecutionPermissionGranted: true,
        clientRunId
      })
    });
    state.activeBenchmarkRunId = started.runId;
    sessionStorage.setItem(benchmarkLiveRunStorageKey, started.runId);
    state.benchmark.live.runId = started.runId;
    elements.benchmarkStatus.textContent =
      `Benchmark loop ${iteration}/${batch.total} running; events connected.`;
    connectBenchmarkEvents(started.runId, 0, started.eventsUrl);
  } catch (error) {
    failBenchmarkLive(benchmarkErrorMessage(error));
  }
}

function initializeBenchmarkLive(
  runId,
  modelIds = selectedBenchmarkModels(),
  harnessIds = [],
  suites = selectedBenchmarkSuites()
) {
  const tests = (suites ?? [{ tests: [{ id: "MANUAL-CUSTOM-001", turnBudget: 1 }] }])
    .flatMap(suite => suite.tests ?? []);
  const cells = {};
  for (const model of modelIds) {
    for (const harness of harnessIds) {
      const key = benchmarkCellKey(model, harness);
      cells[key] = {
        id: key,
        model,
        harness,
        state: "pending",
        completed: 0,
        total: tests.length,
        passed: 0,
        score: null,
        terminality: 0,
        elapsedMilliseconds: 0,
        startedAt: null,
        currentTest: null,
        tests: Object.fromEntries(tests.map(test => [test.id, {
          id: test.id,
          state: "pending",
          currentTurn: 0,
          totalTurns: test.turnBudget ?? 1,
          activities: [],
          checks: {},
          result: null
        }]))
      };
    }
  }
  state.benchmark.live = {
    runId,
    startedAt: new Date().toISOString(),
    lastSequence: 0,
    terminal: false,
    ranking: [],
    models: modelIds,
    harnessIds,
    cells
  };
}

async function resumeLiveBenchmark(runId) {
  try {
    const view = await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}/live`
    );
    initializeBenchmarkLive(runId);
    state.activeBenchmarkRunId = runId;
    setBenchmarkRunning(!view.terminal);
    for (const progressEvent of view.events ?? []) {
      applyBenchmarkProgress(progressEvent, false);
    }
    if (!view.terminal) {
      elements.benchmarkStatus.textContent = view.cancellationRequested
        ? "Cancellation in progress…"
        : "Dashboard reconnected to the running benchmark.";
      connectBenchmarkEvents(runId, view.lastSequence);
      renderBenchmarkLive();
    }
  } catch (error) {
    sessionStorage.removeItem(benchmarkLiveRunStorageKey);
    state.activeBenchmarkRunId = null;
    state.benchmark.live = null;
    clearBenchmarkBatch();
    setBenchmarkRunning(false);
    renderBenchmarkResult(state.benchmark.result);
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function connectBenchmarkEvents(runId, afterSequence = 0, eventsUrl = null) {
  if (state.benchmarkEventSource) {
    state.benchmarkEventSource.close();
  }
  const separator = (eventsUrl ?? "").includes("?") ? "&" : "?";
  const url = `${eventsUrl ?? `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}/events`}`
    + `${separator}after=${Math.max(0, afterSequence)}`;
  const source = new EventSource(url);
  state.benchmarkEventSource = source;
  source.addEventListener("benchmark", event => {
    try {
      applyBenchmarkProgress(JSON.parse(event.data));
    } catch {
      elements.benchmarkStatus.textContent = "Invalid live event; waiting to reconnect.";
    }
  });
  source.onopen = () => {
    if (state.activeBenchmarkRunId) {
      const batch = state.benchmarkBatch;
      elements.benchmarkStatus.textContent = batch
        ? `Benchmark loop ${batch.started}/${batch.total} running; events connected.`
        : "Benchmark running; events connected.";
    }
  };
  source.onerror = () => {
    if (state.activeBenchmarkRunId) {
      elements.benchmarkStatus.textContent = "Connection interrupted; reconnecting without canceling the run…";
    }
  };
  if (!state.benchmarkElapsedTimer) {
    state.benchmarkElapsedTimer = setInterval(() => {
      if (state.benchmark?.live && !state.benchmark.live.terminal) {
        renderBenchmarkLive();
      }
    }, 1000);
  }
}

function benchmarkCellKey(model, harness) {
  return `${model}\u001f${harness}`;
}

function ensureLiveCell(model, harnessId, total = 0) {
  const live = state.benchmark.live;
  const key = benchmarkCellKey(model, harnessId);
  if (!live.cells[key]) {
    live.cells[key] = {
      id: key,
      model,
      harness: harnessId,
      state: "pending",
      completed: 0,
      total,
      passed: 0,
      score: null,
      terminality: 0,
      elapsedMilliseconds: 0,
      startedAt: null,
      currentTest: null,
      tests: {}
    };
  }
  return live.cells[key];
}

function ensureLiveTest(harness, testId) {
  if (!harness.tests[testId]) {
    harness.tests[testId] = {
      id: testId,
      state: "pending",
      currentTurn: 0,
      totalTurns: 1,
      activities: [],
      checks: {},
      result: null
    };
  }
  return harness.tests[testId];
}

function applyBenchmarkProgress(progressEvent, render = true) {
  if (progressEvent.type === "run.snapshot") {
    if (state.benchmark?.live?.runId === progressEvent.runId
      && state.benchmark.live.lastSequence >= progressEvent.sequence) return;
    initializeBenchmarkLive(progressEvent.runId, [], [], []);
    for (const item of progressEvent.snapshotEvents ?? []) {
      applyBenchmarkProgress(item, false);
    }
    if (state.benchmark.live.runId === progressEvent.runId) {
      state.benchmark.live.lastSequence = progressEvent.sequence;
      if (render && !state.benchmark.live.terminal) renderBenchmarkLive();
    }
    return;
  }
  if (!state.benchmark?.live || progressEvent.runId !== state.benchmark.live.runId) {
    initializeBenchmarkLive(
      progressEvent.runId,
      progressEvent.selectedModels ?? (progressEvent.model ? [progressEvent.model] : []),
      progressEvent.selectedHarnesses ?? []
    );
  }
  const live = state.benchmark.live;
  if (progressEvent.sequence <= live.lastSequence) {
    return;
  }
  live.lastSequence = progressEvent.sequence;
  if (progressEvent.type === "run.started") {
    live.startedAt = progressEvent.startedAt ?? progressEvent.timestamp;
    live.models = progressEvent.selectedModels ?? live.models;
    live.harnessIds = progressEvent.selectedHarnesses ?? live.harnessIds;
    for (const model of live.models ?? []) {
      for (const harnessId of live.harnessIds ?? []) {
        const cell = ensureLiveCell(model, harnessId, progressEvent.totalTests);
        for (const test of progressEvent.tests ?? []) {
          ensureLiveTest(cell, test.id);
        }
      }
    }
  }
  const model = progressEvent.model ?? live.models?.[0] ?? state.benchmark?.result?.model ?? "model";
  const harness = progressEvent.harness
    ? ensureLiveCell(model, progressEvent.harness, progressEvent.totalTests)
    : null;
  const test = harness && progressEvent.testId
    ? ensureLiveTest(harness, progressEvent.testId)
    : null;
  if (progressEvent.type === "harness.started" && harness) {
    harness.state = "running";
    harness.startedAt = progressEvent.startedAt ?? progressEvent.timestamp;
  } else if (progressEvent.type === "harness.progress" && harness) {
    Object.assign(harness, {
      state: progressEvent.state,
      completed: progressEvent.completedTests,
      total: progressEvent.totalTests,
      passed: progressEvent.passedTests,
      score: progressEvent.provisionalScore,
      terminality: progressEvent.terminality,
      elapsedMilliseconds: progressEvent.elapsedMilliseconds
    });
  } else if (progressEvent.type === "harness.completed" && harness) {
    Object.assign(harness, {
      state: progressEvent.state,
      completed: progressEvent.completedTests,
      total: progressEvent.totalTests,
      passed: progressEvent.passedTests,
      score: progressEvent.provisionalScore,
      terminality: progressEvent.terminality,
      elapsedMilliseconds: progressEvent.elapsedMilliseconds,
      currentTest: null
    });
  } else if (progressEvent.type === "test.state" && test) {
    test.state = progressEvent.state;
    test.result = progressEvent.testResult ?? test.result;
    if (["running", "harness-completed", "validating"].includes(progressEvent.state)) {
      harness.currentTest = progressEvent.testId;
    }
  } else if (progressEvent.type === "activity" && test) {
    if (progressEvent.turnNumber) {
      test.currentTurn = progressEvent.turnNumber;
      test.totalTurns = progressEvent.totalTurns || test.totalTurns;
    }
    test.activities.push({
      kind: progressEvent.activityKind ?? "activity",
      message: progressEvent.message ?? "Activity recorded.",
      timestamp: progressEvent.timestamp,
      turnNumber: progressEvent.turnNumber || 0,
      totalTurns: progressEvent.totalTurns || 0
    });
    test.activities = test.activities.slice(-8);
  } else if (progressEvent.type === "validation" && test) {
    test.checks = progressEvent.validationChecks ?? {};
  } else if (progressEvent.type === "ranking.provisional") {
    live.ranking = progressEvent.ranking ?? [];
  } else if (progressEvent.type === "run.cancelling") {
    for (const item of Object.values(live.cells)) {
      if (!["completed", "cancelled"].includes(item.state)) {
        item.state = "cancelling";
      }
    }
    elements.benchmarkStatus.textContent = "Cancellation in progress…";
  } else if (progressEvent.type === "run.completed" && progressEvent.finalResult) {
    finishBenchmarkLive(progressEvent.finalResult);
    return;
  } else if (progressEvent.type === "run.failed") {
    failBenchmarkLive(progressEvent.error?.message ?? progressEvent.message);
    return;
  }
  if (render) {
    renderBenchmarkLive();
  }
}

function showBenchmarkTab(name, focus = false) {
  const tabs = [...elements.benchmarkView.querySelectorAll("[data-benchmark-tab]")];
  if (!tabs.some(tab => tab.dataset.benchmarkTab === name)) return;
  state.benchmarkUi.tab = name;
  for (const tab of tabs) {
    const active = tab.dataset.benchmarkTab === name;
    tab.setAttribute("aria-selected", String(active));
    tab.tabIndex = active ? 0 : -1;
    document.getElementById(tab.getAttribute("aria-controls")).hidden = !active;
    if (active && focus) tab.focus();
  }
}

function handleBenchmarkTabKeyDown(event) {
  const tabs = [...elements.benchmarkView.querySelectorAll("[data-benchmark-tab]")];
  const index = tabs.indexOf(event.currentTarget);
  const next = { ArrowRight: (index + 1) % tabs.length, ArrowLeft: (index + tabs.length - 1) % tabs.length, Home: 0, End: tabs.length - 1 }[event.key];
  if (next !== undefined) {
    event.preventDefault();
    showBenchmarkTab(tabs[next].dataset.benchmarkTab, true);
  }
}

function renderBenchmarkSelectionSummary() {
  const models = elements.benchmarkModelList.querySelectorAll('input:checked').length;
  const harnesses = elements.benchmarkHarnessList.querySelectorAll('input:checked[data-available="true"]').length;
  const manual = isManualBenchmarkSelected();
  const tests = new Set(selectedBenchmarkSuites().flatMap(suite => suite.tests.map(test => test.id))).size;
  const repeats = Number(elements.benchmarkRepetitions.value);
  const contextTokens = Number(elements.benchmarkContextTokens.value);
  const gpu = elements.benchmarkDefaultGpu.value || "Unavailable";
  elements.benchmarkModelsCount.textContent = `${models} selected`;
  elements.benchmarkHarnessesCount.textContent = `${harnesses} selected`;
  elements.benchmarkTestsCount.textContent = `${tests} tests`;
  elements.benchmarkSelectionSummary.textContent = `${models * harnesses} combinations × ${tests} tests`;
  elements.benchmarkSelectionTotal.textContent = Number.isInteger(repeats) && repeats >= 1 && repeats <= 20
    ? `${repeats} repetition(s) · ${(models * harnesses * tests * repeats).toLocaleString("en-US")} planned tests · ${Number.isInteger(contextTokens) ? contextTokens.toLocaleString("en-US") : "invalid"} ctx · ${gpu}`
    : "Choose 1–20 sequential runs.";
}

function isBenchmarkCellActive(cell) {
  return ["running", "validating", "harness-completed"].includes(cell.state);
}

function benchmarkCellGroup(cell) {
  if (isBenchmarkCellActive(cell) || cell.state === "cancelling") return "Active";
  return cell.state === "pending" ? "Queued" : "Finished";
}

function benchmarkTestLabel(id) {
  return {
    "FS-CREATE-001": "Create a file", "FS-READ-001": "Read a file",
    "FS-UPDATE-001": "Update a file", "FS-DELETE-001": "Delete a file",
    "CONTINUITY-001": "Keep context across turns", "SCOPE-RETENTION-001": "Stay within task scope",
    "RECOVERY-001": "Recover from a failure", "CONVERGENCE-001": "Converge on a solution",
    "TERMINALITY-001": "Finish the task clearly", "STALE-CONFLICT-001": "Handle conflicting changes",
    "TRUTHFUL-REPORT-001": "Report the outcome accurately", "MISSING-GAME-001": "Complete a browser game collection",
    "MANUAL-CUSTOM-001": t("benchmark.custom_prompt.name")
  }[id] ?? id;
}

function benchmarkStateLabel(value) {
  return { pending: "Queued", running: "Running", validating: "Validating", "harness-completed": "Validating outcome", passed: "Passed", failed: "Failed", "timed-out": "Timed out", completed: "Completed", cancelling: "Cancelling", cancelled: "Cancelled", unavailable: "Unavailable", unsupported: "Unsupported" }[value] ?? value;
}

function benchmarkReviewStatusLabel(value) {
  return {
    "awaiting-user-review": t("benchmark.custom_prompt.status.awaiting"),
    reviewed: t("benchmark.custom_prompt.status.reviewed"),
    "technical-failure": t("benchmark.custom_prompt.status.technical_failure"),
    "not-applicable": t("benchmark.custom_prompt.status.not_applicable")
  }[value] ?? value ?? t("benchmark.custom_prompt.status.awaiting");
}

function updateBenchmarkText(element, text) {
  if (element.textContent !== text) element.textContent = text;
}

function stepBenchmarkCombination(offset) {
  const cells = Object.values(state.benchmark?.live?.cells ?? {});
  const selectedIndex = cells.findIndex(cell => cell.id === state.benchmarkUi.selectedCell);
  const target = cells[selectedIndex + offset];
  if (target) {
    state.benchmarkUi.selectedCell = target.id;
    renderBenchmarkLive();
  }
}

function followCurrentBenchmarkCombination() {
  const current = Object.values(state.benchmark?.live?.cells ?? {}).find(isBenchmarkCellActive);
  if (current) {
    state.benchmarkUi.selectedCell = current.id;
    renderBenchmarkLive();
    showBenchmarkTab("execution", true);
  }
}

function renderBenchmarkLive() {
  const live = state.benchmark?.live;
  if (!live) return;
  const ui = state.benchmarkUi;
  const cells = Object.values(live.cells);
  const current = cells.find(isBenchmarkCellActive);
  const completed = cells.filter(cell => benchmarkCellGroup(cell) === "Finished").length;
  const active = cells.filter(cell => benchmarkCellGroup(cell) === "Active").length;
  const queued = cells.filter(cell => cell.state === "pending").length;
  const batch = state.benchmarkBatch;
  elements.benchmarkLiveDashboard.hidden = cells.length === 0;
  elements.benchmarkCombinationPicker.hidden = cells.length === 0;
  elements.benchmarkExecutionEmpty.hidden = cells.length > 0;
  elements.benchmarkRankingNote.hidden = live.terminal;
  elements.benchmarkFollowActive.hidden = !current || live.terminal;
  updateBenchmarkText(elements.benchmarkProgressTitle, live.failure ? "Benchmark failed" : live.terminal ? "Run finished" : batch ? `Repetition ${batch.started} of ${batch.total}` : "Benchmark in progress");
  if (live.failure) {
    updateBenchmarkText(elements.benchmarkRunSummary, live.failure);
  } else if (!live.terminal) {
    updateBenchmarkText(elements.benchmarkRunSummary, `${completed}/${cells.length} combinations finished · ${active} active · ${queued} queued · sequential local run`);
  }
  updateBenchmarkText(elements.benchmarkQueueSummary, `${cells.length} combinations · ${active} active · ${completed} finished · ${queued} queued`);
  updateBenchmarkText(elements.benchmarkCurrentCombination, current && !live.terminal
    ? `${current.model} × ${benchmarkHarnessLabel(current.harness)}${current.currentTest ? ` · ${benchmarkTestLabel(current.currentTest)}` : ""}` : "No active combination.");
  elements.benchmarkBatchProgress.hidden = !batch || batch.total < 2;
  if (batch) {
    const progress = elements.benchmarkBatchProgress;
    while (progress.children.length < batch.total) progress.append(document.createElement("span"));
    while (progress.children.length > batch.total) progress.lastChild.remove();
    [...progress.children].forEach((bar, i) => { bar.dataset.state = i < batch.completed ? "completed" : i < batch.started ? "running" : "pending"; });
    progress.setAttribute("role", "img");
    progress.setAttribute("aria-label", `${batch.completed} of ${batch.total} repetitions completed`);
  }
  const keys = new Set(cells.map(cell => cell.id));
  for (const [key, option] of ui.liveOptions) {
    if (!keys.has(key)) { option.remove(); ui.liveOptions.delete(key); ui.liveCards.delete(key); }
  }
  const select = elements.benchmarkCombinationSelect;
  for (const groupName of ["Active", "Finished", "Queued"]) {
    let group = [...select.children].find(item => item.dataset.group === groupName);
    if (!group) { group = document.createElement("optgroup"); group.dataset.group = groupName; select.append(group); }
    const members = cells.filter(cell => benchmarkCellGroup(cell) === groupName);
    group.label = `${groupName} (${members.length})`;
    group.disabled = members.length === 0;
    for (const cell of members) {
      let option = ui.liveOptions.get(cell.id);
      if (!option) { option = document.createElement("option"); option.value = cell.id; ui.liveOptions.set(cell.id, option); }
      updateBenchmarkText(option, `${cell.model} × ${benchmarkHarnessLabel(cell.harness)} — ${benchmarkStateLabel(cell.state)} · ${cell.completed}/${cell.total} finished · ${cell.passed} passed`);
      if (option.parentElement !== group) group.append(option);
    }
  }
  if (!keys.has(ui.selectedCell)) ui.selectedCell = current?.id ?? cells[0]?.id ?? null;
  select.value = ui.selectedCell ?? "";
  const selectedIndex = cells.findIndex(cell => cell.id === ui.selectedCell);
  elements.benchmarkPreviousCombination.disabled = selectedIndex <= 0;
  elements.benchmarkCurrentCombinationButton.disabled = !current || current.id === ui.selectedCell;
  elements.benchmarkNextCombination.disabled = selectedIndex < 0 || selectedIndex >= cells.length - 1;
  updateBenchmarkText(elements.benchmarkCombinationPosition, cells.length ? `${selectedIndex + 1} of ${cells.length} combinations` : "No combinations");
  const selected = live.cells[ui.selectedCell];
  if (selected) renderBenchmarkLiveCell(selected);
  if (!live.terminal) {
    renderProvisionalBenchmarkRanking(live.ranking);
    elements.benchmarkScoreContext.textContent = "Provisional scores use the active profile. Finished tests include failures; terminality is a separate metric.";
  }
}

// Describe recorded acceptance failures, never infer absent actions from absent traces.
function benchmarkFailureSummary(testId, raw, phase = "") {
  if (testId === "MANUAL-CUSTOM-001" && (raw || phase === "passed")) return "Custom prompt: review the output and assign a quality score.";
  const status = String(raw?.status ?? "").toLowerCase();
  if (status === "pass" || phase === "passed") return "";
  if (!raw) {
    return ({ pending: "Waiting for this test to start.", running: "Test in progress.",
      "harness-completed": "Execution finished; waiting for Host checks.", validating: "Checking the result.",
      cancelled: "Test cancelled before a complete result was available.",
      "timed-out": "The test ran out of time before completion.",
      failed: "The test did not complete successfully. Open Advanced details for the recorded evidence." })[phase]
      ?? "Waiting for the test result.";
  }
  const messages = [];
  const facts = raw.validationFacts ?? {};
  const isFalse = key => String(facts[key]).toLowerCase() === "false";
  const isTrue = key => String(facts[key]).toLowerCase() === "true";
  const count = key => facts[key] === undefined || facts[key] === "" ? null : Number(facts[key]);
  const execution = raw.executionStatus;
  if (execution === "timed-out") messages.push("The test ran out of time before completion.");
  else if (execution === "cancelled") messages.push("The test was cancelled before completion.");
  else if (execution === "unavailable") messages.push("The selected execution runtime was unavailable.");
  else if (execution === "failed") messages.push("Execution ended with an error before the test could finish successfully.");
  if (raw.objectiveAchieved === false && !(testId === "MISSING-GAME-001" && facts.browserValidation === "unavailable")) messages.push(({
    "FS-CREATE-001": "The expected file, location or exact content was not confirmed.",
    "FS-READ-001": "The required facts from both source files were not confirmed.",
    "FS-UPDATE-001": "The requested edit did not match the expected file content.",
    "FS-DELETE-001": "Deletion of the requested file was not confirmed.",
    "CONTINUITY-001": "The final file did not preserve all requirements across the three turns.",
    "SCOPE-RETENTION-001": "The target file did not match the requested narrow edit.",
    "RECOVERY-001": "The recovery output did not match the expected content.",
    "CONVERGENCE-001": "The edited file did not match the expected final content.",
    "TERMINALITY-001": "The required file change was not confirmed.",
    "STALE-CONFLICT-001": "The final file did not preserve the external change and requested edit.",
    "TRUTHFUL-REPORT-001": "The required file change was not confirmed.",
    "MISSING-GAME-001": "The generated game did not satisfy all workspace and browser checks."
  })[testId] ?? "The expected outcome was not confirmed.");
  if (isFalse("scopeAccurate")) messages.push("The file changes did not match the expected scope; changes were missing or unexpected.");
  else if ((raw.unexpectedFiles ?? []).length) messages.push("The Host recorded unexpected or missing file changes.");
  if (testId === "CONTINUITY-001" && count("terminalTurns") !== null && count("terminalTurns") !== 3)
    messages.push("Not all three turns completed successfully.");
  if (testId === "SCOPE-RETENTION-001" && isTrue("targetRecreated"))
    messages.push("The trace records recreation of a file that should only have been edited.");
  if (testId === "RECOVERY-001") {
    if (count("staleReadAttempts") !== null && count("staleReadAttempts") !== 1)
      messages.push("The Host could not confirm exactly one read of the stale path.");
    if (isFalse("correctReadObserved")) messages.push("The Host could not confirm the recovery read from the correct path.");
    if (count("surfacedErrors") === 0) messages.push("The expected tool failure was not recorded.");
    if (count("recoveredErrors") === 0) messages.push("Recovery after the tool failure was not recorded.");
  }
  if (testId === "CONVERGENCE-001") {
    if (isTrue("toolTraceAvailable") && count("postMutationValidationReads") !== null && count("postMutationValidationReads") !== 1)
      messages.push("The Host could not confirm exactly one validation read after the edit.");
    if (isTrue("toolTraceAvailable") && count("toolCallsAfterSuccess") > 0)
      messages.push("Additional tool calls were recorded after validation; the test required stopping there.");
    if (isFalse("finalReportMatched")) messages.push("The final report did not include the required validation result.");
  }
  if (testId === "TERMINALITY-001" && isFalse("truthfulReport"))
    messages.push("The final report did not confirm the required change and disclose that optional validation was unavailable.");
  if (testId === "STALE-CONFLICT-001" && isFalse("externalMutationRecorded"))
    messages.push("The Host's external file change was not recorded, so conflict preservation could not be confirmed.");
  if (testId === "TRUTHFUL-REPORT-001") {
    if (facts.narrationClassification && facts.narrationClassification !== "accurate")
      messages.push("The final report was incomplete or inconsistent with the observed result.");
    if (count("optionalReadAttempts") !== null && count("optionalReadAttempts") !== 1)
      messages.push("The Host could not confirm exactly one attempt at the optional read.");
    if (isFalse("optionalFailureObserved")) messages.push("The expected optional-check failure was not recorded.");
  }
  if (testId === "MISSING-GAME-001") {
    if (isFalse("existingFilesUnchanged")) messages.push("Existing files were modified or deleted.");
    if (facts.browserValidation === "unavailable")
      messages.push(raw.error?.code === "browser-validation-unavailable"
        ? raw.error.message : "Browser validation is unavailable; the scenario could not be scored.");
    else if (facts.browserValidation && facts.browserValidation !== "passed")
      messages.push("Browser validation did not pass or could not run; inspect its recorded result.");
  }
  return messages.join(" ") || "The Host could not confirm all acceptance criteria. Open Advanced details for the recorded evidence.";
}

function createBenchmarkAdvanced(key) {
  const advanced = document.createElement("details");
  advanced.className = "benchmark-test-advanced";
  advanced.dataset.disclosureKey = `${key}:advanced`;
  const summary = document.createElement("summary");
  const label = document.createElement("span");
  label.textContent = "Advanced details";
  const help = document.createElement("a");
  help.href = "/benchmark-help.html#evidence";
  help.target = "_blank";
  help.rel = "noopener";
  help.textContent = "How to read this evidence ↗";
  help.addEventListener("click", event => event.stopPropagation());
  summary.append(label, help);
  const content = document.createElement("div");
  content.className = "benchmark-advanced-content";
  advanced.append(summary, content);
  return advanced;
}

function benchmarkEvidenceLabel(name) {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, letter => letter.toUpperCase());
}

function benchmarkStatusTone(value) {
  return ({ pass: "success", passed: "success", fail: "danger", failed: "danger", error: "danger",
    "completed-with-failures": "warning", "timed-out": "warning", cancelled: "warning", partial: "warning",
    running: "active", validating: "active", "harness-completed": "active" })[String(value).toLowerCase()] ?? "neutral";
}

function createBenchmarkStatus(label, value) {
  const badge = document.createElement("span");
  badge.className = "benchmark-status-badge";
  badge.dataset.tone = benchmarkStatusTone(value);
  badge.textContent = label;
  return badge;
}

function appendBenchmarkTestIdentity(summary, testId) {
  const identity = document.createElement("span");
  identity.className = "benchmark-test-identity";
  const name = document.createElement("strong");
  name.textContent = benchmarkTestLabel(testId);
  const code = document.createElement("small");
  code.textContent = testId;
  identity.append(name, code);
  summary.append(identity);
}

function createBenchmarkTimelineItem(kind, message, tone = "neutral") {
  const item = document.createElement("li");
  item.dataset.tone = tone;
  const tag = document.createElement("span");
  tag.className = "benchmark-log-tag";
  tag.textContent = kind;
  const text = document.createElement("span");
  text.textContent = message;
  item.append(tag, text);
  return item;
}

function createBenchmarkDataTable(headers, caption, className = "") {
  const shell = document.createElement("div");
  shell.className = `benchmark-data-table-shell ${className}`;
  // Keep wide evidence keyboard-scrollable without widening the page.
  shell.tabIndex = 0;
  shell.setAttribute("role", "region");
  shell.setAttribute("aria-label", caption);
  const table = document.createElement("table");
  table.className = "benchmark-data-table";
  const title = document.createElement("caption");
  title.textContent = caption;
  const head = document.createElement("thead");
  const row = document.createElement("tr");
  for (const label of headers) {
    const cell = document.createElement("th"); cell.scope = "col"; cell.textContent = label; row.append(cell);
  }
  head.append(row);
  const body = document.createElement("tbody");
  table.append(title, head, body);
  shell.append(table);
  return { shell, body };
}

function appendBenchmarkDataRow(body, values) {
  const row = document.createElement("tr");
  values.forEach((value, index) => {
    const cell = document.createElement(index === 0 ? "th" : "td");
    if (index === 0) cell.scope = "row";
    if (value instanceof Node) cell.append(value);
    else cell.textContent = value === null || value === undefined ? "Not observed" : String(value);
    row.append(cell);
  });
  body.append(row);
}

function createBenchmarkEvidenceValue(label, value) {
  const content = document.createElement("span");
  content.className = "benchmark-evidence-value";
  const text = document.createElement("span");
  text.textContent = value === null || value === undefined ? "Not observed" : String(value);
  content.append(text);
  if (/sha256|fingerprint/i.test(label) && value && value !== "missing" && value !== "Unavailable") {
    content.classList.add("benchmark-hash-value");
    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "benchmark-copy-value";
    copy.textContent = "Copy";
    copy.dataset.originalLabel = `Copy ${label}`;
    copy.setAttribute("aria-label", copy.dataset.originalLabel);
    copy.addEventListener("click", () => copyText(String(value), copy, "Copied"));
    content.append(copy);
  }
  return content;
}

function createBenchmarkAcceptanceTable(raw, checks = {}) {
  const facts = raw?.validationFacts ?? checks;
  const rows = [];
  const factKeys = new Set();
  const add = (label, expected, observed, comparable = true) => {
    const missing = observed === null || observed === undefined || observed === "missing";
    const equal = !missing && String(expected) === String(observed);
    const status = createBenchmarkStatus(missing ? "Not observed" : !comparable ? "Recorded" : equal ? "Match" : "Different",
      missing || !comparable ? "unavailable" : equal ? "pass" : "fail");
    rows.push([label, createBenchmarkEvidenceValue(`Expected ${label}`, expected), createBenchmarkEvidenceValue(`Observed ${label}`, observed), status]);
  };
  // Pair only expectations and observations that exist in the saved evidence.
  for (const [label, expectedKey, observedKey] of [
    ["Byte length", "expectedByteLength", "actualByteLength"],
    ["SHA256", "expectedSha256", "actualSha256"],
    ["Changed files", "expectedChangedFiles", "actualChangedFiles"],
    ["Final state", "expectedFinalState", "actualFinalState"]
  ]) {
    if (facts[expectedKey] !== undefined) {
      add(label, facts[expectedKey], facts[observedKey]);
      factKeys.add(expectedKey);
      factKeys.add(observedKey);
    }
  }
  if (facts.expectedPath !== undefined) {
    // A change list is not a separately measured output-path field.
    add("Path / changed files", facts.expectedPath, raw?.changedFiles?.length ? raw.changedFiles.join("\n") : null, false);
    factKeys.add("expectedPath");
  }
  const validation = raw?.hostValidationResult ?? checks["Host validation"];
  if (validation && !["manual-review", "not-applicable"].includes(validation)) {
    add("Host validation", "pass", validation);
    factKeys.add("Host validation");
  }
  const workspace = raw?.containmentAccuracy !== null && raw?.containmentAccuracy !== undefined
    ? `${raw.containmentAccuracy}%` : checks["Workspace containment"];
  if (workspace !== undefined) {
    add("Workspace accuracy", raw ? "100%" : "PASS", workspace);
    factKeys.add("Workspace containment");
  }
  if (!rows.length) return null;
  const { shell, body } = createBenchmarkDataTable(["Property", "Expected", "Observed", "Comparison"], "Expected and observed", "benchmark-acceptance-table");
  rows.forEach(row => appendBenchmarkDataRow(body, row));
  return { shell, factKeys };
}

function renderBenchmarkLiveCell(cell) {
  const cards = state.benchmarkUi.liveCards;
  let view = cards.get(cell.id);
  if (!view) {
    const card = document.createElement("article"); card.className = "benchmark-live-harness";
    const heading = document.createElement("h4");
    const summary = document.createElement("p"); summary.className = "benchmark-live-summary";
    const counts = document.createElement("p"); counts.className = "benchmark-live-counts";
    const progress = document.createElement("div"); progress.className = "benchmark-live-progress";
    const current = document.createElement("p"); current.className = "benchmark-live-current";
    const tests = document.createElement("div");
    card.append(heading, summary, counts, progress, current, tests);
    view = { card, heading, summary, counts, progress, current, tests, testViews: new Map() };
    cards.set(cell.id, view);
  }
  if (elements.benchmarkLiveDashboard.firstElementChild !== view.card) elements.benchmarkLiveDashboard.replaceChildren(view.card);
  view.card.dataset.state = cell.state;
  const elapsed = cell.startedAt && isBenchmarkCellActive(cell) && !state.benchmark.live.terminal
    ? Date.now() - new Date(cell.startedAt).getTime() : cell.elapsedMilliseconds;
  updateBenchmarkText(view.heading, `${cell.model} × ${benchmarkHarnessLabel(cell.harness)}`);
  updateBenchmarkText(view.summary, `${benchmarkStateLabel(cell.state)} · ${cell.completed}/${cell.total} finished · ${cell.passed} passed · score* ${cell.score == null ? "—" : Number(cell.score).toFixed(2)} · terminality ${cell.state === "pending" ? "—" : `${cell.terminality}%`} · ${cell.state === "pending" ? "Not started" : formatBenchmarkDuration(Math.max(0, elapsed || 0))}`);
  const tests = Object.values(cell.tests);
  const failed = tests.filter(test => ["failed", "timed-out"].includes(test.state)).length;
  updateBenchmarkText(view.counts, tests.length === cell.total && cell.total > 0 && tests.every(test => test.state === "passed" && test.id !== "MANUAL-CUSTOM-001")
    ? "All tests passed"
    : `${failed} failed or timed out · ${tests.filter(test => isBenchmarkCellActive(test)).length} active · ${tests.filter(test => test.state === "pending").length} queued`);
  const activeTest = cell.tests[cell.currentTest];
  const inProgress = activeTest && isBenchmarkCellActive(activeTest);
  updateBenchmarkText(view.current, inProgress
    ? `Now: ${benchmarkTestLabel(activeTest.id)} · ${activeTest.id} · ${benchmarkStateLabel(activeTest.state)}${activeTest.currentTurn ? ` · turn ${activeTest.currentTurn}/${activeTest.totalTurns}` : ""}`
    : "No active test in this combination.");
  view.current.hidden = !inProgress;
  for (const [id, testView] of view.testViews) {
    if (!cell.tests[id]) { testView.details.remove(); testView.bar.remove(); view.testViews.delete(id); }
  }
  for (const test of tests) {
    let item = view.testViews.get(test.id);
    if (!item) {
      const details = document.createElement("details"); details.className = "benchmark-live-test"; details.dataset.testId = test.id;
      const summary = document.createElement("summary");
      appendBenchmarkTestIdentity(summary, test.id);
      const status = createBenchmarkStatus("", test.state); status.classList.add("benchmark-test-state");
      summary.append(status);
      const activities = document.createElement("ol"); activities.className = "benchmark-activity-timeline";
      activities.setAttribute("aria-label", "Recent activity");
      const checks = document.createElement("div"); checks.className = "benchmark-live-checks";
      const message = document.createElement("p");
      const advanced = createBenchmarkAdvanced(`${cell.id}:${test.id}`);
      const error = document.createElement("p"); error.className = "benchmark-validation-error";
      advanced.querySelector(".benchmark-advanced-content").append(error, checks);
      details.append(summary, message, activities, advanced);
      const bar = document.createElement("span"); view.progress.append(bar);
      view.tests.append(details);
      item = { details, status, activities, checks, message, error, bar, activityText: "", checkText: "" };
      view.testViews.set(test.id, item);
    }
    item.details.dataset.state = test.state;
    item.bar.dataset.state = test.state;
    item.bar.title = `${test.id} · ${benchmarkStateLabel(test.state)}`;
    updateBenchmarkText(item.status, benchmarkStateLabel(test.state));
    item.status.dataset.tone = benchmarkStatusTone(test.state);
    const activityText = test.activities.map(activity => activity.turnNumber
      ? `Turn ${activity.turnNumber}/${activity.totalTurns} · ${activity.kind}: ${activity.message}`
      : `${activity.kind}: ${activity.message}`);
    const signature = JSON.stringify(activityText);
    if (item.activityText !== signature) {
      item.activities.replaceChildren(...test.activities.map(activity => createBenchmarkTimelineItem(
        activity.kind,
        `${activity.turnNumber ? `Turn ${activity.turnNumber}/${activity.totalTurns} · ` : ""}${activity.message}`,
        activity.kind === "recovered-error" || activity.kind === "timeout" ? "warning" : "neutral"
      )));
      item.activityText = signature;
    }
    item.activities.hidden = !activityText.length;
    const checks = Object.entries(test.checks);
    const raw = test.result?.rawResult;
    const checkText = JSON.stringify([checks, raw?.validationFacts, raw?.changedFiles, raw?.hostValidationResult, raw?.containmentAccuracy]);
    if (item.checkText !== checkText) {
      item.checks.replaceChildren();
      const comparison = test.id === "MANUAL-CUSTOM-001" ? null : createBenchmarkAcceptanceTable(test.result?.rawResult, test.checks);
      if (comparison) item.checks.append(comparison.shell);
      const remainingChecks = checks.filter(([name]) => !comparison?.factKeys.has(name));
      if (remainingChecks.length) {
        const { shell, body } = createBenchmarkDataTable(["Property", "Recorded value"], "Validation evidence");
        for (const [name, value] of remainingChecks) appendBenchmarkDataRow(body, [benchmarkEvidenceLabel(name), createBenchmarkEvidenceValue(name, value)]);
        item.checks.append(shell);
      }
      item.checkText = checkText;
    }
    item.checks.hidden = item.checks.childElementCount === 0;
    const message = benchmarkFailureSummary(test.id, test.result?.rawResult, test.state);
    updateBenchmarkText(item.message, message);
    item.message.hidden = !message;
    const error = test.result?.rawResult?.error;
    updateBenchmarkText(item.error, error ? `${error.code}: ${error.message}` : "");
    item.error.hidden = !error;
  }
  view.progress.setAttribute("role", "img");
  view.progress.setAttribute("aria-label", `${cell.completed} of ${cell.total} tests finished; ${cell.passed} passed; ${failed} failed or timed out`);
}

function renderBenchmarkPairIdentity(element, rank, model, harness) {
  element.classList.add("benchmark-pair-identity");
  const name = document.createElement("strong");
  name.textContent = `${rank ? `#${rank}` : "—"} ${model ?? ""}`;
  const label = document.createElement("small");
  label.textContent = benchmarkHarnessLabel(harness);
  element.append(name, label);
}

function appendBenchmarkRankingCells(row, values, status, scoreIndex = 2) {
  values.forEach((value, index) => {
    const cell = document.createElement("td");
    if (index === 0) cell.append(createBenchmarkStatus(value, status));
    else {
      cell.textContent = value;
      if (index === scoreIndex) cell.className = "benchmark-ranking-score";
    }
    row.append(cell);
  });
}

function renderProvisionalBenchmarkRanking(ranking) {
  elements.benchmarkResultsBody.replaceChildren();
  for (const entry of ranking) {
    const row = document.createElement("tr");
    const identity = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "benchmark-result-link";
    button.dataset.liveCell = benchmarkCellKey(entry.model, entry.harness);
    renderBenchmarkPairIdentity(button, entry.rank, entry.model, entry.harness);
    identity.append(button);
    row.append(identity);
    appendBenchmarkRankingCells(row, [
      benchmarkStateLabel(entry.state),
      `${entry.passed}/${entry.total}`,
      entry.score === null ? "—" : `${Number(entry.score).toFixed(2)}*`,
      formatBenchmarkDuration(entry.durationMilliseconds),
      `${entry.terminality}%`
    ], entry.state);
    elements.benchmarkResultsBody.append(row);
  }
}

function finishBenchmarkLive(result) {
  if (state.benchmark?.live?.terminal) {
    return;
  }
  state.benchmark.live.terminal = true;
  clearBenchmarkLiveConnection();
  state.activeBenchmarkRunId = null;
  sessionStorage.removeItem(benchmarkLiveRunStorageKey);
  state.benchmark.result = result;
  renderBenchmarkResult(result);
  if (result.infrastructureError || result.persistenceError) {
    clearBenchmarkBatch();
    setBenchmarkRunning(false);
    elements.benchmarkStatus.textContent = [
      result.infrastructureError?.message,
      result.persistenceError?.message ?? "Available results were saved."
    ].filter(Boolean).join(" ");
    return;
  }
  const batch = state.benchmarkBatch;
  if (batch) {
    batch.completed += 1;
    batch.runIds.push(result.runId);
    persistBenchmarkBatch();
    if (batch.cancelRequested || result.terminalState === "cancelled") {
      completeBenchmarkBatch(
        `Benchmark batch canceled after ${batch.completed}/${batch.total} loop(s); completed evidence was persisted.`,
        result.runId
      );
      return;
    }
    if (batch.started < batch.total) {
      elements.benchmarkStatus.textContent =
        `Benchmark loop ${batch.completed}/${batch.total} persisted; starting the next loop…`;
      void startNextBenchmarkRun();
      return;
    }
    completeBenchmarkBatch(
      batch.total === 1
        ? "Benchmark completed and persisted."
        : `Benchmark batch completed: ${batch.completed}/${batch.total} independent run(s) persisted.`,
      result.runId
    );
    return;
  }

  setBenchmarkRunning(false);
  refreshCompletedBenchmark(result.runId);
  elements.benchmarkStatus.textContent = result.terminalState === "cancelled"
    ? "Run canceled with a persisted final result."
    : "Benchmark completed and persisted.";
}

function failBenchmarkLive(message) {
  if (state.benchmark?.live) {
    state.benchmark.live.terminal = true;
    state.benchmark.live.failure = message ?? "The live benchmark failed before the final result.";
    renderBenchmarkLive();
  }
  clearBenchmarkLiveConnection();
  state.activeBenchmarkRunId = null;
  sessionStorage.removeItem(benchmarkLiveRunStorageKey);
  clearBenchmarkBatch();
  setBenchmarkRunning(false);
  elements.benchmarkStatus.textContent = message ?? "The live benchmark failed before the final result.";
}

function completeBenchmarkBatch(message, selectedRunId) {
  clearBenchmarkBatch();
  setBenchmarkRunning(false);
  refreshCompletedBenchmark(selectedRunId);
  elements.benchmarkStatus.textContent = message;
}

function refreshCompletedBenchmark(selectedRunId) {
  refreshBenchmarkHistory({ selectRunId: selectedRunId });
  rescoreBenchmarkResult().catch(error => {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  });
  if (!benchmarkIsCustomPromptOnly(state.benchmark?.result)) {
    generateGeneralBenchmarkRecommendation().catch(error => {
      elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
    });
  }
}

function clearBenchmarkLiveConnection() {
  if (state.benchmarkEventSource) {
    state.benchmarkEventSource.close();
    state.benchmarkEventSource = null;
  }
  if (state.benchmarkElapsedTimer) {
    clearInterval(state.benchmarkElapsedTimer);
    state.benchmarkElapsedTimer = null;
  }
}

async function cancelBenchmarkSuite() {
  if (!state.activeBenchmarkRunId) {
    return;
  }
  if (state.benchmarkBatch) {
    state.benchmarkBatch.cancelRequested = true;
    persistBenchmarkBatch();
  }
  elements.benchmarkStatus.textContent = "Requesting clean cancellation…";
  for (const harness of Object.values(state.benchmark?.live?.cells ?? {})) {
    if (!["completed", "cancelled"].includes(harness.state)) {
      harness.state = "cancelling";
    }
  }
  renderBenchmarkLive();
  try {
    await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(state.activeBenchmarkRunId)}/cancel`,
      { method: "POST" }
    );
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function setBenchmarkRunning(running) {
  elements.runBenchmark.disabled = running;
  elements.cancelBenchmark.disabled = !running;
  elements.cancelBenchmark.hidden = !running;
  elements.benchmarkModel.disabled = running;
  for (const input of elements.benchmarkModelList.querySelectorAll("input")) {
    input.disabled = running;
  }
  elements.benchmarkScoringProfileChoice.disabled = running
    || (selectedBenchmarkSuites().length > 0
      && selectedBenchmarkSuites().every(suite => suite.id === "manual"));
  elements.benchmarkTimeout.disabled = running;
  elements.benchmarkRepetitions.disabled = running;
  elements.benchmarkContextTokens.disabled = running;
  elements.benchmarkDefaultGpu.disabled = running;
  elements.benchmarkRunName.disabled = running;
  elements.benchmarkCustomPrompt.disabled = running;
  for (const input of elements.benchmarkSuiteList.querySelectorAll("input")) {
    input.disabled = running;
  }
  elements.benchmarkHistory.disabled = running;
  elements.benchmarkHistoryModelFilter.disabled = running;
  elements.benchmarkHistoryHarnessFilter.disabled = running;
  elements.benchmarkHistorySuiteFilter.disabled = running;
  elements.benchmarkCompareBaseline.disabled = running;
  elements.benchmarkCompareCandidate.disabled = running;
  elements.compareBenchmarkRuns.disabled = running
    || (state.benchmark?.history?.length ?? 0) < 2;
  elements.benchmarkDeleteResult.disabled = running || !elements.benchmarkHistory.value;
  elements.benchmarkDeleteAllResults.disabled = running
    || (state.benchmark?.history?.length ?? 0) === 0;
  elements.generateBenchmarkRecommendation.disabled = running;
  elements.researchBenchmarkRecommendation.disabled = running
    || !state.benchmark?.recommendationCatalog?.externalResearchAvailable;
  for (const input of elements.benchmarkHarnessList.querySelectorAll("input")) {
    input.disabled = running || input.dataset.available === "false";
  }
  renderBenchmarkSelectionSummary();
}

async function openPersistedBenchmark() {
  try {
    const runId = elements.benchmarkHistory.value;
    elements.benchmarkStatus.textContent = runId
      ? "Loading persisted result…"
      : "Clearing loaded result…";
    const selected = runId
      ? await fetchJson(`/api/benchmarks/suite-runs/${encodeURIComponent(runId)}`)
      : null;
    state.benchmark.result = selected;
    state.benchmark.scoringProjection = null;
    if (selected) {
      if (selected.customPrompt !== null && selected.customPrompt !== undefined) {
        elements.benchmarkCustomPrompt.value = selected.customPrompt;
      }
      elements.benchmarkRunName.value = selected.runName ?? "";
      const selections = selected.selectedSuites
        ?? [{ id: selected.suiteId, version: selected.suiteVersion }];
      for (const input of elements.benchmarkSuiteList.querySelectorAll(
        'input[name="benchmark-suite"]'
      )) {
        input.checked = selections.some(item =>
          item.id === input.value && item.version === Number(input.dataset.version)
        );
      }
      updateBenchmarkManualSelection();
    }
    await rescoreBenchmarkResult();
    if (!benchmarkIsCustomPromptOnly(selected)) {
      await generateGeneralBenchmarkRecommendation();
    }
    renderBenchmarkHistory();
    elements.benchmarkStatus.textContent = selected
      ? `Persisted result loaded: ${benchmarkSuiteLabel(selected.suiteId)} · ${selected.runId.slice(0, 8)}.`
      : "No persisted result loaded.";
  } catch (error) {
    renderBenchmarkResult(state.benchmark?.result ?? null);
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function renderBenchmarkResult(result) {
  preserveBenchmarkDisclosures(elements.benchmarkResultDetail, () => renderBenchmarkResultContent(result));
}

function renderBenchmarkResultContent(result) {
  const retainedLive = state.benchmark?.live;
  const keepLive = retainedLive && (state.activeBenchmarkRunId || retainedLive.runId === result?.runId);
  elements.benchmarkLiveDashboard.hidden = !keepLive;
  elements.benchmarkCombinationPicker.hidden = !keepLive;
  elements.benchmarkExecutionEmpty.hidden = Boolean(keepLive);
  if (keepLive) renderBenchmarkLive();
  else {
    elements.benchmarkProgressTitle.textContent = result ? "Saved result" : "Ready to benchmark";
    elements.benchmarkCurrentCombination.textContent = "No active combination.";
    elements.benchmarkFollowActive.hidden = true;
    elements.benchmarkBatchProgress.hidden = true;
  }
  elements.benchmarkRankingNote.hidden = true;
  elements.benchmarkMatrix.hidden = true;
  elements.benchmarkResultsBody.replaceChildren();
  elements.benchmarkResultDetail.replaceChildren();
  elements.benchmarkRawEvidence.hidden = !result;
  elements.benchmarkRawEvidenceContent.textContent = result
    ? JSON.stringify(result, null, 2)
    : "";
  const manual = benchmarkIsCustomPromptOnly(result);
  const mixed = benchmarkIncludesCustomPrompt(result) && !manual;
  const customRanking = elements.benchmarkView.querySelector("#benchmark-custom-ranking");
  customRanking.hidden = !mixed;
  const scoringPanel = elements.benchmarkView.querySelector(".benchmark-scoring-advanced");
  if (scoringPanel) scoringPanel.hidden = manual;
  const rankingHeaders = elements.benchmarkResultsBody.closest("table").querySelectorAll("thead th");
  rankingHeaders[2].dataset.i18n = manual
    ? "benchmark.custom_prompt.technical_column"
    : "benchmark.results.passed";
  rankingHeaders[2].textContent = manual
    ? t("benchmark.custom_prompt.technical_column")
    : t("benchmark.results.passed");
  rankingHeaders[3].dataset.i18n = manual
    ? "benchmark.custom_prompt.user_score_column"
    : "benchmark.results.score";
  rankingHeaders[3].textContent = manual
    ? t("benchmark.custom_prompt.user_score_column")
    : t("benchmark.results.score");
  rankingHeaders[3].title = manual
    ? t("benchmark.custom_prompt.user_score_hint")
    : t("benchmark.results.calculated_score_hint");
  if (!result) {
    elements.benchmarkRunSummary.textContent = "Run or open a persisted result.";
    elements.benchmarkResultDetail.textContent = "Select a harness in the table to inspect scenarios.";
    return;
  }

  const projection = state.benchmark?.scoringProjection?.runId === result.runId
    ? state.benchmark.scoringProjection
    : null;
  const profile = projection?.activeProfile ?? state.benchmark?.scoringProfile;
  const modelSummary = result.selectedModels?.length > 1
    ? `${result.selectedModels.length} models × ${result.selectedHarnesses?.length ?? 0} harnesses`
    : result.selectedModels?.[0] ?? result.model;
  elements.benchmarkRunSummary.textContent =
    `${result.runName ? `${result.runName} · ` : ""}${modelSummary} · ${benchmarkSuiteLabel(result.suiteId)} · ${result.finalStatus}${benchmarkIncludesCustomPrompt(result) ? ` · ${t("benchmark.custom_prompt.name")} ${benchmarkReviewStatusLabel(result.reviewStatus)}` : ""} · ${formatBenchmarkDuration(result.durationMilliseconds)}`;
  const originalScore = benchmarkAggregateOriginalScore(result);
  const currentScore = benchmarkAggregateCurrentScore(projection, result);
  const executionConfiguration = result.configuration
    ? ` · ${Number(result.configuration.contextTokens ?? result.environment?.configuredContextTokens ?? 0).toLocaleString("en-US")} ctx · ${result.configuration.gpu ?? "GPU unavailable"}`
    : "";
  elements.benchmarkScoreContext.textContent = manual
    ? t("benchmark.custom_prompt.score_context_manual")
    : mixed
    ? t("benchmark.custom_prompt.score_context_mixed")
    : profile
    ? `Measured evidence unchanged · Original score ${originalScore.toFixed(2)} · Current-profile score ${currentScore.toFixed(2)} with ${profile.displayName} v${profile.version}${executionConfiguration}`
    : `Measured evidence and Calculated score are presented separately.${executionConfiguration}`;
  if ((result.cells ?? []).length > 0) {
    renderBenchmarkMatrix(result, projection);
    renderBenchmarkRankings(result, projection);
    if (mixed) renderBenchmarkCustomRanking(result);
    const selection = state.benchmarkUi.resultSelection;
    const ranking = projection?.pairRanking ?? result.pairRanking ?? [];
    const first = ranking.find(entry => selection?.runId === result.runId && entry.model === selection.model && entry.harness === selection.harness)
      ?? ranking[0]
      ?? result.cells.find(cell => cell.result);
    if (first) {
      renderBenchmarkMatrixCellDetail(first.model, first.harness);
    }
    return;
  }
  const byHarness = new Map(result.harnessResults.map(item => [item.harness, item]));
  const scoreByHarness = new Map(
    (projection?.harnessScores ?? []).map(item => [item.harness, item])
  );
  const ranking = projection?.ranking ?? result.ranking;
  for (const ranked of ranking) {
    const harness = byHarness.get(ranked.harness);
    if (!harness) {
      continue;
    }
    const row = document.createElement("tr");
    const harnessCell = document.createElement("td");
    const open = document.createElement("button");
    open.type = "button";
    open.className = "benchmark-result-link";
    open.dataset.harness = harness.harness;
    open.textContent = `#${ranked.rank} ${benchmarkHarnessLabel(harness.harness)}`;
    harnessCell.append(open);
    appendBenchmarkRankingCells(row, [
      harness.terminalState,
      `${harness.passed}/${harness.total}`,
      Number(scoreByHarness.get(harness.harness)?.score ?? harness.score).toFixed(2),
      formatBenchmarkDuration(harness.durationMilliseconds),
      `${harness.terminality}%`
    ], harness.terminalState);
    row.prepend(harnessCell);
    elements.benchmarkResultsBody.append(row);
  }
  const selection = state.benchmarkUi.resultSelection;
  const firstHarness = selection?.runId === result.runId && byHarness.has(selection.harness)
    ? selection.harness : ranking[0]?.harness;
  if (firstHarness) {
    renderBenchmarkHarnessDetail(byHarness.get(firstHarness), scoreByHarness.get(firstHarness));
  }
}

function renderBenchmarkMatrix(result, projection) {
  elements.benchmarkMatrix.hidden = false;
  elements.benchmarkMatrix.replaceChildren();
  const models = result.selectedModels ?? [...new Set(result.cells.map(cell => cell.model))];
  const harnesses = result.selectedHarnesses ?? [...new Set(result.cells.map(cell => cell.harness))];
  const scoreByCell = new Map(
    (projection?.matrixCellScores ?? []).map(cell => [benchmarkCellKey(cell.model, cell.harness), cell.score])
  );
  const byCell = new Map(result.cells.map(cell => [benchmarkCellKey(cell.model, cell.harness), cell]));
  const table = document.createElement("table");
  const head = document.createElement("thead");
  const headRow = document.createElement("tr");
  const modelHeading = document.createElement("th");
  modelHeading.textContent = "Model";
  headRow.append(modelHeading);
  for (const harness of harnesses) {
    const heading = document.createElement("th");
    heading.textContent = benchmarkHarnessLabel(harness);
    headRow.append(heading);
  }
  head.append(headRow);
  const body = document.createElement("tbody");
  for (const model of models) {
    const row = document.createElement("tr");
    const label = document.createElement("th");
    label.scope = "row";
    label.textContent = model;
    row.append(label);
    for (const harness of harnesses) {
      const cellElement = document.createElement("td");
      const cell = byCell.get(benchmarkCellKey(model, harness));
      const button = document.createElement("button");
      button.type = "button";
      button.className = "benchmark-matrix-cell";
      button.dataset.model = model;
      button.dataset.harness = harness;
      button.dataset.status = cell?.status ?? "unavailable";
      const calculated = scoreByCell.get(benchmarkCellKey(model, harness));
      button.textContent = cell?.status === "completed"
        ? benchmarkIsCustomPromptOnly(result)
          ? cell.userScore ?? benchmarkReviewStatusLabel(cell.reviewStatus)
          : benchmarkIncludesCustomPrompt(result)
          ? `${Number(calculated ?? cell.score).toFixed(2)} · ${cell.userScore ?? t("benchmark.custom_prompt.review_column")}`
          : Number(calculated ?? cell.score).toFixed(2)
        : cell?.status ?? "unavailable";
      button.title = cell?.message ?? `${model} × ${benchmarkHarnessLabel(harness)}`;
      cellElement.append(button);
      row.append(cellElement);
    }
    body.append(row);
  }
  table.append(head, body);
  elements.benchmarkMatrix.append(table);
}

function renderBenchmarkRankings(result, projection) {
  elements.benchmarkResultsBody.replaceChildren();
  if (!result) {
    return;
  }
  if (benchmarkIsCustomPromptOnly(result)) {
    const cells = [...(result.cells ?? [])].sort((left, right) => {
      if (left.userScore !== null && left.userScore !== undefined
        && right.userScore !== null && right.userScore !== undefined) {
        return right.userScore - left.userScore
          || left.durationMilliseconds - right.durationMilliseconds;
      }
      if (left.userScore !== null && left.userScore !== undefined) return -1;
      if (right.userScore !== null && right.userScore !== undefined) return 1;
      return left.executionOrder - right.executionOrder;
    });
    let rank = 0;
    for (const resultCell of cells) {
      const row = document.createElement("tr");
      const identity = document.createElement("td");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "benchmark-result-link";
      button.dataset.model = resultCell.model;
      button.dataset.harness = resultCell.harness;
      if (resultCell.userScore !== null && resultCell.userScore !== undefined) rank++;
      renderBenchmarkPairIdentity(button, resultCell.userScore === null || resultCell.userScore === undefined ? null : rank, resultCell.model, resultCell.harness);
      identity.append(button);
      row.append(identity);
      appendBenchmarkRankingCells(row, [
        resultCell.status === "completed" ? benchmarkReviewStatusLabel(resultCell.reviewStatus) : resultCell.status,
        resultCell.status === "completed"
          ? t("benchmark.custom_prompt.technical_completed")
          : t("benchmark.custom_prompt.technical_failure"),
        resultCell.userScore ?? "—",
        formatBenchmarkDuration(resultCell.durationMilliseconds),
        `${resultCell.terminality}%`
      ], resultCell.status);
      elements.benchmarkResultsBody.append(row);
    }
    return;
  }
  const scope = elements.benchmarkRankingScope.value;
  if (scope === "model" || scope === "harness") {
    const ranking = scope === "model"
      ? projection?.modelRanking ?? result.modelRanking ?? []
      : projection?.harnessRanking ?? result.harnessRanking ?? [];
    for (const entry of ranking) {
      const row = document.createElement("tr");
      const label = scope === "model" ? entry.id : benchmarkHarnessLabel(entry.id);
      const identity = document.createElement("td");
      identity.textContent = `#${entry.rank} ${label}`;
      row.append(identity);
      appendBenchmarkRankingCells(row, [
        `${entry.completedCells}/${entry.totalCells} completed`,
        String(entry.passed),
        Number(entry.score).toFixed(2),
        formatBenchmarkDuration(entry.durationMilliseconds),
        `${entry.terminality}%`
      ], "completed");
      elements.benchmarkResultsBody.append(row);
    }
    return;
  }
  const ranking = projection?.pairRanking ?? result.pairRanking ?? [];
  for (const entry of ranking) {
    const resultCell = result.cells?.find(cell =>
      cell.model === entry.model && cell.harness === entry.harness
    );
    const row = document.createElement("tr");
    const identity = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "benchmark-result-link";
    button.dataset.model = entry.model;
    button.dataset.harness = entry.harness;
    renderBenchmarkPairIdentity(button, entry.rank, entry.model, entry.harness);
    identity.append(button);
    row.append(identity);
    appendBenchmarkRankingCells(row, [
      entry.status,
      `${entry.passed}/${resultCell?.result?.tests?.filter(test => test.run.suiteId !== "manual").length ?? Math.max(0, (resultCell?.total ?? 0) - (benchmarkIncludesCustomPrompt(result) ? 1 : 0))}`,
      Number(entry.score).toFixed(2),
      formatBenchmarkDuration(entry.durationMilliseconds),
      `${entry.terminality}%`
    ], entry.status);
    elements.benchmarkResultsBody.append(row);
  }
}

function renderBenchmarkCustomRanking(result) {
  const body = elements.benchmarkView.querySelector("#benchmark-custom-ranking-body");
  body.replaceChildren();
  const cells = [...(result.cells ?? [])].sort((left, right) => {
    if (left.userScore !== null && left.userScore !== undefined
      && right.userScore !== null && right.userScore !== undefined) {
      return right.userScore - left.userScore
        || left.durationMilliseconds - right.durationMilliseconds;
    }
    if (left.userScore !== null && left.userScore !== undefined) return -1;
    if (right.userScore !== null && right.userScore !== undefined) return 1;
    return left.executionOrder - right.executionOrder;
  });
  let rank = 0;
  for (const resultCell of cells) {
    const row = document.createElement("tr");
    const identity = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "benchmark-result-link";
    button.dataset.model = resultCell.model;
    button.dataset.harness = resultCell.harness;
    if (resultCell.userScore !== null && resultCell.userScore !== undefined) rank++;
    renderBenchmarkPairIdentity(button, resultCell.userScore === null || resultCell.userScore === undefined ? null : rank, resultCell.model, resultCell.harness);
    identity.append(button);
    row.append(identity);
    appendBenchmarkRankingCells(row, [
      benchmarkReviewStatusLabel(resultCell.reviewStatus),
      resultCell.userScore ?? "—",
      formatBenchmarkDuration(resultCell.durationMilliseconds)
    ], resultCell.reviewStatus, 1);
    body.append(row);
  }
}

function openBenchmarkMatrixCell(event) {
  const button = event.target.closest("[data-model][data-harness]");
  if (button) {
    renderBenchmarkMatrixCellDetail(button.dataset.model, button.dataset.harness);
  }
}

function renderBenchmarkMatrixCellDetail(model, harnessId) {
  state.benchmarkUi.resultSelection = { runId: state.benchmark?.result?.runId, model, harness: harnessId };
  for (const button of elements.benchmarkResultsBody.querySelectorAll("button[data-model][data-harness]")) {
    button.setAttribute("aria-pressed", String(button.dataset.model === model && button.dataset.harness === harnessId));
  }
  const cell = state.benchmark?.result?.cells?.find(item =>
    item.model === model && item.harness === harnessId
  );
  const calculated = state.benchmark?.scoringProjection?.matrixCellScores?.find(item =>
    item.model === model && item.harness === harnessId
  );
  if (!cell?.result) {
    elements.benchmarkResultDetail.replaceChildren();
    const heading = document.createElement("h4");
    heading.textContent = `${model} × ${benchmarkHarnessLabel(harnessId)} · ${cell?.status ?? "unavailable"}`;
    const message = document.createElement("p");
    message.textContent = cell?.message ?? "This combination did not produce an executable result.";
    elements.benchmarkResultDetail.append(heading, message);
    return;
  }
  renderBenchmarkHarnessDetail(cell.result, calculated, model);
}

function openBenchmarkHarnessResult(event) {
  const liveButton = event.target.closest("[data-live-cell]");
  if (liveButton && state.benchmark?.live?.cells[liveButton.dataset.liveCell]) {
    state.benchmarkUi.selectedCell = liveButton.dataset.liveCell;
    renderBenchmarkLive();
    showBenchmarkTab("execution", true);
    return;
  }
  const button = event.target.closest("[data-harness]");
  if (!button) {
    return;
  }
  if (button.dataset.model) {
    renderBenchmarkMatrixCellDetail(button.dataset.model, button.dataset.harness);
    return;
  }
  const harness = state.benchmark?.result?.harnessResults?.find(
    item => item.harness === button.dataset.harness
  );
  const score = state.benchmark?.scoringProjection?.harnessScores?.find(
    item => item.harness === button.dataset.harness
  );
  renderBenchmarkHarnessDetail(harness, score);
}

function renderBenchmarkHarnessDetail(harness, calculated, model = null) {
  state.benchmarkUi.resultSelection = { runId: state.benchmark?.result?.runId, model, harness: harness?.harness };
  elements.benchmarkResultDetail.replaceChildren();
  if (!harness) {
    elements.benchmarkResultDetail.textContent = "Harness result unavailable.";
    return;
  }
  const heading = document.createElement("h4");
  const result = state.benchmark?.result;
  const manual = benchmarkIsCustomPromptOnly(result);
  const predefined = harness.tests.filter(test => test.run.suiteId !== "manual");
  const predefinedPassed = predefined.filter(test => test.rawResult.status === "pass").length;
  const progress = manual
    ? t("benchmark.custom_prompt.manual_review")
    : benchmarkIncludesCustomPrompt(result)
    ? `${t("benchmark.custom_prompt.predefined_passed", { passed: predefinedPassed, total: predefined.length })} · ${t("benchmark.custom_prompt.name")} ${benchmarkReviewStatusLabel(result.reviewStatus)}`
    : `${harness.passed}/${harness.total} passed`;
  heading.textContent = `${model ? `${model} × ` : ""}${benchmarkHarnessLabel(harness.harness)}`;
  const overview = document.createElement("header");
  overview.className = "benchmark-result-overview";
  const progressLabel = document.createElement("p");
  progressLabel.textContent = progress;
  overview.append(heading, progressLabel);
  elements.benchmarkResultDetail.append(overview);
  if (harness.total > 0 && harness.tests.length === harness.total
    && harness.tests.every(test => test.run.suiteId !== "manual" && String(test.rawResult.status).toLowerCase() === "pass")) {
    const passed = document.createElement("p");
    passed.className = "benchmark-outcome-summary";
    passed.textContent = "All tests passed";
    elements.benchmarkResultDetail.append(passed);
  }
  if (benchmarkIncludesCustomPrompt(result)) {
    const rerun = document.createElement("button");
    rerun.type = "button";
    rerun.className = "secondary-button";
    rerun.dataset.benchmarkRerun = "true";
    rerun.dataset.i18n = "benchmark.custom_prompt.rerun";
    rerun.textContent = t("benchmark.custom_prompt.rerun");
    elements.benchmarkResultDetail.append(rerun);
  }
  if (calculated) {
    const scoreHeading = document.createElement("strong");
    scoreHeading.className = "benchmark-total-score";
    scoreHeading.textContent = `Calculated score · ${Number(calculated.score).toFixed(2)}`;
    const breakdown = document.createElement("dl");
    breakdown.className = "benchmark-score-breakdown";
    for (const [label, value] of [
      ["Objective success", calculated.breakdown.objectiveSuccess],
      ["Correctness / exactness", calculated.breakdown.correctness],
      ["Terminality", calculated.breakdown.terminality],
      ["Workspace accuracy", calculated.breakdown.workspaceAccuracy],
      ["Efficiency", calculated.breakdown.efficiency]
    ]) {
      const term = document.createElement("dt");
      term.textContent = label;
      const definition = document.createElement("dd");
      definition.textContent = Number(value).toFixed(2);
      const metric = document.createElement("div");
      metric.append(term, definition);
      breakdown.append(metric);
    }
    elements.benchmarkResultDetail.append(scoreHeading, breakdown);
  }
  if (harness.tests.length === 0) {
    const empty = document.createElement("p");
    empty.textContent = "No test started before cancellation.";
    elements.benchmarkResultDetail.append(empty);
    return;
  }
  for (const test of harness.tests) {
    const manualTest = test.run.suiteId === "manual";
    const details = document.createElement("details");
    details.className = "benchmark-test-detail";
    details.dataset.state = manualTest ? test.rawResult.executionStatus : String(test.rawResult.status).toLowerCase();
    details.dataset.testId = test.run.testId;
    details.dataset.disclosureKey = `${model ?? ""}:${harness.harness}:${test.run.testId}`;
    const summary = document.createElement("summary");
    const calculatedTest = calculated?.tests?.find(item => item.runId === test.run.runId);
    appendBenchmarkTestIdentity(summary, test.run.testId);
    const metadata = document.createElement("span");
    metadata.className = "benchmark-test-meta";
    const scoreLabel = document.createElement("small");
    scoreLabel.textContent = manualTest
      ? `${test.rawResult.executionStatus}${test.userReview ? ` · ${t("benchmark.custom_prompt.quality_score", { score: test.userReview.score })}` : ""}`
      : `Score ${Number(calculatedTest?.score?.total ?? test.score?.total ?? 0).toFixed(2)} / 100`;
    const statusLabel = manualTest ? benchmarkReviewStatusLabel(test.reviewStatus)
      : ({ pass: "Passed", fail: "Failed", error: "Error" })[String(test.rawResult.status).toLowerCase()] ?? test.rawResult.status;
    metadata.append(scoreLabel, createBenchmarkStatus(statusLabel, manualTest ? test.rawResult.executionStatus : test.rawResult.status));
    summary.append(metadata);
    details.append(summary);
    const explanation = benchmarkFailureSummary(test.run.testId, test.rawResult);
    if (explanation) {
      const message = document.createElement("p");
      message.className = "benchmark-outcome-summary";
      message.textContent = explanation;
      details.append(message);
    }
    const advanced = createBenchmarkAdvanced(details.dataset.disclosureKey);
    const advancedContent = advanced.querySelector(".benchmark-advanced-content");
    const comparison = manualTest ? null : createBenchmarkAcceptanceTable(test.rawResult);
    if (comparison) advancedContent.append(comparison.shell);
    const facts = createBenchmarkDataTable(["Property", "Recorded value"], "Measured evidence", "benchmark-evidence-grid");
    const operational = test.rawResult.operationalDiagnostics;
    const runtime = test.rawResult.runtimeEvidence;
    const runtimeEvidence = runtime ? [
      ["Runtime · GPU selection", runtime.gpuSelection],
      ["Runtime · Backend", runtime.backend ?? "Unavailable"],
      ["Runtime · Context", `${runtime.requestedContextTokens} requested / ${runtime.actualContextTokens ?? "not observed"} observed · ${runtime.contextStatus}`],
      ["Runtime · Processor", runtime.processor],
      ["Runtime · Model / VRAM / RAM", `${formatGiB(runtime.modelSizeBytes)} / ${formatGiB(runtime.vramSizeBytes)} / ${formatGiB(runtime.estimatedRamSizeBytes)}`],
      ["Runtime · Prompt tokens/s", runtime.promptTokensPerSecond ?? "Unavailable"],
      ["Runtime · Output tokens/s", runtime.outputTokensPerSecond ?? "Unavailable"],
      ["Runtime · Timeout phase", runtime.timeoutPhase ?? "n/a"],
      ["Runtime · Last progress", runtime.lastProgress ?? "Unavailable"],
      ["Runtime · System RAM sample", runtime.deviceMemory?.systemMemory
        ? `${formatGiB(runtime.deviceMemory.systemMemory.usedBytes)} / ${formatGiB(runtime.deviceMemory.systemMemory.totalBytes)} · ${runtime.deviceMemory.systemMemory.usedPercent ?? "n/d"}%`
        : "Unavailable"],
      ["Runtime · GPU VRAM samples", (runtime.deviceMemory?.gpus ?? []).map(device =>
        `${device.name}: ${formatGiB(device.usedDedicatedMemoryBytes)} / ${formatGiB(device.totalDedicatedMemoryBytes)} · ${device.usedPercent ?? "n/d"}%`
      ).join("; ") || "Unavailable"]
    ] : [];
    const operationalEvidence = operational ? [
      ["Operational · Strategy", `${operational.requestedStrategy} → ${operational.resolvedStrategy}`],
      ["Operational · Tool calls", operational.toolCalls ?? "Unavailable"],
      ["Operational · Failed tool calls", operational.failedToolCalls ?? "Unavailable"],
      ["Operational · Tool validation errors", operational.toolValidationErrors ?? "Unavailable"],
      ["Operational · Repeated tool calls", operational.repeatedToolCalls ?? "Unavailable"],
      ["Operational · Repeated identical actions", operational.repeatedIdenticalActions ?? "Unavailable"],
      ["Operational · Recovery attempts", operational.recoveryAttempts ?? "Unavailable"],
      ["Operational · Execution turns", operational.executionTurns ?? "Unavailable"],
      ["Operational · Terminal reason", operational.terminalReason],
      ["Operational · Execute duration", formatBenchmarkDuration(operational.executionDurationMilliseconds)],
      ["Operational · Host setup", formatBenchmarkDuration(operational.setupDurationMilliseconds)],
      ["Operational · Browser validation", formatBenchmarkDuration(operational.browserValidationDurationMilliseconds)],
      ["Operational · Tokens", operational.inputTokens === null || operational.inputTokens === undefined
        ? "Unavailable"
        : `${operational.inputTokens} in / ${operational.outputTokens ?? 0} out · ${operational.tokenProvenance}`],
      ["Operational · Files written", (operational.filesWritten ?? []).join(", ") || "none"],
      ["Operational · Files modified", (operational.filesModified ?? []).join(", ") || "none"],
      ["Operational · Unavailable metrics", (operational.unavailableMetrics ?? []).join(", ") || "none"]
    ] : [];
    const evidence = [
      ["Terminal", test.rawResult.executionStatus],
      ["Failure category", test.rawResult.failureCategory ?? "unknown"],
      ...(manualTest ? [] : [
        ["Exactness", `${test.rawResult.exactness}%`],
        ["Workspace", `${test.rawResult.containmentAccuracy}%`]
      ]),
      ["Host validation", test.rawResult.hostValidationResult],
      ["Duration", formatBenchmarkDuration(test.durationMilliseconds)],
      ["Workspace id", test.run.workspaceId],
      ["Fixture fingerprint", test.run.fixtureFingerprint],
      ["Workspace cleaned", test.workspaceCleanedUp],
      ["Tool calls", test.rawResult.toolCallCount ?? "Unavailable"],
      ["Errors / recovered", `${test.rawResult.surfacedErrorCount ?? "Unavailable"} / ${test.rawResult.recoveredErrorCount ?? "Unavailable"}`],
      ["Changed files", (test.rawResult.changedFiles ?? []).join(", ") || "none"],
      ...(manualTest ? [] : [
        ["Unexpected", (test.rawResult.unexpectedFiles ?? []).join(", ") || "none"],
        ["Turns", `${test.rawResult.behaviorMetrics?.successfulTerminalTurns ?? 0}/${test.rawResult.behaviorMetrics?.totalTurns ?? 0}`],
        ["Continuity", benchmarkMetric(test.rawResult.behaviorMetrics?.continuityPreservation)],
        ["Scope accuracy", benchmarkMetric(test.rawResult.behaviorMetrics?.scopeAccuracy)],
        ["Recovery", benchmarkMetric(test.rawResult.behaviorMetrics?.recovery)],
        ["Convergence", benchmarkMetric(test.rawResult.behaviorMetrics?.convergence)],
        ["Hygiene", benchmarkMetric(test.rawResult.behaviorMetrics?.hygiene)],
        ["Truthful report", benchmarkMetric(test.rawResult.behaviorMetrics?.truthfulFinalReport)],
        ["Narration", test.rawResult.behaviorMetrics?.narrationClassification ?? "Unavailable"]
      ]),
      ...runtimeEvidence,
      ...operationalEvidence,
      ...Object.entries(test.rawResult.validationFacts ?? {}).filter(([key]) => !comparison?.factKeys.has(key)).map(
        ([key, value]) => [`Validation · ${benchmarkEvidenceLabel(key)}`, value]
      )
    ];
    advancedContent.append(facts.shell);
    const groups = new Map([["", facts]]);
    for (const [label, value] of evidence) {
      const prefix = ["Runtime · ", "Operational · ", "Validation · "].find(prefix => label.startsWith(prefix)) ?? "";
      if (!groups.has(prefix)) {
        const label = ({ "Runtime · ": "Runtime and resources", "Operational · ": "Execution diagnostics", "Validation · ": "Validation facts" })[prefix];
        const group = createBenchmarkDataTable(["Property", "Recorded value"], label, "benchmark-evidence-grid");
        advancedContent.append(group.shell);
        groups.set(prefix, group);
      }
      appendBenchmarkDataRow(groups.get(prefix).body, [label.slice(prefix.length), createBenchmarkEvidenceValue(label, value)]);
    }
    if (test.rawResult.error) {
      const error = document.createElement("p");
      error.className = "benchmark-validation-error";
      error.textContent = `${test.rawResult.error.code}: ${test.rawResult.error.message}`;
      advancedContent.append(error);
    }
    appendManualBenchmarkReview(test, details);
    appendBenchmarkWorkspaceReview(test, details);
    const promptLabel = document.createElement("strong");
    promptLabel.textContent = "Canonical prompt";
    const prompt = document.createElement("pre");
    prompt.textContent = test.run.prompt;
    const reportLabel = document.createElement("strong");
    reportLabel.textContent = "Final harness report";
    const report = document.createElement("div");
    report.className = "benchmark-markdown benchmark-final-report";
    renderBenchmarkMarkdown(
      report,
      test.rawResult.finalHarnessReportHtml,
      test.rawResult.finalHarnessReport,
      "(no report)"
    );
    advancedContent.append(promptLabel, prompt);
    const reportCard = document.createElement("section");
    reportCard.className = "benchmark-report-card";
    reportCard.append(reportLabel, report);
    details.append(reportCard);
    const calls = test.rawResult.toolCalls ?? [];
    if (calls.length) {
      const activity = document.createElement("section");
      activity.className = "benchmark-recorded-activity";
      const title = document.createElement("h5");
      title.textContent = calls.length > 8 ? `Recent tool activity · last 8 of ${calls.length} events` : "Recorded tool activity";
      const timeline = document.createElement("ol");
      timeline.className = "benchmark-activity-timeline";
      for (const call of calls.slice(-8)) {
        timeline.append(createBenchmarkTimelineItem(call.state, `${call.tool}${call.path ? ` · ${call.path}` : ""}${call.turn ? ` · turn ${call.turn}` : ""}`,
          call.state === "failed" ? "warning" : "neutral"));
      }
      activity.append(title, timeline);
      details.append(activity);
    }
    if ((test.rawResult.turns ?? []).length > 0) {
      const turnsLabel = document.createElement("strong");
      turnsLabel.textContent = "Persisted turns";
      const turns = document.createElement("ol");
      for (const turn of test.rawResult.turns) {
        const item = document.createElement("li");
        item.className = "benchmark-persisted-turn";
        const metadata = document.createElement("div");
        metadata.className = "benchmark-turn-metadata";
        metadata.textContent = `${turn.name} · ${turn.executionStatus} · ${turn.durationMilliseconds} ms`;
        const narrative = document.createElement("div");
        narrative.className = "benchmark-markdown";
        renderBenchmarkMarkdown(
          narrative,
          turn.finalReportHtml,
          turn.finalReport,
          "(no report)"
        );
        item.append(metadata, narrative);
        turns.append(item);
      }
      turns.classList.add("benchmark-activity-timeline");
      advancedContent.append(turnsLabel, turns);
    }
    if ((test.rawResult.hostEvents ?? []).length > 0) {
      const hostLabel = document.createElement("strong");
      hostLabel.textContent = "Host events";
      const hostEvents = document.createElement("ul");
      hostEvents.className = "benchmark-activity-timeline";
      for (const hostEvent of test.rawResult.hostEvents) {
        hostEvents.append(createBenchmarkTimelineItem(hostEvent.type, `After turn ${hostEvent.afterTurn} · ${hostEvent.message}`));
      }
      advancedContent.append(hostLabel, hostEvents);
    }
    details.append(advanced);
    elements.benchmarkResultDetail.append(details);
  }
}

function renderBenchmarkMarkdown(container, renderedHtml, markdown, fallback) {
  if (!renderedHtml) {
    container.textContent = markdown || fallback;
    return;
  }
  const content = document.createElement("div");
  content.innerHTML = renderedHtml;
  container.replaceChildren(content);
  secureRenderedLinks(container);
  enhanceCodeBlocks(container, markdown ?? "");
}

function appendBenchmarkWorkspaceReview(test, details) {
  if (test.workspaceCleanedUp) {
    return;
  }
  const runId = state.benchmark?.result?.runId;
  if (!runId || !test.run?.workspaceId) {
    return;
  }
  const section = document.createElement("section");
  section.className = "benchmark-workspace-review";
  section.dataset.runId = runId;
  section.dataset.workspaceId = test.run.workspaceId;
  const heading = document.createElement("strong");
  heading.textContent = "Workspace for human review";
  const status = document.createElement("p");
  status.className = "benchmark-workspace-status";
  status.textContent = "Checking retained workspace…";
  const path = document.createElement("code");
  path.textContent = test.run.workspacePath;
  const actions = document.createElement("div");
  actions.className = "benchmark-workspace-actions";
  const view = document.createElement("button");
  view.type = "button";
  view.className = "secondary-button";
  view.dataset.benchmarkViewWorkspace = "true";
  view.textContent = "View folder";
  view.disabled = true;
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "secondary-button danger-button";
  remove.dataset.benchmarkDeleteWorkspace = "true";
  remove.textContent = "Delete workspace";
  remove.disabled = true;
  actions.append(view, remove);
  section.append(heading, status, path, actions);
  details.append(section);
  refreshBenchmarkWorkspaceStatus(section);
}

function appendManualBenchmarkReview(test, details) {
  if (test.run.suiteId !== "manual") {
    return;
  }
  const section = document.createElement("section");
  section.className = "benchmark-user-review";
  section.dataset.testRunId = test.run.runId;
  const heading = document.createElement("strong");
  heading.dataset.i18n = "benchmark.custom_prompt.review_heading";
  heading.textContent = t("benchmark.custom_prompt.review_heading");
  const status = document.createElement("p");
  status.className = "benchmark-review-status";
  status.textContent = benchmarkReviewStatusLabel(test.reviewStatus);
  section.append(heading, status);
  if (test.reviewStatus === "technical-failure") {
    const message = document.createElement("p");
    message.dataset.i18n = "benchmark.custom_prompt.technical_failure_note";
    message.textContent = t("benchmark.custom_prompt.technical_failure_note");
    section.append(message);
    details.append(section);
    return;
  }
  const scoreLabel = document.createElement("label");
  const scoreCaption = document.createElement("span");
  scoreCaption.dataset.i18n = "benchmark.custom_prompt.score_label";
  scoreCaption.textContent = t("benchmark.custom_prompt.score_label");
  const score = document.createElement("input");
  score.type = "number";
  score.min = "0";
  score.max = "100";
  score.step = "1";
  score.required = true;
  score.value = test.userReview?.score ?? "";
  score.dataset.benchmarkReviewScore = "true";
  scoreLabel.append(scoreCaption, score);
  const notesLabel = document.createElement("label");
  const notesCaption = document.createElement("span");
  notesCaption.dataset.i18n = "benchmark.custom_prompt.notes_label";
  notesCaption.textContent = t("benchmark.custom_prompt.notes_label");
  const notes = document.createElement("textarea");
  notes.rows = 4;
  notes.maxLength = 10000;
  notes.value = test.userReview?.notes ?? "";
  notes.dataset.benchmarkReviewNotes = "true";
  notesLabel.append(notesCaption, notes);
  const save = document.createElement("button");
  save.type = "button";
  save.className = "primary-button";
  save.dataset.benchmarkSaveReview = "true";
  save.dataset.i18n = test.userReview
    ? "benchmark.custom_prompt.update_review"
    : "benchmark.custom_prompt.save_review";
  save.textContent = test.userReview
    ? t("benchmark.custom_prompt.update_review")
    : t("benchmark.custom_prompt.save_review");
  section.append(scoreLabel, notesLabel, save);
  details.append(section);
}

async function refreshBenchmarkWorkspaceStatus(section) {
  try {
    const workspace = await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(section.dataset.runId)}/workspaces/${encodeURIComponent(section.dataset.workspaceId)}`
    );
    if (!section.isConnected) {
      return;
    }
    section.querySelector("code").textContent = workspace.workspacePath;
    section.querySelector(".benchmark-workspace-status").textContent = workspace.available
      ? "Retained locally and available for inspection."
      : "This workspace has been deleted.";
    section.querySelector("[data-benchmark-view-workspace]").disabled = !workspace.available;
    section.querySelector("[data-benchmark-delete-workspace]").disabled = !workspace.available;
    section.dataset.available = String(workspace.available);
  } catch (error) {
    if (section.isConnected) {
      section.querySelector(".benchmark-workspace-status").textContent = benchmarkErrorMessage(error);
    }
  }
}

async function handleBenchmarkResultDetailClick(event) {
  const saveReview = event.target.closest("[data-benchmark-save-review]");
  if (saveReview) {
    const reviewSection = saveReview.closest(".benchmark-user-review");
    const rawScore = reviewSection.querySelector("[data-benchmark-review-score]").value;
    const score = Number(rawScore);
    if (rawScore.trim() === "" || !Number.isInteger(score) || score < 0 || score > 100) {
      reviewSection.querySelector(".benchmark-review-status").textContent =
        t("benchmark.custom_prompt.score_invalid");
      return;
    }
    saveReview.disabled = true;
    try {
      const result = await fetchJson(
        `/api/benchmarks/suite-runs/${encodeURIComponent(state.benchmark.result.runId)}/results/${encodeURIComponent(reviewSection.dataset.testRunId)}/review`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            score,
            notes: reviewSection.querySelector("[data-benchmark-review-notes]").value || null
          })
        }
      );
      state.benchmark.result = result;
      state.benchmark.scoringProjection = null;
      renderBenchmarkResult(result);
      await refreshBenchmarkHistory({ selectRunId: result.runId });
      elements.benchmarkStatus.textContent = t("benchmark.custom_prompt.review_saved");
    } catch (error) {
      reviewSection.querySelector(".benchmark-review-status").textContent = benchmarkErrorMessage(error);
      saveReview.disabled = false;
    }
    return;
  }
  const rerun = event.target.closest("[data-benchmark-rerun]");
  if (rerun) {
    rerun.disabled = true;
    try {
      const source = state.benchmark.result;
      const rerunGpu = source.configuration?.gpu ?? state.benchmark.catalog.defaultGpu;
      const started = await fetchJson(
        `/api/benchmarks/suite-runs/${encodeURIComponent(source.runId)}/rerun`,
        { method: "POST" }
      );
      state.benchmark.selectedDefaultGpu = rerunGpu;
      replaceOptions(
        elements.benchmarkDefaultGpu,
        gpuOptions(false, rerunGpu),
        rerunGpu
      );
      elements.benchmarkDefaultGpu.title =
        elements.benchmarkDefaultGpu.selectedOptions[0]?.textContent ?? rerunGpu;
      state.activeBenchmarkRunId = started.runId;
      sessionStorage.setItem(benchmarkLiveRunStorageKey, started.runId);
      initializeBenchmarkLive(
        started.runId,
        source.selectedModels ?? [source.model],
        source.selectedHarnesses ?? [],
        null
      );
      setBenchmarkRunning(true);
      showBenchmarkTab("execution");
      connectBenchmarkEvents(started.runId, 0, started.eventsUrl);
      elements.benchmarkStatus.textContent = t("benchmark.custom_prompt.rerunning");
    } catch (error) {
      rerun.disabled = false;
      elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
    }
    return;
  }
  const section = event.target.closest(".benchmark-workspace-review");
  if (!section) {
    return;
  }
  const view = event.target.closest("[data-benchmark-view-workspace]");
  if (view) {
    view.disabled = true;
    try {
      await fetchJson(
        `/api/benchmarks/suite-runs/${encodeURIComponent(section.dataset.runId)}/workspaces/${encodeURIComponent(section.dataset.workspaceId)}/open-folder`,
        { method: "POST" }
      );
      section.querySelector(".benchmark-workspace-status").textContent = "Folder opened in Explorer.";
    } catch (error) {
      section.querySelector(".benchmark-workspace-status").textContent = benchmarkErrorMessage(error);
    } finally {
      view.disabled = section.dataset.available !== "true";
    }
    return;
  }
  const remove = event.target.closest("[data-benchmark-delete-workspace]");
  if (!remove || !await showAppConfirm(
    "Delete this retained benchmark workspace? The saved benchmark result will remain available.",
    { title: "Delete retained workspace", confirmLabel: "Delete workspace", tone: "danger" }
  )) {
    return;
  }
  remove.disabled = true;
  try {
    await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(section.dataset.runId)}/workspaces/${encodeURIComponent(section.dataset.workspaceId)}?confirmed=true`,
      { method: "DELETE" }
    );
    section.dataset.available = "false";
    section.querySelector(".benchmark-workspace-status").textContent = "Workspace deleted. The benchmark result was preserved.";
  } catch (error) {
    remove.disabled = false;
    section.querySelector(".benchmark-workspace-status").textContent = benchmarkErrorMessage(error);
  }
}

function benchmarkMetric(value) {
  return value === null || value === undefined ? "Unavailable" : `${value}%`;
}

function benchmarkHarnessLabel(harnessId) {
  return state.benchmark?.catalog?.harnesses?.find(
    item => item.definition.id === harnessId
  )?.definition.displayName
    ?? state.harnesses?.find(item => item.definition.id === harnessId)
      ?.definition.displayName
    ?? harnessId;
}

function formatBenchmarkDuration(milliseconds) {
  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }
  return `${(milliseconds / 1000).toFixed(2)} s`;
}

function benchmarkErrorMessage(error) {
  const errors = error.payload?.errors;
  const first = errors
    ? Object.values(errors).flat()[0]
    : null;
  return first ?? error.message;
}

function renderRecoveryState() {
  const recovery = state.recovery;
  elements.safeModeBanner.hidden = !recovery?.safeMode;
  document.body.dataset.historyAutoload =
    recovery?.historyAutoLoadDisabled ? "disabled" : "enabled";
  elements.safeModeReason.textContent = recovery?.reason
    ?? "Execute, cloud, and configuration changes are disabled.";

  if (!recovery?.safeMode) {
    return;
  }

  document.querySelector("[data-mode=\"execute\"]").disabled = true;
  elements.saveSettings.disabled = true;
  elements.importSettingsYaml.disabled = true;
  elements.messageInput.disabled = true;
  elements.sendButton.disabled = true;
}

function updateProviderStatus(response) {
  elements.providerBadge.textContent = response.available ? "Online" : "Unavailable";
  elements.providerBadge.className = `badge ${response.available ? "success" : "error"}`;
  elements.runtimeDetails.dataset.provider = response.available ? "online" : "offline";
}

function updateDeviceStatus() {}

function renderWorkspace() {
  const active = activeWorkspaceProfile();
  const workspace = state.workspace;
  const valid = Boolean(workspace?.valid);
  elements.workspacePath.textContent = active
    ? `${active.name} · ${active.path}`
    : "No folder selected";
  renderProjectSidebar();
  elements.workspaceValidation.textContent = workspace?.diagnostic
    ?? workspace?.status
    ?? "Not configured";
  elements.workspaceValidation.className =
    `workspace-validation ${valid ? "valid" : workspace?.configured ? "invalid" : ""}`;
  elements.trustedWorkspacePath.value = workspace?.path ?? "";
  elements.workspaceProfileName.value = "";
  elements.clearWorkspace.disabled = !workspace?.configured;
  elements.renameWorkspace.disabled = !workspace?.configured;
  elements.settingsOpenWorkspace.textContent = active
    ? "Open project settings"
    : "Add project";
  elements.workspaceHistoryEnabled.checked = Boolean(active?.historyEnabled);
}

function activeWorkspaceProfile() {
  return state.workspaceProfiles?.profiles?.find(profile => profile.active) ?? null;
}

