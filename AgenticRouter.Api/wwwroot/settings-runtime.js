async function refreshProviderHealth() {
  elements.refreshProviderHealth.disabled = true;

  try {
    state.providerHealth = await fetchJson("/api/provider-health");
    renderProviderHealth();
  } finally {
    elements.refreshProviderHealth.disabled = false;
  }
}

function providerHealthStateLabel(value) {
  return {
    healthy: "Healthy",
    degraded: "Degraded",
    unavailable: "Unavailable",
    "not-configured": "Not configured",
    unknown: "Unknown"
  }[value] ?? value;
}

function formatProviderHealthDate(value) {
  return value
    ? new Date(value).toLocaleString(window.AgenticRouterI18n.locale)
    : "not observed yet";
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
    elements.runtimeCompactMeters.textContent = "Resources unavailable";
    elements.runtimeModelList.replaceChildren(
      diagnosticRow("Memory telemetry", error.message)
    );
  }

  await refreshUsage();
}

async function refreshUsage() {
  try {
    const active = activeWorkspaceProfile();
    const query = active?.id
      ? `?workspaceId=${encodeURIComponent(active.id)}`
      : "";
    [
      state.usageOverview,
      state.cloudUsageDashboard
    ] = await Promise.all([
      fetchJson(`/api/usage/overview${query}`),
      fetchJson("/api/usage/cloud-dashboard")
    ]);
    renderUsageSummary();
    renderCloudUsage();
  } catch (error) {
    elements.settingsUsageAccuracy.textContent = "unavailable";
    elements.settingsUsageDetails.textContent =
      `Usage unavailable · ${error.message}`;
    elements.cloudUsageBadge.textContent = "unavailable";
    elements.cloudUsageSummary.textContent = "Cloud usage unavailable";
    elements.cloudUsageDetail.textContent = error.message;
  }
}

function renderUsageSummary() {
  const overview = state.usageOverview;

  if (!overview) {
    elements.settingsUsageAccuracy.textContent = "no data";
    elements.settingsUsageDetails.textContent = "Usage is not available yet.";
    return;
  }

  const usage = overview.selected;
  const accuracy = usage.accuracy === "exact"
    ? "exact"
    : usage.accuracy === "mixed"
      ? "mixed"
      : usage.accuracy === "estimated"
        ? "estimated"
        : "no data";
  const lastUpdate = usage.lastUpdatedAt
    ? new Date(usage.lastUpdatedAt).toLocaleString(window.AgenticRouterI18n.locale)
    : "no recorded calls";
  const topModels = usage.topModels.length
    ? usage.topModels.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "No model in this period.";
  const topRoles = usage.topRoles.length
    ? usage.topRoles.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "No role in this period.";
  const pinnedWindows = overview.pinned.length
    ? overview.pinned.map(
      item => `${item.window.id}: ${formatInteger(item.totalTokens)} tokens`
    ).join("\n")
    : "No pinned window.";
  const local = usage.providerBreakdown
    .filter(item => item.key === "ollama-local")
    .reduce((total, item) => total + item.totalTokens, 0);
  const cloud = usage.providerBreakdown
    .filter(item => item.key !== "ollama-local")
    .reduce((total, item) => total + item.totalTokens, 0);
  const comparison =
    `${overview.comparisonProvider} · ${overview.comparisonModel}`;
  const comparisonPrice = state.pricingCatalog?.comparisons.find(
    item => item.providerId === overview.comparisonProvider
      && item.modelId === overview.comparisonModel
  );
  const plan = state.pricingCatalog?.ollamaPlans.find(
    item => item.plan === state.settings?.usage.ollamaPlanReference
  );
  const comparisonDetails = comparisonPrice
    ? `${formatCurrency(comparisonPrice.inputPricePerMillion)}/M input · `
      + `${formatCurrency(comparisonPrice.outputPricePerMillion)}/M output · `
      + `catalog ${comparisonPrice.catalogVersion} · `
      + `updated ${new Date(comparisonPrice.updatedAt).toLocaleDateString(window.AgenticRouterI18n.locale)} · `
      + `${comparisonPrice.stale ? "stale" : "current"}\n`
      + `Comparison source: ${comparisonPrice.officialSourceUrl}`
    : "comparison price unavailable";
  const planDetails = plan
    ? `${formatCurrency(plan.monthlyPrice)}/month · ${plan.usageDescription}\n`
      + `${plan.tokenEquivalent}\n`
      + `${plan.availability ? `${plan.availability}\n` : ""}`
      + `Effective date: ${plan.effectiveDate} · `
      + `${plan.stale ? "stale reference" : "current reference"}\n`
      + `Official source: ${plan.officialSourceUrl}`
    : "reference unavailable";
  elements.settingsUsageAccuracy.textContent = accuracy;
  elements.settingsUsageSummary.dataset.accuracy = usage.accuracy;
  elements.settingsUsageDetails.textContent =
    `Window: ${usage.window.id}\n`
    + `Input / output / total: ${formatInteger(usage.inputTokens)} / `
    + `${formatInteger(usage.outputTokens)} / ${formatInteger(usage.totalTokens)}\n`
    + `Calls: ${usage.requests} · Success: ${usage.successes} · `
    + `Failures: ${usage.failures} · Cancellations: ${usage.cancellations}\n`
    + `Local / cloud: ${formatInteger(local)} / ${formatInteger(cloud)} tokens\n`
    + `Estimated provider cost: ${formatCurrency(usage.estimatedActualCost)}\n`
    + `Equivalent cloud estimate: ${formatCurrency(usage.equivalentCloudCost)} `
    + `against ${comparison}\n`
    + `Comparison rates: ${comparisonDetails}\n`
    + `This is an equivalent comparison, not an exact Ollama Cloud saving.\n`
    + `Top models:\n${topModels}\n`
    + `Top roles:\n${topRoles}\n`
    + `Pinned windows:\n${pinnedWindows}\n`
    + `Ollama plan reference: ${plan?.plan ?? "unavailable"}\n`
    + `${planDetails}\n`
    + `Last update: ${lastUpdate}`;
}

function renderCloudUsage() {
  const dashboard = state.cloudUsageDashboard;
  const active = parseModelReference(
    state.activeAgentModel
      ?? elements.modelSelector.value
  );
  const activeProvider = active.provider === "ollama-local"
    ? null
    : dashboard?.providers.find(
      provider => provider.providerId === active.provider
    );

  delete elements.cloudUsageCard.dataset.alert;

  if (!dashboard || dashboard.providers.length === 0) {
    elements.cloudUsageBadge.textContent = "not configured";
    elements.cloudUsageSummary.textContent = "Cloud usage";
    elements.cloudUsageDetail.textContent = "Not configured";
  } else if (activeProvider) {
    const accuracy = usageAccuracyLabel(activeProvider.accuracy);
    elements.cloudUsageBadge.textContent = activeProvider.percentage === null
      ? accuracy
      : `${formatPercentage(activeProvider.percentage)} · ${accuracy}`;
    elements.cloudUsageSummary.textContent =
      `${activeProvider.displayName} · ${active.model}`;
    elements.cloudUsageDetail.textContent =
      `${state.activeAgentRole ?? "primary"} · ${activeProvider.window}`;

    if (activeProvider.alertThreshold !== null) {
      elements.cloudUsageCard.dataset.alert =
        String(activeProvider.alertThreshold);
    }
  } else if (dashboard.connectedProviderCount > 0) {
    elements.cloudUsageBadge.textContent = "inactive";
    elements.cloudUsageSummary.textContent =
      `${dashboard.connectedProviderCount} connected provider(s)`;
    elements.cloudUsageDetail.textContent = "No active cloud model";
  } else {
    elements.cloudUsageBadge.textContent = "disconnected";
    elements.cloudUsageSummary.textContent =
      `${dashboard.providers.length} configured provider(s)`;
    elements.cloudUsageDetail.textContent = "No active cloud model";
  }

  elements.cloudUsageDashboardSummary.textContent = dashboard
    ? `Selected window: ${dashboard.selectedWindow}\n`
      + `Connected providers: ${dashboard.connectedProviderCount}\n`
      + `Local alerts: ${dashboard.alertThresholds.join("%, ")}%\n`
      + `Updated: ${new Date(dashboard.generatedAt).toLocaleString(window.AgenticRouterI18n.locale)}`
    : "Dashboard is not available yet.";
  elements.cloudUsageProviderCards.replaceChildren();

  for (const provider of dashboard?.providers ?? []) {
    elements.cloudUsageProviderCards.append(
      createCloudUsageProviderCard(provider)
    );
  }
}

function createCloudUsageProviderCard(provider) {
  const card = document.createElement("article");
  card.className = "cloud-usage-provider-card";

  if (provider.alertThreshold !== null) {
    card.dataset.alert = String(provider.alertThreshold);
  }

  const heading = document.createElement("div");
  heading.className = "cloud-usage-provider-heading";
  const headingText = document.createElement("div");
  const title = document.createElement("h3");
  title.textContent = provider.displayName;
  const connection = document.createElement("small");
  connection.textContent =
    `${cloudConnectionLabel(provider.connectionState)} · `
    + `${billingModeLabel(provider.expectedBillingMode)}`;
  headingText.append(title, connection);
  const quota = document.createElement("span");
  quota.className = `usage-accuracy-badge${provider.alertThreshold !== null
    ? " warning"
    : ""}`;
  quota.textContent = provider.percentage === null
    ? usageAccuracyLabel(provider.accuracy)
    : `${formatPercentage(provider.percentage)} · `
      + usageAccuracyLabel(provider.accuracy);
  heading.append(headingText, quota);

  const metrics = document.createElement("div");
  metrics.className = "cloud-usage-metrics";
  metrics.append(
    cloudUsageMetric("Tokens", formatInteger(provider.totalTokens)),
    cloudUsageMetric("Requests", formatInteger(provider.requests)),
    cloudUsageMetric(
      "Estimated cost",
      formatCurrency(provider.estimatedActualCost)
    ),
    cloudUsageMetric(
      "Last call",
      provider.latestRequestAt
        ? new Date(provider.latestRequestAt).toLocaleString(window.AgenticRouterI18n.locale)
        : "none"
    )
  );

  const quotaDetail = document.createElement("small");
  quotaDetail.textContent =
    `Quota: ${provider.quotaSource} · ${provider.window}`
    + `${provider.resetAt
      ? ` · reset ${new Date(provider.resetAt).toLocaleString(window.AgenticRouterI18n.locale)}`
      : ""}`;
  const billingDetail = document.createElement("small");
  billingDetail.textContent =
    `${billingModeLabel(provider.expectedBillingMode)} is only a local expectation; `
    + "it does not guarantee billing or free usage.";
  const warning = document.createElement("small");
  warning.hidden = !provider.hasRateLimitWarning;
  warning.className = "cloud-provider-diagnostic";
  warning.textContent = "Warning: a 429 response was observed in this window.";

  const models = document.createElement("div");
  models.className = "cloud-usage-models";

  for (const model of provider.models) {
    const item = document.createElement("section");
    item.className = "cloud-usage-model";
    const modelTitle = document.createElement("h4");
    modelTitle.textContent = `${provider.displayName} · ${model.modelId}`;
    const details = document.createElement("small");
    details.textContent =
      `${formatInteger(model.inputTokens)} input · `
      + `${formatInteger(model.outputTokens)} output · `
      + `${formatInteger(model.requests)} call(s) · `
      + `${formatCurrency(model.estimatedActualCost)} · `
      + `${model.roles.join(", ") || "no observed role"}`;
    const capabilities = document.createElement("div");
    capabilities.className = "cloud-capability-list";

    for (const capability of model.capabilities) {
      const badge = document.createElement("span");
      badge.className = "badge muted";
      badge.textContent = capability;
      capabilities.append(badge);
    }

    item.append(modelTitle, details, capabilities);
    models.append(item);
  }

  if (provider.models.length === 0) {
    const empty = document.createElement("small");
    empty.textContent = "No cached model or observed usage.";
    models.append(empty);
  }

  card.append(
    heading,
    metrics,
    quotaDetail,
    billingDetail,
    warning,
    models
  );
  return card;
}

function cloudUsageMetric(label, value) {
  const metric = document.createElement("div");
  metric.className = "cloud-usage-metric";
  const name = document.createElement("span");
  name.textContent = label;
  const content = document.createElement("strong");
  content.textContent = value;
  metric.append(name, content);
  return metric;
}

async function openCloudUsage() {
  elements.runtimeDetails.open = false;
  elements.cloudUsageDialog.showModal();
  elements.dismissCloudUsage.focus();
  await refreshCloudUsage();
}

function closeCloudUsage() {
  if (!elements.cloudUsageDialog.open) {
    return;
  }

  elements.cloudUsageDialog.close();
  elements.cloudUsageCard.focus();
}

async function refreshCloudUsage() {
  elements.refreshCloudUsage.disabled = true;
  elements.cloudUsageRefreshStatus.textContent = "Refreshing local data…";
  elements.cloudUsageDialog.setAttribute("aria-busy", "true");

  try {
    state.cloudUsageDashboard = await fetchJson("/api/usage/cloud-dashboard");
    renderCloudUsage();
    elements.cloudUsageRefreshStatus.textContent = "Dashboard refreshed.";
  } catch (error) {
    elements.cloudUsageRefreshStatus.textContent = error.message;
  } finally {
    elements.refreshCloudUsage.disabled = false;
    elements.cloudUsageDialog.setAttribute("aria-busy", "false");
  }
}

function parseModelReference(value) {
  const normalized = value && value !== "auto"
    ? value
    : "";
  const separator = normalized.indexOf("::");
  return separator > 0
    ? {
      provider: normalized.slice(0, separator),
      model: normalized.slice(separator + 2)
    }
    : {
      provider: "ollama-local",
      model: normalized
    };
}

function usageAccuracyLabel(accuracy) {
  return {
    exact: "exact",
    estimated: "estimated",
    mixed: "mixed",
    unavailable: "unavailable"
  }[accuracy] ?? "unavailable";
}

function billingModeLabel(mode) {
  return {
    "free-tier": "Expected free tier",
    paid: "Expected paid",
    unknown: "Unknown billing"
  }[mode] ?? "Unknown billing";
}

function formatPercentage(value) {
  return `${Number(value).toLocaleString(
    window.AgenticRouterI18n.locale,
    {
      maximumFractionDigits: 2
    }
  )}%`;
}

function renderRuntimeStatus(runtime) {
  state.runtime = runtime;
  const memoryRows = [];
  const compactMeters = [];
  const ram = runtime.systemMemory;

  if (ram.status === "available") {
    compactMeters.push(
      compactRuntimeMeter("RAM", ram.usedPercent, "system")
    );
    memoryRows.push(
      memoryRow(
        "System RAM",
        ram.usedBytes,
        ram.totalBytes,
        ram.usedPercent,
        ram.diagnostic,
        "system"
      )
    );
  } else {
    compactMeters.push(compactRuntimeMeter("RAM", null, "system"));
    memoryRows.push(
      diagnosticRow(
        "System RAM",
        ram.diagnostic
      )
    );
  }

  for (const device of runtime.devices) {
    compactMeters.push(
      compactRuntimeMeter(
        compactDeviceName(device.name),
        device.usedDedicatedMemoryBytes == null ? null : device.usedPercent,
        "gpu"
      )
    );
    memoryRows.push(
      device.usedDedicatedMemoryBytes == null
        ? diagnosticRow(
          device.name,
          device.diagnostic ?? `Dedicated total: ${formatGiB(device.totalDedicatedMemoryBytes)}`,
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
        "Graphics devices",
        runtime.devicesDiagnostic
      )
    );
  }

  elements.runtimeCompactMeters.replaceChildren(...compactMeters);
  elements.runtimeSummary.title = runtime.warnings.join("\n");
  elements.runtimeMemoryList.replaceChildren(...memoryRows);
  renderLoadedModels(runtime);
}

function compactRuntimeMeter(label, percent, kind) {
  const indicator = document.createElement("span");
  indicator.className = `runtime-compact-indicator ${kind}`;
  const name = document.createElement("span");
  name.className = "runtime-compact-label";
  name.textContent = label;
  const meter = document.createElement("span");
  meter.className = "runtime-compact-meter";
  const fill = document.createElement("span");
  const normalized = percent == null
    ? 0
    : Math.max(0, Math.min(100, percent));
  fill.style.width = `${normalized}%`;
  fill.className = normalized >= 90
    ? "critical"
    : normalized >= 75
      ? "warning"
      : "";
  meter.append(fill);
  const value = document.createElement("span");
  value.className = "runtime-compact-value";
  value.textContent = formatPercent(percent);
  indicator.setAttribute(
    "aria-label",
    `${label}: ${formatPercent(percent)}`
  );
  indicator.append(name, meter, value);
  return indicator;
}

function compactDeviceName(name) {
  const match = name.match(/(?:RTX|GTX|RX)\s*(\d{3,4}(?:\s*XT[X]?)?)/i);
  return match?.[1] ?? name.replace(/^NVIDIA\s+/i, "");
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
  value.textContent = diagnostic ?? "Unavailable";
  row.append(label, value);
  return row;
}

function renderLoadedModels(runtime) {
  const availableDevices = runtime.devices.filter(
    device => device.usedDedicatedMemoryBytes != null
      && device.totalDedicatedMemoryBytes > 0
  );
  const allocatedModelVram = runtime.loadedModels.reduce(
    (total, model) => total + Number(model.vramSizeBytes ?? 0),
    0
  );
  const totalGpuMemory = availableDevices.reduce(
    (total, device) => total + device.totalDedicatedMemoryBytes,
    0
  );
  elements.runtimeModelSummary.textContent = totalGpuMemory > 0
    ? t("memory.model_gpu_summary", {
      allocated: formatGiB(allocatedModelVram),
      capacity: formatGiB(totalGpuMemory)
    })
    : t("memory.gpu_memory_unavailable");

  const groups = new Map();
  for (const model of runtime.loadedModels) {
    const identity = loadedModelGpuIdentity(model);
    if (!groups.has(identity.key)) {
      groups.set(identity.key, {
        ...identity,
        models: []
      });
    }
    groups.get(identity.key).models.push(model);
  }
  for (const device of runtime.devices) {
    const key = `device-${device.id}`;
    const label = runtimeDeviceLabel(device);
    if (!groups.has(key)) {
      groups.set(key, {
        key,
        deviceId: device.id,
        gpuIndex: device.backendIndex == null
          ? null
          : Number(device.backendIndex),
        label,
        order: runtimeDeviceOrder(device),
        models: []
      });
    } else {
      const existing = groups.get(key);
      Object.assign(existing, {
        deviceId: device.id,
        gpuIndex: existing.gpuIndex ?? (device.backendIndex == null
          ? null
          : Number(device.backendIndex)),
        label: existing.models.length > 0
          ? existing.label
          : label,
        order: runtimeDeviceOrder(device)
      });
    }
  }

  if (groups.size === 0) {
    elements.runtimeModelList.replaceChildren(
      diagnosticRow(
        runtime.loadedModelsStatus === "unavailable"
          ? "Ollama telemetry"
          : "No reported model",
        runtime.loadedModelsDiagnostic
          ?? "Ollama did not report loaded models in /api/ps."
      )
    );
    return;
  }

  const orderedGroups = [...groups.values()].sort(
    (left, right) => left.order - right.order
  );
  const grid = document.createElement("div");
  grid.className = "loaded-model-gpu-grid";
  grid.append(
    ...orderedGroups.map(
      group => loadedModelGpuCard(
        group,
        runtime.devices,
        runtime.loadedModelsStatus
      )
    )
  );
  const summary = document.createElement("div");
  summary.className = "loaded-model-summary";
  summary.append(
    loadedModelSummaryItem(
      t("memory.system_ram_model"),
      formatGiB(runtime.loadedModels.reduce(
        (total, model) => total + Number(model.estimatedRamSizeBytes ?? 0),
        0
      ))
    ),
    loadedModelSummaryItem(
      t("memory.allocated_context_windows"),
      `${formatInteger(runtime.loadedModels.reduce(
        (total, model) => total + Number(model.actualContextTokens ?? 0),
        0
      ))} tokens`
    )
  );
  elements.runtimeModelList.replaceChildren(grid, summary);
}

function loadedModelGpuIdentity(model) {
  if (model.observedGpuId) {
    return {
      key: `device-${model.observedGpuId}`,
      deviceId: model.observedGpuId,
      gpuIndex: model.observedBackendIndex == null
        ? null
        : Number(model.observedBackendIndex),
      label: observedModelPlacementLabel(model),
      order: model.observedBackendIndex == null
        ? Number.MAX_SAFE_INTEGER - 4
        : Number(model.observedBackendIndex)
    };
  }
  if (model.processor === "cpu") {
    return {
      key: "cpu",
      deviceId: null,
      gpuIndex: null,
      label: t("memory.cpu"),
      order: Number.MAX_SAFE_INTEGER - 2
    };
  }
  if (model.processor === "gpu" || model.processor === "hybrid") {
    const backend = model.observedBackend
      ? runtimeBackendLabel(model.observedBackend)
      : null;
    return {
      key: `backend-${model.observedBackend ?? "unknown"}`,
      deviceId: null,
      gpuIndex: null,
      label: backend === "Vulkan"
        ? "Vulkan combined allocation · physical split not reported"
        : backend
          ? `${backend} model allocation · exact device not reported`
        : t("memory.gpu_unknown"),
      order: Number.MAX_SAFE_INTEGER - 1
    };
  }
  return {
    key: "unknown",
    deviceId: null,
    gpuIndex: null,
    label: t("memory.gpu_unknown"),
    order: Number.MAX_SAFE_INTEGER
  };
}

function runtimeDeviceLabel(device) {
  const backend = device.backend
    ? runtimeBackendLabel(device.backend)
    : null;
  const index = device.backendIndex == null
    ? ""
    : ` ${device.backendIndex}`;
  return `${backend ? `${backend}${index} · ` : ""}${device.name}`;
}

function runtimeDeviceOrder(device) {
  const backendOrder = {
    cuda: 0,
    rocm: 1,
    vulkan: 2
  }[device.backend] ?? 3;
  return backendOrder * 1000 + Number(device.backendIndex ?? 999);
}

function runtimeBackendLabel(backend) {
  return {
    cuda: "CUDA",
    rocm: "ROCm",
    vulkan: "Vulkan"
  }[backend] ?? backend;
}

function observedModelPlacementLabel(model) {
  const backend = model.observedBackend
    ? runtimeBackendLabel(model.observedBackend)
    : "GPU";
  const index = model.observedBackendIndex == null
    ? ""
    : ` ${model.observedBackendIndex}`;
  return `${backend}${index}${model.gpuName ? ` · ${model.gpuName}` : ""}`;
}

function loadedModelGpuCard(group, devices, loadedModelsStatus) {
  const card = document.createElement("article");
  card.className = "loaded-model-gpu-card";
  card.dataset.deviceId = group.deviceId ?? group.key;
  const header = document.createElement("header");
  const title = document.createElement("strong");
  title.textContent = group.label;
  const details = document.createElement("details");
  details.className = "loaded-model-details";
  const detailsSummary = document.createElement("summary");
  detailsSummary.textContent = t("memory.details");
  const detailsContent = document.createElement("div");
  detailsContent.className = "loaded-model-details-content";
  detailsContent.append(
    ...(group.models.length > 0
      ? group.models.map(model => loadedModelDetailRow(model))
      : [loadedModelEmptyDetail()])
  );
  details.append(detailsSummary, detailsContent);
  header.append(title, details);

  const modelVramValues = group.models
    .map(model => model.vramSizeBytes)
    .filter(value => value != null);
  const modelVram = modelVramValues.reduce(
    (total, value) => total + Number(value),
    0
  );
  const device = group.deviceId == null
    ? null
    : devices.find(item => item.id === group.deviceId);
  const modelTelemetryAvailable = loadedModelsStatus === "available";
  const modelVramKnown = modelTelemetryAvailable
    && modelVramValues.length === group.models.length;
  const adapterVram = device?.usedDedicatedMemoryBytes == null
    ? null
    : Number(device.usedDedicatedMemoryBytes);
  const contextRuntimeValues = group.models
    .map(model => estimatedContextRuntimeBytes(model))
    .filter(value => value != null);
  const contextRuntimeBytes = group.models.length === 0 && modelTelemetryAvailable
    ? 0
    : contextRuntimeValues.length === group.models.length
      ? contextRuntimeValues.reduce((total, value) => total + value, 0)
      : null;
  const requestedContextValues = group.models
    .map(model => model.requestedContextTokens)
    .filter(value => value != null);
  const requestedContextKnown = group.models.length > 0
    && requestedContextValues.length === group.models.length;
  const requestedContextTokens = requestedContextValues.reduce(
    (total, value) => total + Number(value),
    0
  );
  const metrics = document.createElement("div");
  metrics.className = "loaded-model-metrics";
  metrics.append(
    loadedModelMetric(
      "model",
      t("memory.model_vram_used"),
      modelVramKnown ? formatGiB(modelVram) : "n/d",
      t("memory.model_vram_note")
    ),
    loadedModelMetric(
      "system",
      t("memory.adapter_vram_used"),
      adapterVram == null ? "n/d" : formatGiB(adapterVram),
      t("memory.adapter_vram_note")
    ),
    loadedModelMetric(
      "context",
      t("memory.requested_context_window"),
      requestedContextKnown
        ? `${formatInteger(requestedContextTokens)} tokens`
        : "n/d",
      t("memory.requested_context_note")
    ),
    loadedModelMetric(
      "memory",
      t("memory.context_runtime"),
      contextRuntimeBytes == null ? "n/d" : `~${formatGiB(contextRuntimeBytes)}`,
      t("memory.context_runtime_note")
    )
  );
  card.append(header, metrics);
  return card;
}

function loadedModelMetric(kind, labelText, valueText, diagnostic = null) {
  const row = document.createElement("div");
  row.className = "loaded-model-metric";
  if (diagnostic) {
    row.title = diagnostic;
  }
  const label = document.createElement("span");
  const icon = document.createElement("span");
  icon.className = `loaded-model-metric-icon ${kind}`;
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = {
    model: "▦",
    system: "⚙",
    context: "●",
    memory: "◫"
  }[kind];
  label.append(icon, document.createTextNode(labelText));
  const value = document.createElement("strong");
  value.textContent = valueText;
  row.append(label, value);
  return row;
}

function loadedModelDetailRow(model) {
  const row = document.createElement("div");
  row.className = "loaded-model-detail-row";
  const name = document.createElement("strong");
  name.textContent = model.name;
  const allocation = document.createElement("span");
  const contextRuntimeBytes = estimatedContextRuntimeBytes(model);
  const requestedContext = model.requestedContextTokens == null
    ? ""
    : ` · ${formatInteger(model.requestedContextTokens)} requested`;
  allocation.textContent = `${formatGiB(model.vramSizeBytes)} VRAM · `
    + `${formatGiB(model.estimatedRamSizeBytes)} RAM · `
    + `${formatInteger(model.actualContextTokens)}-token allocated context`
    + `${requestedContext} · `
    + `${contextRuntimeBytes == null ? "n/d" : `~${formatGiB(contextRuntimeBytes)}`} context/runtime`;
  const placement = document.createElement("span");
  placement.className = "loaded-model-placement";
  placement.textContent = `Observed: ${observedModelPlacementLabel(model)} · `
    + `Configured: ${configuredModelPlacementLabel(model)}`;
  placement.title = model.placementDiagnostic ?? "";
  row.append(name, allocation, placement);
  return row;
}

function configuredModelPlacementLabel(model) {
  if (model.configuredGpu === "auto") {
    return "Auto";
  }
  if (model.configuredGpu === "vulkan:all") {
    return "Vulkan combined · all GPUs";
  }
  if (String(model.configuredGpu).startsWith("vulkan:prefer:")) {
    const [, , preferredBackend, preferredIndex] = model.configuredGpu.split(":");
    const preferredDevice = state.devices.find(
      device => device.backend === preferredBackend
        && Number(device.backendIndex) === Number(preferredIndex)
    );
    return `Vulkan combined · prioritize ${preferredDevice?.name
      ?? `${runtimeBackendLabel(preferredBackend)} ${preferredIndex}`}`;
  }
  const [selectionBackend, selectionIndex] = String(
    model.configuredGpu ?? "auto"
  ).split(":");
  const backend = selectionBackend === "ollama"
    ? "cuda"
    : selectionBackend;
  if (model.configuredGpuIndex == null && selectionIndex == null) {
    return "Auto";
  }
  return `${runtimeBackendLabel(backend)} ${model.configuredGpuIndex ?? selectionIndex}`
    + `${model.configuredGpuName ? ` · ${model.configuredGpuName}` : ""}`;
}

function loadedModelEmptyDetail() {
  const row = document.createElement("div");
  row.className = "loaded-model-detail-row";
  const message = document.createElement("span");
  message.textContent = t("memory.no_loaded_model");
  row.append(message);
  return row;
}

function estimatedContextRuntimeBytes(model) {
  if (model.totalSizeBytes == null) {
    return null;
  }
  const installed = state.models.find(
    candidate => candidate.provider === "ollama-local"
      && candidate.name === model.name
  );
  return installed?.sizeBytes == null
    ? null
    : Math.max(0, Number(model.totalSizeBytes) - Number(installed.sizeBytes));
}

function loadedModelSummaryItem(labelText, valueText) {
  const item = document.createElement("div");
  const label = document.createElement("span");
  label.textContent = labelText;
  const value = document.createElement("strong");
  value.textContent = valueText;
  item.append(label, value);
  return item;
}

function formatInteger(value) {
  return new Intl.NumberFormat(window.AgenticRouterI18n.locale).format(Number(value ?? 0));
}

function formatCurrency(value) {
  const number = Number(value ?? 0);
  const digits = Math.abs(number) > 0 && Math.abs(number) < 0.01
    ? 6
    : 2;
  return new Intl.NumberFormat(
    window.AgenticRouterI18n.locale,
    {
      style: "currency",
      currency: "USD",
      minimumFractionDigits: digits,
      maximumFractionDigits: digits
    }
  ).format(number);
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

function scheduleExternalAvailabilityRefresh() {
  clearTimeout(state.externalAvailabilityTimer);
  state.externalAvailabilityTimer = null;

  if (document.hidden || !state.settings) {
    return;
  }

  state.externalAvailabilityTimer = window.setTimeout(
    async () => {
      state.externalAvailabilityTimer = null;
      try {
        await refreshExternalAvailability();
      } finally {
        scheduleExternalAvailabilityRefresh();
      }
    },
    60_000
  );
}

async function refreshExternalAvailability() {
  if (document.hidden || !state.settings) {
    return;
  }
  if (state.externalAvailabilityRefresh) {
    return state.externalAvailabilityRefresh;
  }

  state.externalAvailabilityRefresh = (async () => {
    const activeWorkspace = activeWorkspaceProfile();
    const checks = await Promise.allSettled([
      fetchJson("/api/setup/status"),
      fetchJson("/api/knowledge-providers"),
      fetchJson("/api/runtime/profiles"),
      fetchJson("/api/provider-health"),
      fetchJson("/api/web-search"),
      activeWorkspace ? fetchJson("/api/git") : Promise.resolve(null)
    ]);
    const value = index => checks[index].status === "fulfilled"
      ? checks[index].value
      : null;
    const setup = value(0);

    if (setup) {
      state.setup = setup;
      state.harnesses = setup.harnesses.map(harness => ({
        definition: harness.definition,
        availability: harness.availability
      }));
      state.devices = setup.devices;
      updateProviderStatus(setup.ollama);
    }
    if (value(1)) {
      state.knowledgeProviders = value(1);
      state.knowledgeProviderLoadError = null;
    } else if (checks[1].status === "rejected") {
      state.knowledgeProviderLoadError = checks[1].reason?.message
        ?? "Knowledge provider status could not be loaded.";
    }
    state.runtimeProfiles = value(2) ?? state.runtimeProfiles;
    state.providerHealth = value(3) ?? state.providerHealth;
    state.webSearch = value(4) ?? state.webSearch;
    if (activeWorkspace && value(5)) {
      state.git = value(5);
    }
    renderExternalAppWarnings();
  })();

  try {
    await state.externalAvailabilityRefresh;
  } finally {
    state.externalAvailabilityRefresh = null;
  }
}

function refreshExternalAvailabilityNow() {
  clearTimeout(state.externalAvailabilityTimer);
  state.externalAvailabilityTimer = null;
  void refreshExternalAvailability().finally(
    scheduleExternalAvailabilityRefresh
  );
}

function handleVisibilityChange() {
  if (document.hidden) {
    clearTimeout(state.runtimeTimer);
    clearTimeout(state.externalAvailabilityTimer);
    state.externalAvailabilityTimer = null;
  } else {
    refreshRuntimeStatus();
    void refreshGit();
    scheduleRuntimeRefresh();
    refreshExternalAvailabilityNow();
  }
}

function handleWindowFocus() {
  if (!document.hidden) {
    refreshExternalAvailabilityNow();
  }
}

const runtimeRoleLabels = {
  specialist: "Specialist",
  primary: "Primary",
  fallback: "Fallback",
  benchmark: "Benchmark",
  modelTest: "Model test",
  webSearchSynthesis: "Web search synthesis",
  visionRequest: "Vision request"
};

function renderRuntimeProfilesEditor() {
  const runtime = state.settings?.ollamaRuntime;

  if (!runtime || !elements.runtimeRoleProfiles) {
    return;
  }

  const profiles = [];

  for (const [role, profile] of Object.entries(runtime.roleDefaults)) {
    if (!(role in runtimeRoleLabels)) {
      continue;
    }
    const card = document.createElement("article");
    card.className = "runtime-role-profile";
    card.dataset.role = role;
    const title = document.createElement("strong");
    title.textContent = runtimeRoleLabels[role] ?? role;
    const fields = document.createElement("div");
    fields.className = "runtime-profile-fields";

    for (const [label, field] of [
      ["Min.", "minimumContextTokens"],
      ["Target", "targetContextTokens"],
      ["Max.", "maximumContextTokens"],
      ["Output", "outputTokenLimit"],
      ["Keep-alive", "keepAlive"]
    ]) {
      const fieldLabel = document.createElement("label");
      const caption = document.createElement("span");
      caption.textContent = label;
      const input = document.createElement("input");
      input.type = "number";
      input.min = field === "keepAlive" ? "-1" : "128";
      input.max = field === "keepAlive" ? "86400" : "131072";
      input.value = profile[field];
      input.dataset.runtimeRole = role;
      input.dataset.runtimeField = field;
      fieldLabel.append(caption, input);
      fields.append(fieldLabel);
    }

    card.append(title, fields);
    profiles.push(card);
  }

  elements.runtimeRoleProfiles.replaceChildren(...profiles);
  elements.runtimeMemoryGpuPercent.value =
    runtime.memory.targetMaximumGpuUsagePercent;
  elements.runtimeMemoryFreeVram.value =
    bytesToGiB(runtime.memory.minimumFreeVramBytes);
  elements.runtimeMemoryFreeRam.value =
    bytesToGiB(runtime.memory.minimumFreeSystemRamBytes);
  elements.runtimeMemoryCpuOffload.checked = runtime.memory.allowCpuOffload;
  elements.runtimeMemoryPreferFullGpu.checked =
    runtime.memory.preferFullGpuForActivePrimary;
  renderRuntimeDevicePolicies(
    runtime.memory
  );

  const localModels = state.models
    .filter(model => model.provider === "ollama-local")
    .map(model => ({
      value: model.name,
      label: `${model.displayName ?? model.name} · ${shortDigest(model.digest)}`,
      title: model.digest ?? "digest unavailable"
    }));
  replaceOptions(
    elements.runtimeOverrideModel,
    localModels,
    elements.runtimeOverrideModel.value
      || localModels[0]?.value
      || ""
  );
  replaceOptions(
    elements.runtimeOverrideRole,
    Object.keys(runtimeRoleLabels).map(role => ({
      value: role,
      label: runtimeRoleLabels[role]
    })),
    elements.runtimeOverrideRole.value || "specialist"
  );
  loadRuntimeOverrideEditor();
  renderRuntimeProfileEvidence();
}

function loadRuntimeOverrideEditor() {
  const runtime = state.settings?.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;
  const saved = runtime?.modelOverrides.find(
    candidate => candidate.provider === "ollama-local"
      && candidate.model === model?.name
      && candidate.digest === model?.digest
  )?.overrides?.[role];
  const profile = saved ?? runtime?.roleDefaults?.[role];

  if (!profile) {
    return;
  }

  elements.runtimeOverrideMinimum.value = profile.minimumContextTokens;
  elements.runtimeOverrideTarget.value = profile.targetContextTokens;
  elements.runtimeOverrideMaximum.value = profile.maximumContextTokens;
  elements.runtimeOverrideOutput.value = profile.outputTokenLimit;
  elements.runtimeOverrideKeepAlive.value = profile.keepAlive;
  elements.removeRuntimeOverride.disabled = !saved;
}

function renderRuntimeDevicePolicies(memory) {
  const cards = state.devices
    .filter(device => !device.isAuto)
    .map(device => {
      const card = document.createElement("article");
      card.className = "runtime-device-policy";
      const enabledLabel = document.createElement("label");
      enabledLabel.className = "checkbox-label";
      const enabled = document.createElement("input");
      enabled.type = "checkbox";
      enabled.dataset.runtimeDeviceEnabled = device.id;
      enabled.checked = Object.hasOwn(
        memory.devices,
        device.id
      );
      const name = document.createElement("span");
      name.textContent = runtimeDeviceLabel(device);
      if (!device.affinitySelectable) {
        name.title = "Detected for monitoring; exact Ollama affinity remains Auto.";
      }
      enabledLabel.append(enabled, name);

      const fields = document.createElement("div");
      fields.className = "runtime-profile-fields runtime-device-policy-fields";
      const policy = memory.devices[device.id] ?? {
        targetMaximumUsagePercent: memory.targetMaximumGpuUsagePercent,
        minimumFreeVramBytes: memory.minimumFreeVramBytes
      };
      const percentLabel = document.createElement("label");
      const percentCaption = document.createElement("span");
      percentCaption.textContent = "Maximum usage (%)";
      const percent = document.createElement("input");
      percent.type = "number";
      percent.min = "50";
      percent.max = "100";
      percent.value = policy.targetMaximumUsagePercent;
      percent.dataset.runtimeDevicePercent = device.id;
      percent.disabled = !enabled.checked;
      percentLabel.append(percentCaption, percent);
      const freeLabel = document.createElement("label");
      const freeCaption = document.createElement("span");
      freeCaption.textContent = "Free VRAM (GiB)";
      const free = document.createElement("input");
      free.type = "number";
      free.min = "0";
      free.step = "0.25";
      free.value = bytesToGiB(policy.minimumFreeVramBytes);
      free.dataset.runtimeDeviceFreeVram = device.id;
      free.disabled = !enabled.checked;
      freeLabel.append(freeCaption, free);
      fields.append(percentLabel, freeLabel);
      card.append(enabledLabel, fields);
      return card;
    });

  if (cards.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "No specific GPU was detected.";
    cards.push(empty);
  }

  elements.runtimeMemoryDevicePolicies.replaceChildren(...cards);
}

function handleRuntimeDevicePolicyChange(event) {
  const deviceId = event.target.dataset.runtimeDeviceEnabled;

  if (!deviceId) {
    return;
  }

  const enabled = event.target.checked;
  elements.runtimeMemoryDevicePolicies.querySelector(
    `[data-runtime-device-percent="${CSS.escape(deviceId)}"]`
  ).disabled = !enabled;
  elements.runtimeMemoryDevicePolicies.querySelector(
    `[data-runtime-device-free-vram="${CSS.escape(deviceId)}"]`
  ).disabled = !enabled;
}

function saveRuntimeOverrideDraft() {
  const runtime = state.settings.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;

  if (!model?.digest) {
    elements.runtimeProfileResult.textContent =
      "The local model must have an exact digest to receive an override.";
    return;
  }

  const profile = {
    minimumContextTokens: Number(elements.runtimeOverrideMinimum.value),
    targetContextTokens: Number(elements.runtimeOverrideTarget.value),
    maximumContextTokens: Number(elements.runtimeOverrideMaximum.value),
    outputTokenLimit: Number(elements.runtimeOverrideOutput.value),
    keepAlive: Number(elements.runtimeOverrideKeepAlive.value)
  };
  const overrides = runtime.modelOverrides.map(
    item => ({
      ...item,
      overrides: {
        ...item.overrides
      }
    })
  );
  let exact = overrides.find(
    item => item.provider === "ollama-local"
      && item.model === model.name
      && item.digest === model.digest
  );

  if (!exact) {
    exact = {
      provider: "ollama-local",
      model: model.name,
      digest: model.digest,
      overrides: {}
    };
    overrides.push(exact);
  }

  exact.overrides[role] = profile;
  state.settings.ollamaRuntime = {
    ...runtime,
    modelOverrides: overrides
  };
  state.settingsDirty = true;
  updateSettingsDirtyState();
  elements.runtimeProfileResult.textContent =
    `Override prepared for ${model.name}@${shortDigest(model.digest)} · ${runtimeRoleLabels[role]}. Save settings to apply it.`;
  loadRuntimeOverrideEditor();
}

function removeRuntimeOverrideDraft() {
  const runtime = state.settings.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;
  const overrides = runtime.modelOverrides.map(
    item => ({
      ...item,
      overrides: {
        ...item.overrides
      }
    })
  );
  const exact = overrides.find(
    item => item.provider === "ollama-local"
      && item.model === model?.name
      && item.digest === model?.digest
  );

  if (exact) {
    delete exact.overrides[role];
  }

  state.settings.ollamaRuntime = {
    ...runtime,
    modelOverrides: overrides.filter(
      item => Object.keys(item.overrides).length > 0
    )
  };
  state.settingsDirty = true;
  updateSettingsDirtyState();
  elements.runtimeProfileResult.textContent =
    "Override removed from the draft. Save settings to apply it.";
  loadRuntimeOverrideEditor();
}

function collectOllamaRuntimeSettings() {
  const runtime = state.settings.ollamaRuntime;
  const roles = {};

  for (const [role, fallback] of Object.entries(runtime.roleDefaults)) {
    const values = {};

    for (const field of Object.keys(fallback)) {
      const input = elements.runtimeRoleProfiles.querySelector(
        `[data-runtime-role="${role}"][data-runtime-field="${field}"]`
      );
      values[field] = input ? Number(input.value) : fallback[field];
    }

    roles[role] = values;
  }

  const devices = {};

  for (const enabled of elements.runtimeMemoryDevicePolicies.querySelectorAll(
    "[data-runtime-device-enabled]"
  )) {
    if (!enabled.checked) {
      continue;
    }

    const deviceId = enabled.dataset.runtimeDeviceEnabled;
    devices[deviceId] = {
      targetMaximumUsagePercent: Number(
        elements.runtimeMemoryDevicePolicies.querySelector(
          `[data-runtime-device-percent="${CSS.escape(deviceId)}"]`
        ).value
      ),
      minimumFreeVramBytes: giBToBytes(
        elements.runtimeMemoryDevicePolicies.querySelector(
          `[data-runtime-device-free-vram="${CSS.escape(deviceId)}"]`
        ).value
      )
    };
  }

  return {
    ...runtime,
    roleDefaults: roles,
    memory: {
      ...runtime.memory,
      targetMaximumGpuUsagePercent: Number(
        elements.runtimeMemoryGpuPercent.value
      ),
      minimumFreeVramBytes: giBToBytes(
        elements.runtimeMemoryFreeVram.value
      ),
      minimumFreeSystemRamBytes: giBToBytes(
        elements.runtimeMemoryFreeRam.value
      ),
      allowCpuOffload: elements.runtimeMemoryCpuOffload.checked,
      preferFullGpuForActivePrimary:
        elements.runtimeMemoryPreferFullGpu.checked,
      devices
    }
  };
}

async function analyzeRuntimeProfile() {
  const model = elements.runtimeOverrideModel.value;
  const role = elements.runtimeOverrideRole.value;
  elements.analyzeRuntimeProfile.disabled = true;
  elements.runtimeProfileResult.textContent =
    `Analyzing ${model} metadata; the model will not be loaded…`;

  try {
    const result = await fetchJson(
      "/api/runtime/profiles/analyze",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model,
          role
        })
      }
    );
    const recommendation = result.recommendation;
    elements.runtimeProfileResult.textContent =
      `${model} · ${runtimeRoleLabels[role] ?? role}\n`
      + `Declared: ${formatInteger(recommendation.declaredMaximumContext)} · `
      + `configured: ${formatInteger(recommendation.configuredContext)}\n`
      + `Suggestion ${formatInteger(recommendation.suggestedMinimum)} / `
      + `${formatInteger(recommendation.suggestedTarget)} / `
      + `${formatInteger(recommendation.suggestedMaximum)} · `
      + `confidence ${recommendation.confidence}\n`
      + `Source: ${recommendation.source} · ${recommendation.reason}\n`
      + `Load changed: ${result.loadedModelChanged ? "yes (unexpected)" : "no"}`;
  } catch (error) {
    elements.runtimeProfileResult.textContent =
      runtimeProfileErrorMessage(error);
  } finally {
    elements.analyzeRuntimeProfile.disabled = false;
  }
}

async function measureRuntimeProfile() {
  const model = elements.runtimeOverrideModel.value;
  const role = elements.runtimeOverrideRole.value;
  const context = Number(elements.runtimeOverrideTarget.value);
  const consent =
    `Measure ${model} with ${formatInteger(context)} context tokens?\n\n`
    + "This action will load a real model in Ollama and may use GPU, VRAM, and RAM. "
    + "The Host will restore the target model's prior loaded state when possible.";

  if (!await showAppConfirm(consent, {
    title: "Run real measurement?",
    confirmLabel: "Run measurement"
  })) {
    return;
  }

  elements.measureRuntimeProfile.disabled = true;
  elements.runtimeProfileResult.textContent =
    `Measuring ${model} at ${formatInteger(context)} tokens…`;

  try {
    const result = await fetchJson(
      "/api/runtime/profiles/measure",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model,
          role,
          contextCandidates: [context],
          permissionGranted: true,
          runMinimalRequest: false
        })
      }
    );
    const measurement = result.measurement;
    elements.runtimeProfileResult.textContent =
      `${measurement.model}@${shortDigest(measurement.digest)} · `
      + `${formatInteger(measurement.actualContext)} tokens\n`
      + `VRAM ${formatGiB(measurement.vramSizeBytes)} · `
      + `estimated RAM ${formatGiB(measurement.estimatedRamSizeBytes)} · `
      + `${measurement.processor}\n`
      + `Load ${formatInteger(measurement.loadDurationMilliseconds)} ms · `
      + `target was already loaded: ${result.targetWasAlreadyLoaded ? "yes" : "no"}`;
    state.runtimeProfiles = await fetchJson("/api/runtime/profiles");
    renderRuntimeProfileEvidence();
    await refreshRuntimeStatus();
  } catch (error) {
    elements.runtimeProfileResult.textContent =
      runtimeProfileErrorMessage(error);
  } finally {
    elements.measureRuntimeProfile.disabled = false;
  }
}

function renderRuntimeProfileEvidence() {
  renderExternalAppWarnings();
  const profiles = state.runtimeProfiles;

  if (!profiles) {
    return;
  }

  const warnings = profiles.sharedModelWarnings.map(warning => {
    const row = document.createElement("div");
    row.className = "runtime-shared-warning";
    const icon = document.createElement("span");
    icon.className = "information-button";
    icon.textContent = "i";
    icon.tabIndex = 0;
    icon.setAttribute("role", "img");
    icon.setAttribute("aria-label", warning.message);
    icon.dataset.tooltip = warning.message;
    const text = document.createElement("span");
    text.textContent =
      `${warning.model} · ${warning.roles.map(
        role => runtimeRoleLabels[role] ?? role
      ).join(", ")}`;
    row.append(icon, text);
    return row;
  });
  const diagnostics = (profiles.diagnostics ?? []).map(diagnostic => {
    const row = document.createElement("p");
    row.className = "verification-warning";
    row.textContent = diagnostic.message;
    return row;
  });
  elements.runtimeSharedModelWarnings.replaceChildren(...diagnostics, ...warnings);
}

function runtimeProfileErrorMessage(error) {
  const payload = error.payload;
  return payload?.code
    ? `${payload.message}\nCode: ${payload.code} · stage: ${payload.stage} · trace: ${payload.traceId}`
    : error.message;
}

function shortDigest(value) {
  return value
    ? value.slice(0, 12)
    : "no digest";
}

function bytesToGiB(value) {
  return Number((Number(value ?? 0) / (1024 ** 3)).toFixed(2));
}

function giBToBytes(value) {
  return Math.round(Number(value || 0) * (1024 ** 3));
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
  updateComposerModelTitle();
  updateHarnessControls();
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(elements.routerModel, modelOptions(), state.settings.routerModel);
  replaceOptions(
    elements.routerGpu,
    gpuOptions(true, state.settings.routerGpu),
    state.settings.routerGpu
  );
  replaceOptions(
    elements.actionModel,
    modelOptions(),
    state.settings.actionModel
  );
  replaceOptions(
    elements.actionGpu,
    gpuOptions(true, state.settings.actionGpu),
    state.settings.actionGpu
  );
  replaceOptions(
    elements.coordinatorModel,
    modelOptions(),
    state.settings.coordinatorModel
  );
  replaceOptions(
    elements.coordinatorGpu,
    gpuOptions(true, state.settings.coordinatorGpu),
    state.settings.coordinatorGpu
  );
  replaceOptions(elements.defaultModel, modelOptions(), state.settings.defaultModel);
  replaceOptions(
    elements.supervisorModel,
    [
      {
        value: "same-as-worker",
        label: "Same as Worker"
      },
      ...modelOptions().filter(option => option.provider === "ollama-local")
    ],
    state.settings.supervisorModel ?? "same-as-worker"
  );
  replaceOptions(
    elements.defaultGpu,
    gpuOptions(false, state.settings.defaultGpu),
    state.settings.defaultGpu
  );
  elements.defaultContextTokens.value = state.settings.context.defaultContextTokens;
  elements.providerContextTokens.value = state.settings.context.providerContextTokens;
  elements.reservedResponseTokens.value = state.settings.context.reservedResponseTokens;
  elements.maxDirectPlanSteps.value = state.settings.execution.maxDirectPlanSteps ?? 5;
  elements.fileCreationOutputTokenLimit.value =
    state.settings.execution.fileCreationOutputTokenLimit ?? "";
  const phaseEffort = state.settings.execution.phaseEffort ?? {};
  elements.phaseEffortPlan.value = phaseEffort.plan ?? "high";
  elements.phaseEffortWork.value = phaseEffort.work ?? "medium";
  elements.phaseEffortVerify.value = phaseEffort.verify ?? "medium";
  elements.phaseEffortComplete.value = phaseEffort.complete ?? "low";
  elements.phaseEffortRecovery.value = phaseEffort.recovery ?? "high";
  elements.maxToolOutputTokens.value = state.settings.execution.maxToolOutputTokens;
  elements.generationTimeoutSeconds.value = state.settings.runtime.generationTimeoutSeconds;
  elements.maxConversationMessages.value = state.settings.context.maxConversationMessages;
  elements.showOnboardingBeforeConversation.checked =
    state.settings.onboarding?.showBeforeNewConversation !== false;
  renderRuntimeProfilesEditor();
  elements.usageSelectedWindow.value = state.settings.usage.selectedWindow;
  elements.usageRetentionDays.value = state.settings.usage.retentionDays;
  elements.usageProviderShortMinutes.value =
    state.settings.usage.providerShortWindowMinutes;
  elements.usageProviderLongMinutes.value =
    state.settings.usage.providerLongWindowMinutes;
  elements.usageCustomMinutes.value =
    state.settings.usage.customRollingWindowMinutes;
  elements.usageAlertThresholds.value =
    state.settings.usage.alertThresholds.join(", ");
  for (const option of elements.usagePinnedWindows.options) {
    option.selected = state.settings.usage.pinnedWindows.includes(option.value);
  }
  replaceOptions(
    elements.usageComparisonModel,
    (state.pricingCatalog?.comparisons ?? []).map(
      entry => ({
        value: `${entry.providerId}|${entry.modelId}`,
        label: `${entry.providerId} · ${entry.modelId} · `
          + `${formatCurrency(entry.inputPricePerMillion)}/M input · `
          + `${formatCurrency(entry.outputPricePerMillion)}/M output`
      })
    ),
    `${state.settings.usage.comparisonProvider}|${state.settings.usage.comparisonModel}`
  );
  replaceOptions(
    elements.usageOllamaPlan,
    (state.pricingCatalog?.ollamaPlans ?? []).map(
      plan => ({
        value: plan.plan,
        label: `${plan.plan} · ${formatCurrency(plan.monthlyPrice)}/month`
      })
    ),
    state.settings.usage.ollamaPlanReference
  );
  replaceOptions(
    elements.modelTestSelector,
    modelOptions(),
    elements.modelTestSelector.value || state.settings.defaultModel
  );
  elements.intentionsGrid.replaceChildren();

  for (const [name, intention] of Object.entries(state.settings.intentions)) {
    elements.intentionsGrid.append(createIntentionCard(name, intention));
  }

  renderModelDiagnostics();
  renderCloudProviders();
  renderModelOrganization();
  renderModelProfiles();
  renderModelChainPreview();
  renderSettingsSummaries();
  renderUsageSummary();
}

function renderSettingsSummaries() {
  if (!state.settings) {
    return;
  }

  const active = activeWorkspaceProfile();
  const usage = state.sessions?.usage;
  elements.settingsWorkspaceSummary.textContent = active
    ? `Active workspace: ${active.name}\n`
      + `History: ${active.historyEnabled ? "enabled" : "disabled"}\n`
      + `Stored sessions: ${usage?.sessionCount ?? 0}\n`
      + `Storage: ${formatBytes(usage?.storageBytes ?? 0)}\n`
      + `Retention: ${state.settings.sessionHistory.maxSessionsPerWorkspace} sessions\n`
      + `Compaction: ${formatBytes(state.settings.sessionHistory.sessionCompactionThresholdBytes)} threshold, `
      + `${formatBytes(state.settings.sessionHistory.sessionCompactionTargetBytes)} target`
    : "No active workspace.";
  renderProcessPermissions();
  elements.settingsGitSummary.textContent = state.git?.state === "available"
    ? `Repository: ${state.git.repository?.repositoryRoot ?? "."}\n`
      + `Branch: ${state.git.repository?.detachedHead ? "detached HEAD" : state.git.repository?.branch ?? "unborn"}\n`
      + `Upstream: ${state.git.repository?.upstream ?? "not configured"}\n`
      + `Git: ${state.git.version ?? "unavailable"}\n`
      + `Identity: ${state.git.userName?.scope ?? "unset"} / ${state.git.userEmail?.scope ?? "unset"}`
    : state.git?.state === "not-initialized"
      ? "Git repository is not initialized for this workspace."
      : `Git unavailable: ${state.git?.diagnostic ?? "unknown"}`;
  elements.settingsValidationSummary.textContent =
    state.validationProfiles?.active
      ? `Active profile: ${state.validationProfiles.active.name}\n`
        + `${state.validationProfiles.active.steps.length} structured step(s)`
      : state.validationProfiles?.detected
        ? `Detected suggestion: ${state.validationProfiles.detected.name}\nNo user profile is active.`
        : "No validation profile is configured or detected.";
  elements.settingsAdvancedSummary.textContent =
    `Git diff limit: ${formatBytes(state.settings.gitDelivery.maxDiffBytesPerFile)} per file\n`
    + `Git log limit: ${state.settings.gitDelivery.maxLogEntries} entries\n`
    + `Process history output: ${formatBytes(state.settings.sessionHistory.maxStoredProcessOutputBytesPerTurn)} per turn\n`
    + `No-progress attempts: ${state.settings.execution.maxToolCallsPerTurn} before recovery`;
}

function renderProcessPermissions() {
  const host = elements.settingsProcessPermissions;

  if (!host) {
    return;
  }

  host.replaceChildren();
  const workspace = activeWorkspaceProfile();
  const permissions = workspace?.processPermissions ?? [];

  if (!workspace || permissions.length === 0) {
    const empty = document.createElement("p");
    empty.className = "settings-empty-state";
    empty.textContent = workspace
      ? "No persistent process permissions for this workspace."
      : "Select an active workspace to manage process permissions.";
    host.append(empty);
    return;
  }

  for (const permission of permissions) {
    const row = document.createElement("div");
    row.className = "settings-permission-row";
    const details = document.createElement("div");
    details.className = "settings-permission-details";
    const command = document.createElement("code");
    command.textContent = formatProcessPermission(permission);
    const scope = document.createElement("small");
    scope.textContent = `Working directory: ${permission.workingDirectory}`;
    details.append(command, scope);
    const revoke = document.createElement("button");
    revoke.className = "secondary-button";
    revoke.type = "button";
    revoke.textContent = "Revoke";
    revoke.addEventListener(
      "click",
      () => revokeProcessPermission(workspace.id, permission.id, revoke)
    );
    row.append(details, revoke);
    host.append(row);
  }
}

function formatProcessPermission(permission) {
  return `${permission.executable} · ${permission.argumentCount} exact argument(s) · `
    + `SHA-256 ${permission.argumentsDigest.slice(0, 12)}`;
}

async function revokeProcessPermission(workspaceId, permissionId, button) {
  button.disabled = true;

  try {
    const updated = await fetchJson(
      `/api/workspaces/${encodeURIComponent(workspaceId)}`
        + `/process-permissions/${encodeURIComponent(permissionId)}`,
      {
        method: "DELETE"
      }
    );
    state.workspaceProfiles = {
      ...state.workspaceProfiles,
      profiles: (state.workspaceProfiles?.profiles ?? []).map(
        profile => profile.id === updated.id ? updated : profile
      )
    };
    renderSettingsSummaries();
    showToast("Persistent process permission revoked.");
  } catch (error) {
    button.disabled = false;
    showToast(error.message);
  }
}

function modelOptions() {
  const groups = {
    "ollama-local": "Local models",
    groq: "Groq",
    "google-ai-studio": "Google AI Studio",
    cerebras: "Cerebras"
  };

  return state.models.map(model => {
    const organized = organizedModel(model.name);
    const capabilities = model.capabilities;
    const badges = [
      capabilities?.nativeTools ? "tools" : null,
      capabilities?.vision ? "vision" : null,
      capabilities?.streaming ? "stream" : null
    ].filter(Boolean);

    return {
      value: model.name,
      label: `${organized?.alias ?? model.displayName ?? model.name}`
        + `${organized?.alias ? ` · ${model.name}` : ""}`
        + `${organized?.favorite ? " ★" : ""}`
        + `${badges.length ? ` · ${badges.join(" · ")}` : ""}`,
      group: groups[model.provider] ?? model.provider ?? "Local models",
      disabled: model.selectable === false,
      hidden: organized?.hidden === true,
      favorite: organized?.favorite === true,
      provider: model.provider ?? "ollama-local",
      exactId: organized?.modelId ?? model.name,
      title: capabilities?.source
        ? `Capacidades: ${capabilities.source}`
        : null
    };
  }).filter(
    option => !option.hidden
  ).sort(
    (left, right) =>
      left.provider.localeCompare(right.provider)
      || Number(right.favorite) - Number(left.favorite)
      || left.label.localeCompare(right.label, undefined, { sensitivity: "base" })
      || left.exactId.localeCompare(right.exactId)
  );
}

function organizedModel(qualifiedId) {
  return state.modelOrganization?.models?.find(
    model => model.qualifiedId === qualifiedId
  ) ?? null;
}

function renderModelOrganization() {
  if (!elements.modelOrganizationList) {
    return;
  }

  elements.modelOrganizationList.replaceChildren();
  const search = elements.modelFilterSearch.value.trim().toLocaleLowerCase();
  const location = elements.modelFilterLocation.value;
  const minimumContext = Number(elements.modelFilterContext.value) || 0;
  const models = (state.modelOrganization?.models ?? []).filter(
    model => {
      const capability = model.capabilities;
      return (elements.modelFilterHidden.checked || !model.hidden)
        && (!search
          || `${model.alias ?? ""} ${model.modelId} ${model.qualifiedId}`
            .toLocaleLowerCase()
            .includes(search))
        && (location === "all"
          || location === "local" && model.providerId === "ollama-local"
          || location === "cloud" && model.providerId !== "ollama-local")
        && (!elements.modelFilterTools.checked || capability?.nativeTools)
        && (!elements.modelFilterWeb.checked || capability?.webSearch)
        && (!elements.modelFilterVision.checked || capability?.vision)
        && (!elements.modelFilterStructured.checked || capability?.structuredOutput)
        && (!elements.modelFilterConformance.checked || model.conformanceApproved)
        && (!elements.modelFilterAvailable.checked || model.available)
        && (!elements.modelFilterFavorites.checked || model.favorite)
        && (minimumContext <= 0 || (capability?.contextTokens ?? 0) >= minimumContext);
    }
  );

  for (const model of models) {
    const card = document.createElement("article");
    card.className = "model-organization-card";
    card.dataset.modelIdentity = model.qualifiedId;
    const heading = document.createElement("div");
    heading.className = "model-organization-card-heading";
    const title = document.createElement("strong");
    title.textContent = model.alias ?? model.modelId;
    const exact = document.createElement("small");
    exact.textContent = `${providerLabel(model.providerId)} · ${model.modelId}`;
    const badges = document.createElement("span");
    badges.className = "model-organization-badges";

    for (const label of [
      model.favorite ? "★ favorito" : null,
      model.hidden ? "oculto" : null,
      model.available ? "available" : "unavailable",
      model.conformanceApproved ? "approved conformance" : null,
      model.capabilities?.nativeTools ? "tools" : null,
      model.capabilities?.webSearch ? "web" : null,
      model.capabilities?.vision ? "vision" : null,
      model.capabilities?.structuredOutput ? "structured" : null
    ].filter(Boolean)) {
      const badge = document.createElement("span");
      badge.className = "badge muted";
      badge.textContent = label;
      badges.append(badge);
    }

    heading.append(title, exact, badges);
    const fields = document.createElement("div");
    fields.className = "model-preference-fields";
    const alias = document.createElement("input");
    alias.type = "text";
    alias.maxLength = 80;
    alias.placeholder = "Local alias";
    alias.value = model.alias ?? "";
    alias.dataset.modelAlias = "";
    const note = document.createElement("input");
    note.type = "text";
    note.maxLength = 500;
    note.placeholder = "Optional note";
    note.value = model.note ?? "";
    note.dataset.modelNote = "";
    if (
      model.providerId === "ollama-local"
      && selectableAffinityDevices().length >= 2
    ) {
      const affinityField = document.createElement("label");
      affinityField.className = "model-gpu-affinity-field";
      const affinityLabel = document.createElement("span");
      affinityLabel.textContent = "GPU Affinity";
      const affinity = document.createElement("select");
      affinity.dataset.modelGpuAffinity = model.qualifiedId;
      replaceOptions(
        affinity,
        modelGpuAffinityOptions(
          state.settings?.modelGpuAffinities?.[model.qualifiedId] ?? "auto"
        ),
        state.settings?.modelGpuAffinities?.[model.qualifiedId] ?? "auto"
      );
      affinity.addEventListener("change", () => {
        setModelGpuAffinity(model.qualifiedId, affinity.value, affinity);
      });
      const affinityHelp = document.createElement("small");
      affinityHelp.textContent = "Overrides all role GPUs. Auto uses General Default GPU.";
      affinityField.append(affinityLabel, affinity, affinityHelp);
      fields.append(affinityField);
    }
    fields.append(alias, note);
    const actions = document.createElement("div");
    actions.className = "settings-action-row";

    for (const action of [
      {
        value: "favorite",
        label: model.favorite ? "Desfavoritar" : "Favoritar"
      },
      {
        value: "hidden",
        label: model.hidden ? "Reexibir" : "Ocultar"
      },
      {
        value: "save",
        label: "Save alias and note"
      }
    ]) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary-button";
      button.dataset.modelOrganizationAction = action.value;
      button.dataset.providerId = model.providerId;
      button.dataset.modelId = model.modelId;
      button.textContent = action.label;
      actions.append(button);
    }

    card.append(heading, fields, actions);
    elements.modelOrganizationList.append(card);
  }

  if (models.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "No model matches the filters.";
    elements.modelOrganizationList.append(empty);
  }
}

function selectableAffinityDevices() {
  return state.devices.filter(
    device => !device.isAuto && device.available && device.affinitySelectable
  );
}

function modelGpuAffinityOptions(selected) {
  const options = [
    {
      value: "auto",
      label: "Auto"
    },
    ...selectableAffinityDevices().map(device => ({
      value: `device:${device.id}`,
      label: device.name
    }))
  ];
  if (
    selected?.startsWith("device:")
    && !options.some(option => option.value === selected)
  ) {
    options.push({
      value: selected,
      label: "Configured GPU · unavailable"
    });
  }
  return options;
}

async function handleModelOrganizationAction(event) {
  const button = event.target.closest("[data-model-organization-action]");

  if (!button) {
    return;
  }

  const current = (state.modelOrganization?.models ?? []).find(
    model => model.providerId === button.dataset.providerId
      && model.modelId === button.dataset.modelId
  );

  if (!current) {
    return;
  }

  const card = button.closest(".model-organization-card");
  button.disabled = true;

  try {
    state.modelOrganization = await fetchJson(
      "/api/model-organization/preference",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          providerId: current.providerId,
          modelId: current.modelId,
          alias: card.querySelector("[data-model-alias]").value.trim() || null,
          note: card.querySelector("[data-model-note]").value.trim() || null,
          favorite: button.dataset.modelOrganizationAction === "favorite"
            ? !current.favorite
            : current.favorite,
          hidden: button.dataset.modelOrganizationAction === "hidden"
            ? !current.hidden
            : current.hidden
        })
      }
    );
    renderComposerModels();
    renderSettings();
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function allProfileModelOptions(includeNone = false) {
  return [
    ...(includeNone
      ? [
        {
          value: "none",
          label: "None"
        }
      ]
      : []),
    ...(state.modelOrganization?.models ?? []).map(
      model => ({
        value: model.qualifiedId,
        label: `${model.alias ?? model.modelId}`
          + `${model.alias ? ` · ${model.qualifiedId}` : ""}`
          + `${model.available ? "" : " (unavailable)"}`,
        group: providerLabel(model.providerId),
        disabled: !model.available
      })
    )
  ];
}

function renderModelProfiles() {
  const profiles = state.modelOrganization?.profiles ?? [];
  const selected = elements.modelProfileSelector.value;
  replaceOptions(
    elements.modelProfileSelector,
    [
      {
        value: "",
        label: "New profile"
      },
      ...profiles.map(
        profile => ({
          value: profile.id,
          label: profile.name
        })
      )
    ],
    profiles.some(profile => profile.id === selected)
      ? selected
      : ""
  );
  replaceOptions(
    elements.modelProfilePrimary,
    allProfileModelOptions(),
    elements.modelProfilePrimary.value || state.settings?.defaultModel
  );
  replaceOptions(
    elements.modelProfileFallback,
    allProfileModelOptions(true),
    elements.modelProfileFallback.value || "none"
  );
  replaceOptions(
    elements.modelProfileRouter,
    allProfileModelOptions(),
    elements.modelProfileRouter.value || state.settings?.routerModel
  );
  replaceOptions(
    elements.modelProfileCoordinator,
    allProfileModelOptions(),
    elements.modelProfileCoordinator.value || state.settings?.coordinatorModel
  );
  replaceOptions(
    elements.workspaceModelProfile,
    [
      {
        value: "",
        label: "No preferred profile"
      },
      ...profiles.map(
        profile => ({
          value: profile.id,
          label: profile.name
        })
      )
    ],
    activeWorkspaceProfile()?.preferredModelProfileId ?? ""
  );
  elements.workspaceModelProfile.disabled = !activeWorkspaceProfile();
  elements.applyModelProfile.disabled = !elements.modelProfileSelector.value;
  elements.deleteModelProfile.disabled = !elements.modelProfileSelector.value;
}

function loadSelectedModelProfile() {
  const profile = (state.modelOrganization?.profiles ?? []).find(
    item => item.id === elements.modelProfileSelector.value
  );

  if (!profile) {
    elements.modelProfileName.value = "";
    elements.modelProfilePreview.textContent =
      "Fill in the fields and save to generate the authoritative view.";
    renderModelProfiles();
    return;
  }

  elements.modelProfileName.value = profile.name;
  replaceOptions(
    elements.modelProfilePrimary,
    allProfileModelOptions(),
    profile.primaryModel
  );
  replaceOptions(
    elements.modelProfileFallback,
    allProfileModelOptions(true),
    profile.fallbackModel
  );
  replaceOptions(
    elements.modelProfileRouter,
    allProfileModelOptions(),
    profile.routerModel
  );
  replaceOptions(
    elements.modelProfileCoordinator,
    allProfileModelOptions(),
    profile.coordinatorModel
  );
  elements.modelProfileWeb.value = profile.webPreference;
  elements.modelProfileUsageWindow.value = profile.usageWindow ?? "";
  renderModelProfiles();
  void previewModelProfile(profile.id);
}

async function saveModelProfile() {
  elements.saveModelProfile.disabled = true;
  elements.modelProfileStatus.textContent = "Validating and saving profile…";

  try {
    const preview = await fetchJson(
      "/api/model-organization/profiles",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          id: elements.modelProfileSelector.value || null,
          name: elements.modelProfileName.value.trim(),
          primaryModel: elements.modelProfilePrimary.value,
          fallbackModel: elements.modelProfileFallback.value,
          routerModel: elements.modelProfileRouter.value,
          coordinatorModel: elements.modelProfileCoordinator.value,
          webPreference: elements.modelProfileWeb.value,
          comparisonModel: null,
          usageWindow: elements.modelProfileUsageWindow.value || null
        })
      }
    );
    state.modelOrganization = await fetchJson("/api/model-organization");
    renderModelProfiles();
    elements.modelProfileSelector.value = preview.profileId;
    loadSelectedModelProfile();
    renderProfilePreview(preview);
    elements.modelProfileStatus.textContent = "Profile saved without starting a model.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    elements.saveModelProfile.disabled = false;
  }
}

async function previewModelProfile(profileId) {
  try {
    renderProfilePreview(
      await fetchJson(
        `/api/model-organization/profiles/${encodeURIComponent(profileId)}/preview`
      )
    );
  } catch (error) {
    elements.modelProfilePreview.textContent = error.message;
  }
}

function renderProfilePreview(preview) {
  elements.modelProfilePreview.textContent = [
    ...preview.chain.map(
      item =>
        `${item.role.toUpperCase()}\n`
        + `${providerLabel(item.providerId)} · ${item.exactModelId}\n`
        + `${item.alias ? `Alias: ${item.alias}\n` : ""}`
        + `Available: ${item.available ? "yes" : "no"} · `
        + `Conformance: ${item.conformanceApproved ? "approved" : "not approved"} · `
        + `Tools: ${item.toolPath} · Web: ${item.web ? "yes" : "no"} · `
        + `Vision: ${item.vision ? "yes" : "no"}`
    ),
    `Local fallback: ${preview.localFallbackValid ? "valid" : "invalid"}`,
    `Affected workspaces: ${preview.affectedWorkspaces.join(", ") || "none"}`,
    ...preview.errors.map(error => `ERROR: ${error}`)
  ].join("\n\n");
}

async function applyModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Apply this profile atomically to new requests? The current conversation will not restart.",
    { title: "Apply profile?", confirmLabel: "Apply" }
  )) {
    return;
  }

  elements.applyModelProfile.disabled = true;

  try {
    const preview = await fetchJson(
      `/api/model-organization/profiles/${encodeURIComponent(profileId)}/apply`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          confirmed: true
        })
      }
    );
    state.settings = await fetchJson("/api/settings");
    renderSettings();
    renderProfilePreview(preview);
    elements.modelProfileStatus.textContent =
      "Profile applied. The current conversation selection was preserved.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    elements.applyModelProfile.disabled = false;
  }
}

async function deleteModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Delete this saved profile?",
    { title: "Delete profile?", confirmLabel: "Delete", danger: true }
  )) {
    return;
  }

  try {
    state.modelOrganization = await fetchJson(
      `/api/model-organization/profiles/${encodeURIComponent(profileId)}`,
      {
        method: "DELETE"
      }
    );
    elements.modelProfileSelector.value = "";
    loadSelectedModelProfile();
    elements.modelProfileStatus.textContent = "Profile deleted.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  }
}

async function saveWorkspaceModelProfile() {
  const workspace = activeWorkspaceProfile();

  if (!workspace) {
    return;
  }

  try {
    state.workspaceProfiles = await fetchJson(
      `/api/model-organization/workspaces/${encodeURIComponent(workspace.id)}/preferred-profile`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          profileId: elements.workspaceModelProfile.value || null
        })
      }
    );
    elements.modelProfileStatus.textContent =
      "Workspace preference saved by reference.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  }
}

function renderModelChainPreview() {
  if (!state.settings || !elements.modelChainPreview) {
    return;
  }

  const generalFallback =
    document.querySelector('[data-intention="general-chat"] .intention-fallback-model')
      ?.value
    ?? state.settings.intentions["general-chat"]?.fallbackModel
    ?? "none";
  const roles = [
    {
      role: "PRIMARY",
      model: elements.defaultModel.value || state.settings.defaultModel
    },
    {
      role: "FALLBACK",
      model: generalFallback
    },
    {
      role: "ROUTER",
      model: elements.routerModel.value || state.settings.routerModel
    },
    {
      role: "COORDINATOR",
      model: elements.coordinatorModel.value || state.settings.coordinatorModel
    }
  ];
  elements.modelChainPreview.textContent = roles.map(
    item => {
      if (item.model === "none") {
        return `${item.role}\nNenhum`;
      }

      const model = organizedModel(item.model);
      const reference = parseModelReference(item.model);
      return `${item.role}\n`
        + `${providerLabel(reference.provider)} · ${reference.model}\n`
        + `${model?.alias ? `Alias: ${model.alias} · ` : ""}`
        + `${model?.available ? "available" : "unavailable"} · `
        + `conformance ${model?.conformanceApproved ? "approved" : "not approved"} · `
        + `tools ${model?.capabilities?.nativeTools ? "yes" : "no"} · `
        + `web ${model?.capabilities?.webSearch ? "yes" : "no"} · `
        + `vision ${model?.capabilities?.vision ? "yes" : "no"}`;
    }
  ).join("\n\n");
}

function renderCloudProviders() {
  renderExternalAppWarnings();
  elements.cloudProvidersList.replaceChildren();

  const localHealth = (state.providerHealth?.providers ?? []).find(
    provider => provider.providerId.startsWith("ollama")
  );
  if (localHealth) {
    elements.cloudProvidersList.append(
      createProviderHealthCard(localHealth)
    );
  }

  for (const provider of state.cloudProviders?.providers ?? []) {
    const health = (state.providerHealth?.providers ?? []).find(
      item => item.providerId === provider.provider
    );
    const card = document.createElement("details");
    card.className = "cloud-provider-card";
    card.dataset.provider = provider.provider;
    card.open = state.openCloudProviders.has(provider.provider);
    card.addEventListener("toggle", () => {
      if (card.open) {
        state.openCloudProviders.add(provider.provider);
      } else {
        state.openCloudProviders.delete(provider.provider);
      }
    });
    const summary = document.createElement("summary");
    const title = document.createElement("span");
    title.className = "cloud-provider-title";
    title.textContent = provider.displayName;
    const status = document.createElement("span");
    status.className = `badge ${provider.connectionState === "connected"
      ? "success"
      : provider.connectionState === "error"
        ? "error"
        : "muted"}`;
    status.textContent = cloudConnectionLabel(provider.connectionState);
    summary.append(title, status);

    const body = document.createElement("div");
    body.className = "cloud-provider-body";
    const metadata = document.createElement("dl");
    metadata.className = "cloud-provider-metadata";
    appendDefinition(metadata, "Active", provider.enabled ? "Yes" : "No");
    appendDefinition(metadata, "Key", provider.maskedKeyState);
    appendDefinition(metadata, "Models", String(provider.modelCount));
    appendDefinition(
      metadata,
      "Last update",
      provider.lastRefreshAt
        ? new Date(provider.lastRefreshAt).toLocaleString(window.AgenticRouterI18n.locale)
        : "Not updated yet"
    );
    appendDefinition(metadata, "Quota", provider.quotaSource);
    if (health) {
      appendDefinition(
        metadata,
        "Health",
        providerHealthStateLabel(health.connectionState)
      );
      appendDefinition(
        metadata,
        "Last success",
        formatProviderHealthDate(health.lastSuccessfulRequest)
      );
      appendDefinition(
        metadata,
        "Latency",
        health.totalLatencyMilliseconds == null
          ? "unavailable"
          : `${formatInteger(health.totalLatencyMilliseconds)} ms`
      );
      appendDefinition(metadata, "Usage", usageAccuracyLabel(health.tokenUsageAccuracy));
    }

    const billingField = document.createElement("label");
    billingField.className = "cloud-billing-field";
    const billingLabel = document.createElement("span");
    billingLabel.textContent = "Expected billing mode";
    const billingSelect = document.createElement("select");
    billingSelect.dataset.cloudBilling = provider.provider;
    replaceOptions(
      billingSelect,
      [
        {
          value: "unknown",
          label: "Unknown"
        },
        {
          value: "free-tier",
          label: "Free tier"
        },
        {
          value: "paid",
          label: "Paid"
        }
      ],
      provider.expectedBillingMode ?? "unknown"
    );
    billingField.append(billingLabel, billingSelect);

    const keyField = document.createElement("label");
    const keyLabel = document.createElement("span");
    keyLabel.textContent = provider.hasKey ? "Replace key" : "API key";
    const keyInput = document.createElement("input");
    keyInput.type = "password";
    keyInput.autocomplete = "new-password";
    keyInput.dataset.cloudKey = provider.provider;
    keyInput.dataset.ignoreSettingsDirty = "";
    keyInput.placeholder = provider.hasKey
      ? "Enter a new key"
      : "Enter the key";
    keyField.append(keyLabel, keyInput);

    const actions = document.createElement("div");
    actions.className = "settings-action-row";
    actions.append(
      cloudActionButton(
        provider.provider,
        "save-key",
        provider.hasKey ? "Replace" : "Save key"
      ),
      cloudActionButton(provider.provider, "test", "Test connection"),
      cloudActionButton(provider.provider, "refresh", "Refresh models"),
      cloudActionButton(
        provider.provider,
        "remove-key",
        "Remove key",
        "danger-button"
      )
    );
    actions.querySelector('[data-cloud-action="test"]').disabled = !provider.hasKey;
    actions.querySelector('[data-cloud-action="refresh"]').disabled = !provider.hasKey;
    actions.querySelector('[data-cloud-action="remove-key"]').disabled = !provider.hasKey;

    const diagnostic = document.createElement("p");
    diagnostic.className = "runtime-note cloud-provider-diagnostic";
    diagnostic.dataset.cloudDiagnostic = provider.provider;
    diagnostic.textContent = provider.diagnostic ?? "";
    const fields = document.createElement("div");
    fields.className = "cloud-provider-fields";
    fields.append(keyField, billingField);
    body.append(metadata, fields, actions, diagnostic);
    card.append(summary, body);
    elements.cloudProvidersList.append(card);
  }

  renderOllamaWebSearchSettings();
}

function createProviderHealthCard(provider) {
  const card = document.createElement("details");
  card.className = "cloud-provider-card provider-health-card";
  card.dataset.provider = provider.providerId;
  card.dataset.state = provider.connectionState;
  card.open = state.openCloudProviders.has(provider.providerId);
  card.addEventListener("toggle", () => {
    if (card.open) {
      state.openCloudProviders.add(provider.providerId);
    } else {
      state.openCloudProviders.delete(provider.providerId);
    }
  });
  const summary = document.createElement("summary");
  const title = document.createElement("span");
  title.className = "cloud-provider-title";
  title.textContent = provider.displayName;
  const badge = document.createElement("span");
  badge.className = `badge ${provider.connectionState === "healthy"
    ? "success"
    : ["degraded", "unavailable"].includes(provider.connectionState)
      ? "error"
      : "muted"}`;
  badge.textContent = providerHealthStateLabel(provider.connectionState);
  summary.append(title, badge);
  const body = document.createElement("div");
  body.className = "cloud-provider-body";
  const metadata = document.createElement("dl");
  metadata.className = "cloud-provider-metadata";
  appendDefinition(metadata, "Last success", formatProviderHealthDate(provider.lastSuccessfulRequest));
  appendDefinition(
    metadata,
    "Latency",
    provider.totalLatencyMilliseconds == null
      ? "unavailable"
      : `${formatInteger(provider.totalLatencyMilliseconds)} ms`
  );
  appendDefinition(metadata, "Quota", provider.quotaState);
  appendDefinition(metadata, "Usage", usageAccuracyLabel(provider.tokenUsageAccuracy));
  const diagnostic = document.createElement("pre");
  diagnostic.className = "provider-health-diagnostic";
  diagnostic.textContent = [
    `status: ${provider.diagnostic.lastStatusCode ?? "unavailable"}`,
    `retry: ${provider.diagnostic.retryDecision}`,
    `source: ${provider.healthSource}`,
    `stale: ${provider.stale ? "yes" : "no"}`
  ].join("\n");
  body.append(metadata, diagnostic);
  card.append(summary, body);
  return card;
}

function renderOllamaWebSearchSettings() {
  const integration = state.webSearch;

  if (!integration) {
    return;
  }

  const card = document.createElement("details");
  card.className = "cloud-provider-card";
  card.dataset.provider = integration.provider;
  card.open = state.openCloudProviders.has(integration.provider);
  card.addEventListener("toggle", () => {
    if (card.open) {
      state.openCloudProviders.add(integration.provider);
    } else {
      state.openCloudProviders.delete(integration.provider);
    }
  });
  const summary = document.createElement("summary");
  const title = document.createElement("span");
  title.className = "cloud-provider-title";
  title.textContent = integration.displayName;
  const status = document.createElement("span");
  status.className = `badge ${integration.state === "available"
    ? "success"
    : "muted"}`;
  status.textContent = integration.state === "available"
    ? "Available"
    : "Not configured";
  summary.append(title, status);

  const body = document.createElement("div");
  body.className = "cloud-provider-body";
  const note = document.createElement("p");
  note.className = "runtime-note";
  note.textContent =
    "Read-only search for local models. It is separate from Ollama Cloud models "
      + "and becomes automatically available when the effective route can use it.";
  const keyField = document.createElement("label");
  const keyLabel = document.createElement("span");
  keyLabel.textContent = integration.hasKey
    ? "Replace key"
    : "Separate API key";
  const keyInput = document.createElement("input");
  keyInput.type = "password";
  keyInput.autocomplete = "new-password";
  keyInput.dataset.webSearchKey = "";
  keyInput.dataset.ignoreSettingsDirty = "";
  keyInput.placeholder = integration.hasKey
    ? "Enter a new key"
    : "OLLAMA_API_KEY";
  keyField.append(keyLabel, keyInput);
  const actions = document.createElement("div");
  actions.className = "settings-action-row";
  const save = document.createElement("button");
  save.type = "button";
  save.className = "secondary-button";
  save.dataset.webSearchAction = "save-key";
  save.textContent = integration.hasKey ? "Replace" : "Save key";
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "secondary-button danger-button";
  remove.dataset.webSearchAction = "remove-key";
  remove.textContent = "Remove key";
  remove.disabled = !integration.hasKey;
  actions.append(save, remove);
  const diagnostic = document.createElement("p");
  diagnostic.className = "runtime-note cloud-provider-diagnostic";
  diagnostic.dataset.webSearchDiagnostic = "";
  diagnostic.textContent = integration.diagnostic ?? "";
  body.append(note, keyField, actions, diagnostic);
  card.append(summary, body);
  elements.cloudProvidersList.append(card);
}

function cloudActionButton(provider, action, label, extraClass = "") {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `secondary-button ${extraClass}`.trim();
  button.dataset.cloudProvider = provider;
  button.dataset.cloudAction = action;
  button.textContent = label;
  return button;
}

function collectCloudProviderSettings() {
  const cloud = structuredClone(state.settings.cloudProviders);
  const keys = {
    groq: "groq",
    "google-ai-studio": "googleAiStudio",
    cerebras: "cerebras"
  };

  for (const select of elements.cloudProvidersList.querySelectorAll(
    "[data-cloud-billing]"
  )) {
    const key = keys[select.dataset.cloudBilling];

    if (key && cloud[key]) {
      cloud[key].expectedBillingMode = select.value;
    }
  }

  return cloud;
}

function appendDefinition(list, term, description) {
  const dt = document.createElement("dt");
  dt.textContent = term;
  const dd = document.createElement("dd");
  dd.textContent = description;
  list.append(dt, dd);
}

function cloudConnectionLabel(stateName) {
  return {
    connected: "Connected",
    error: "Error",
    disabled: "Disabled",
    "key-required": "Key required",
    "not-tested": "Not tested"
  }[stateName] ?? stateName;
}

async function handleCloudProviderAction(event) {
  const webButton = event.target.closest("[data-web-search-action]");

  if (webButton) {
    await handleOllamaWebSearchAction(webButton);
    return;
  }

  const button = event.target.closest("[data-cloud-action]");

  if (!button) {
    return;
  }

  const provider = button.dataset.cloudProvider;
  const action = button.dataset.cloudAction;
  const diagnostic = elements.cloudProvidersList.querySelector(
    `[data-cloud-diagnostic="${CSS.escape(provider)}"]`
  );
  let path;
  let options;

  if (action === "save-key") {
    const input = elements.cloudProvidersList.querySelector(
      `[data-cloud-key="${CSS.escape(provider)}"]`
    );
    const apiKey = input.value.trim();

    if (!apiKey) {
      diagnostic.textContent = "Enter an API key before saving.";
      input.classList.add("field-invalid");
      input.setAttribute("aria-invalid", "true");
      showToast(diagnostic.textContent);
      input.focus();
      return;
    }

    path = `/api/cloud-providers/${encodeURIComponent(provider)}/key`;
    options = {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ apiKey })
    };
  } else if (action === "remove-key") {
    if (!await showAppConfirm(
      "Permanently remove this provider's protected key?",
      { title: "Remove key?", confirmLabel: "Remove", danger: true }
    )) {
      return;
    }

    path = `/api/cloud-providers/${encodeURIComponent(provider)}/key?confirmed=true`;
    options = { method: "DELETE" };
  } else {
    const operation = action === "test" ? "test the connection" : "refresh models";

    if (!await showAppConfirm(
      `Allow a real provider call for ${operation}? This action may consume quota.`,
      { title: "Authorize provider call?", confirmLabel: "Authorize" }
    )) {
      return;
    }

    path = action === "test"
      ? `/api/cloud-providers/${encodeURIComponent(provider)}/test`
      : `/api/cloud-providers/${encodeURIComponent(provider)}/models/refresh`;
    options = { method: "POST" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processing…";

  try {
    await fetchJson(path, options);
    await refreshCloudProviderState();
    showToast("Provider action completed.", "success");
  } catch (error) {
    diagnostic.textContent = error.message;
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
}

async function handleOllamaWebSearchAction(button) {
  const action = button.dataset.webSearchAction;
  const diagnostic = elements.cloudProvidersList.querySelector(
    "[data-web-search-diagnostic]"
  );
  let path;
  let options;

  if (action === "save-key") {
    const input = elements.cloudProvidersList.querySelector(
      "[data-web-search-key]"
    );
    const apiKey = input.value.trim();

    if (!apiKey) {
      diagnostic.textContent = "Enter the separate key before saving.";
      input.classList.add("field-invalid");
      input.setAttribute("aria-invalid", "true");
      showToast(diagnostic.textContent);
      input.focus();
      return;
    }

    path = "/api/web-search/key";
    options = {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ apiKey })
    };
  } else {
    if (!await showAppConfirm(
      "Permanently remove the protected Ollama Web Search key?",
      { title: "Remove key?", confirmLabel: "Remove", danger: true }
    )) {
      return;
    }

    path = "/api/web-search/key?confirmed=true";
    options = { method: "DELETE" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processing…";

  try {
    state.webSearch = await fetchJson(path, options);
    renderCloudProviders();
    await refreshSelectedModelCapabilities();
  } catch (error) {
    diagnostic.textContent = error.message;
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
}

async function refreshCloudProviderState() {
  const [
    cloudProviders,
    modelsResponse,
    settings,
    cloudUsageDashboard,
    webSearch
  ] = await Promise.all([
    fetchJson("/api/cloud-providers"),
    fetchJson("/api/models"),
    fetchJson("/api/settings"),
    fetchJson("/api/usage/cloud-dashboard"),
    fetchJson("/api/web-search")
  ]);
  state.cloudProviders = cloudProviders;
  state.models = modelsResponse.models;
  state.settings = settings;
  state.cloudUsageDashboard = cloudUsageDashboard;
  state.webSearch = webSearch;
  renderCloudProviders();
  renderComposerModels();
  renderSettings();
  renderCloudUsage();
  await refreshSelectedModelCapabilities();
  await refreshProviderHealth();
}

function gpuOptions(includeDefault, selected = null) {
  const options = includeDefault
    ? [
      {
        value: "default",
        label: "Default"
      }
    ]
    : [];

  for (const device of state.devices) {
    if (!device.isAuto && !device.affinitySelectable) {
      continue;
    }

    const backend = device.backend ?? "cuda";
    const index = device.backendIndex ?? device.ollamaIndex;
    options.push({
      value: device.isAuto
        ? "auto"
        : backend === "cuda"
          ? `ollama:${index}`
          : `${backend}:${index}`,
      label: device.isAuto
        ? "Auto"
        : `${runtimeBackendLabel(backend)} ${index} · ${device.name}`
    });
  }

  const selectableDevices = state.devices.filter(
    device => !device.isAuto && device.affinitySelectable
  );
  if (selectableDevices.length >= 2) {
    options.push({
      value: "vulkan:all",
      label: "Vulkan combined · all GPUs (experimental)"
    });
    for (const device of selectableDevices) {
      options.push({
        value: `vulkan:prefer:${device.backend}:${device.backendIndex}`,
        label: `Vulkan combined · prioritize ${device.name}`
      });
    }
  }

  if (
    selected
    && /^(?:(?:ollama|rocm|vulkan):(?:\d+|all)|vulkan:prefer:(?:cuda|rocm):\d+)$/.test(selected)
    && !options.some(option => option.value === selected)
  ) {
    if (selected.startsWith("vulkan:prefer:")) {
      const [, , preferredBackend, preferredIndex] = selected.split(":");
      options.push({
        value: selected,
        label: `Vulkan combined · prioritize ${runtimeBackendLabel(preferredBackend)} ${preferredIndex} · configured, unavailable`
      });
      return options;
    }
    const [backend, index] = selected.split(":");
    options.push({
      value: selected,
      label: `${runtimeBackendLabel(backend === "ollama" ? "cuda" : backend)} ${index} · configured, unavailable`
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
  const modelField = createSelectField(
      "Model",
      "intention-model",
      [
        {
          value: "default",
          label: "Default"
        },
        ...modelOptions()
      ],
      intention.model
    );
  const fallbackField = createSelectField(
      "Fallback",
      "intention-fallback-model",
      [
        {
          value: "none",
          label: "None"
        },
        {
          value: "default",
          label: "Default"
        },
        ...modelOptions()
      ],
      intention.fallbackModel ?? "none"
    );
  const affinityField = createSelectField(
    "GPU",
    "intention-model-gpu-affinity",
    [],
    "auto"
  );
  const modelSelect = modelField.querySelector("select");
  const affinity = affinityField.querySelector("select");
  const refreshAffinity = () => {
    const modelIdentity = resolveIntentionModelIdentity(modelSelect.value);
    const local = isLocalModelIdentity(modelIdentity);
    const selected = local
      ? state.settings?.modelGpuAffinities?.[modelIdentity] ?? "auto"
      : "auto";
    affinity.dataset.modelGpuAffinity = local ? modelIdentity : "";
    replaceOptions(
      affinity,
      local
        ? modelGpuAffinityOptions(selected)
        : [{ value: "auto", label: "Provider managed" }],
      selected
    );
    affinity.disabled = !local;
    affinity.title = local
      ? "Explicit model affinity overrides every role GPU. Auto uses General Default GPU."
      : "Cloud-provider models do not use a local GPU affinity.";
  };
  modelSelect.addEventListener("change", refreshAffinity);
  affinity.addEventListener("change", () => {
    const modelIdentity = affinity.dataset.modelGpuAffinity;
    if (modelIdentity) {
      setModelGpuAffinity(modelIdentity, affinity.value, affinity);
    }
  });
  refreshAffinity();
  selects.append(modelField, fallbackField, affinityField);
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

function resolveIntentionModelIdentity(configuredModel) {
  return configuredModel === "default"
    ? state.settings?.defaultModel
    : configuredModel;
}

function isLocalModelIdentity(modelIdentity) {
  if (!modelIdentity || modelIdentity === "auto") {
    return false;
  }
  return organizedModel(modelIdentity)?.providerId === "ollama-local"
    || !modelIdentity.includes("::");
}

function setModelGpuAffinity(modelIdentity, value, source = null) {
  const configured = {
    ...(state.settings.modelGpuAffinities ?? {})
  };
  if (value === "auto") {
    delete configured[modelIdentity];
  } else {
    configured[modelIdentity] = value;
  }
  state.settings = {
    ...state.settings,
    modelGpuAffinities: configured
  };
  document.querySelectorAll("select[data-model-gpu-affinity]").forEach(select => {
    if (select !== source && select.dataset.modelGpuAffinity === modelIdentity) {
      replaceOptions(select, modelGpuAffinityOptions(value), value);
    }
  });
  state.settingsDirty = true;
  updateSettingsDirtyState();
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
      label: `${selected} (unavailable)`,
      group: "Current configuration"
    });
  }

  const nodes = [];
  const groups = new Map();

  for (const option of normalized) {
    let parent = null;

    if (option.group) {
      parent = groups.get(option.group);

      if (!parent) {
        parent = document.createElement("optgroup");
        parent.label = option.group;
        groups.set(
          option.group,
          parent
        );
        nodes.push(
          parent
        );
      }
    }

    const element = document.createElement("option");
    element.value = option.value;
    element.textContent = option.label;
    element.disabled = Boolean(option.disabled);

    if (option.title) {
      element.title = option.title;
    }

    if (parent) {
      parent.append(
        element
      );
    } else {
      nodes.push(
        element
      );
    }
  }

  select.replaceChildren(
    ...nodes
  );
  select.value = selected;
}

function renderModelDiagnostics() {
  if (!state.modelDiagnostics) {
    elements.modelDiagnosticsList.replaceChildren();
    elements.modelContextDiagnostic.textContent = "Loading diagnostic…";
    return;
  }

  elements.modelContextDiagnostic.textContent =
    state.modelDiagnostics.contextDiagnostic;
  elements.modelDiagnosticsList.replaceChildren(
    ...state.modelDiagnostics.models.map(
      diagnostic => {
        const row = document.createElement("div");
        row.className = "model-diagnostic-row";
        const configuration = document.createElement("span");
        configuration.textContent = diagnostic.configuration;
        const model = document.createElement("strong");
        model.textContent = diagnostic.resolvedModel
          ?? diagnostic.configuredValue
          ?? "—";
        const status = document.createElement("span");
        status.className = `model-status ${diagnostic.status.toLowerCase()}`;
        status.textContent = diagnostic.status;
        row.append(configuration, model, status);
        return row;
      }
    )
  );
}

async function testSelectedModel() {
  const model = elements.modelTestSelector.value;
  elements.testModel.disabled = true;
  elements.modelTestResult.textContent = `Testing ${model}…`;

  try {
    const result = await fetchJson(
      "/api/models/test",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model
        })
      }
    );
    elements.modelTestResult.textContent = result.connected
      ? `${result.model} · Completed · Time to first chunk: `
        + `${result.timeToFirstChunkMilliseconds ?? "unavailable"} ms · `
        + `Total duration: ${result.totalDurationMilliseconds} ms`
      : `${result.model} · Failed · ${result.error} · Trace ID: ${result.traceId}`;
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderModelDiagnostics();
  } catch (error) {
    elements.modelTestResult.textContent = error.message;
  } finally {
    elements.testModel.disabled = false;
  }
}

async function openSettings(section = "general") {
  elements.runtimeDetails.open = false;
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "";
  elements.modelTestResult.textContent = "";
  renderSettings();
  elements.settingsDialog.showModal();
  state.settingsDirty = false;
  updateSettingsDirtyState();
  setSettingsSection(section, false);
  document.querySelector(
    `[data-settings-target="${normalizeSettingsSection(section)}"]`
  )?.focus();
  await refreshSetupStatus({ quiet: true });

  try {
    [
      state.modelDiagnostics,
      state.runtimeProfiles
    ] = await Promise.all([
      fetchJson("/api/models/diagnostics"),
      fetchJson("/api/runtime/profiles")
    ]);
    renderModelDiagnostics();
    renderRuntimeProfilesEditor();
  } catch (error) {
    elements.modelContextDiagnostic.textContent = error.message;
    elements.runtimeProfileResult.textContent = error.message;
  }

  await loadPortableYaml();
}

async function closeSettings() {
  if (
    state.settingsDirty
    && !await showAppConfirm(
      "Discard the unsaved configuration changes?",
      { title: "Close without saving?", confirmLabel: "Discard", danger: true }
    )
  ) {
    return false;
  }
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  document.querySelector("#open-settings").focus();
  return true;
}

async function saveSettings(event) {
  event.preventDefault();
  elements.settingsErrors.hidden = true;
  clearSettingsValidationMarkers();
  elements.saveStatus.textContent = "Saving…";
  const intentions = {};

  for (const card of elements.intentionsGrid.querySelectorAll(".intention-card")) {
    intentions[card.dataset.intention] = {
      model: card.querySelector(".intention-model").value,
      fallbackModel: card.querySelector(".intention-fallback-model").value,
      gpu: state.settings.intentions[card.dataset.intention]?.gpu ?? "auto",
      systemPrompt: card.querySelector(".intention-prompt").value
    };
  }

  const nextSettings = {
    schemaVersion: state.settings.schemaVersion,
    ollamaUrl: elements.ollamaUrl.value.trim(),
    routerModel: elements.routerModel.value,
    routerGpu: elements.routerGpu.value,
    actionModel: elements.actionModel.value,
    actionGpu: elements.actionGpu.value,
    coordinatorModel: elements.coordinatorModel.value,
    coordinatorGpu: elements.coordinatorGpu.value,
    defaultModel: elements.defaultModel.value,
    supervisorModel: elements.supervisorModel.value,
    defaultGpu: elements.defaultGpu.value,
    modelGpuAffinities: {
      ...(state.settings.modelGpuAffinities ?? {})
    },
    trustedWorkspacePath: state.settings.trustedWorkspacePath ?? null,
    intentions,
    context: {
      defaultContextTokens: Number(elements.defaultContextTokens.value),
      providerContextTokens: Number(elements.providerContextTokens.value),
      reservedResponseTokens: Number(elements.reservedResponseTokens.value),
      maxConversationMessages: Number(elements.maxConversationMessages.value)
    },
    runtime: {
      ...state.settings.runtime,
      generationTimeoutSeconds: Number(elements.generationTimeoutSeconds.value)
    },
    ollamaRuntime: collectOllamaRuntimeSettings(),
    execution: {
      ...state.settings.execution,
      maxDirectPlanSteps: Number(elements.maxDirectPlanSteps.value),
      fileCreationOutputTokenLimit: elements.fileCreationOutputTokenLimit.value === ""
        ? null
        : Number(elements.fileCreationOutputTokenLimit.value),
      phaseEffort: {
        plan: elements.phaseEffortPlan.value,
        work: elements.phaseEffortWork.value,
        verify: elements.phaseEffortVerify.value,
        complete: elements.phaseEffortComplete.value,
        recovery: elements.phaseEffortRecovery.value
      },
      maxToolOutputTokens: Number(elements.maxToolOutputTokens.value)
    },
    projectAwareness: state.settings.projectAwareness,
    validationProfile: state.validationProfiles?.active ?? null,
    sessionHistory: state.settings.sessionHistory,
    gitDelivery: state.settings.gitDelivery,
    usage: {
      ...state.settings.usage,
      retentionDays: Number(elements.usageRetentionDays.value),
      selectedWindow: elements.usageSelectedWindow.value,
      pinnedWindows: Array.from(
        elements.usagePinnedWindows.selectedOptions,
        option => option.value
      ),
      providerShortWindowMinutes: Number(elements.usageProviderShortMinutes.value),
      providerLongWindowMinutes: Number(elements.usageProviderLongMinutes.value),
      customRollingWindowMinutes: Number(elements.usageCustomMinutes.value),
      alertThresholds: elements.usageAlertThresholds.value
        .split(",")
        .map(value => Number(value.trim()))
        .filter(value => Number.isInteger(value)),
      comparisonProvider: elements.usageComparisonModel.value.split("|")[0],
      comparisonModel: elements.usageComparisonModel.value.split("|")[1],
      ollamaPlanReference: elements.usageOllamaPlan.value
    },
    cloudProviders: collectCloudProviderSettings(),
    webSearch: state.settings.webSearch,
    knowledgeProviders: state.settings.knowledgeProviders,
    modelOrganization: state.settings.modelOrganization,
    onboarding: {
      showBeforeNewConversation:
        elements.showOnboardingBeforeConversation.checked
    }
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
    elements.saveStatus.textContent = "Saved";
    state.settingsDirty = false;
    updateSettingsDirtyState();
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderSettings();
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  } catch (error) {
    const errors = error.payload?.errors;
    const message = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsErrors.textContent = message;
    markSettingsValidationErrors(errors ?? {});
    showToast(message, "error", 30000);
    elements.saveStatus.textContent = "";
    navigateToSettingsError(
      Object.keys(errors ?? {})[0]
    );
  }
}

function handleSettingsInput(event) {
  event.target.classList.remove("field-invalid");
  event.target.removeAttribute("aria-invalid");
  event.target.closest(".intention-card")?.classList.remove("field-invalid-card");
  if (
    event.target.closest("[data-ignore-settings-dirty]")
    ||
    event.target.id === "model-test-selector"
    || event.target.closest(".model-test-panel") && event.target.tagName === "BUTTON"
  ) {
    return;
  }
  state.settingsDirty = true;
  updateSettingsDirtyState();
  renderModelChainPreview();
}

function clearSettingsValidationMarkers() {
  elements.settingsForm.querySelectorAll(".field-invalid").forEach(
    field => {
      field.classList.remove("field-invalid");
      field.removeAttribute("aria-invalid");
    }
  );
  elements.settingsForm.querySelectorAll(".field-invalid-card").forEach(
    card => card.classList.remove("field-invalid-card")
  );
}

function markSettingsValidationErrors(errors) {
  for (const field of Object.keys(errors)) {
    let control = null;
    const intention = field.match(/^intentions[.:]([^.:]+)/i)?.[1];
    if (intention) {
      const card = elements.intentionsGrid.querySelector(
        `[data-intention="${CSS.escape(intention)}"]`
      );
      control = field.toLowerCase().endsWith("gpu")
        ? card?.querySelector(".intention-gpu")
        : field.toLowerCase().includes("fallback")
          ? card?.querySelector(".intention-fallback-model")
          : card?.querySelector(".intention-model");
      card?.classList.add("field-invalid-card");
    } else if (field === "routerGpu") {
      control = elements.routerGpu;
    } else if (field.startsWith("router")) {
      control = elements.routerModel;
    } else if (field === "actionGpu") {
      control = elements.actionGpu;
    } else if (field.startsWith("action")) {
      control = elements.actionModel;
    } else if (field === "coordinatorGpu") {
      control = elements.coordinatorGpu;
    } else if (field.startsWith("coordinator")) {
      control = elements.coordinatorModel;
    } else if (field.startsWith("defaultModel")) {
      control = elements.defaultModel;
    } else if (field.startsWith("supervisorModel")) {
      control = elements.supervisorModel;
    } else {
      const fieldControls = {
        "context.defaultContextTokens": elements.defaultContextTokens,
        "context.providerContextTokens": elements.providerContextTokens,
        "context.reservedResponseTokens": elements.reservedResponseTokens,
        "context.maxConversationMessages": elements.maxConversationMessages,
        "execution.maxDirectPlanSteps": elements.maxDirectPlanSteps,
        "execution.fileCreationOutputTokenLimit": elements.fileCreationOutputTokenLimit,
        "execution.phaseEffort.plan": elements.phaseEffortPlan,
        "execution.phaseEffort.work": elements.phaseEffortWork,
        "execution.phaseEffort.verify": elements.phaseEffortVerify,
        "execution.phaseEffort.complete": elements.phaseEffortComplete,
        "execution.phaseEffort.recovery": elements.phaseEffortRecovery,
        "execution.maxToolOutputTokens": elements.maxToolOutputTokens,
        "runtime.generationTimeoutSeconds": elements.generationTimeoutSeconds,
        defaultGpu: elements.defaultGpu,
        ollamaUrl: elements.ollamaUrl
      };
      control = fieldControls[field] ?? null;
    }
    if (control) {
      control.classList.add("field-invalid");
      control.setAttribute("aria-invalid", "true");
    }
  }
}

function updateSettingsDirtyState() {
  elements.settingsDirty.textContent = state.settingsDirty
    ? "Unsaved changes"
    : "No changes";
  elements.settingsDirty.className =
    `badge ${state.settingsDirty ? "error" : "muted"}`;
  elements.saveSettings.disabled =
    Boolean(state.recovery?.settingsReadOnly) || !state.settingsDirty;
}

async function loadPortableYaml() {
  elements.settingsYamlStatus.textContent = "Loading configuration…";
  elements.settingsYamlStatus.className = "portable-yaml-status";

  try {
    elements.settingsYaml.value = await fetchText("/api/settings/yaml");
    elements.settingsYamlStatus.textContent = "Current configuration loaded.";
  } catch (error) {
    elements.settingsYamlStatus.textContent = error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  }
}

async function loadPortableYamlFile(event) {
  const [file] = event.target.files;
  event.target.value = "";

  if (!file) {
    return;
  }

  try {
    elements.settingsYaml.value = await file.text();
    elements.settingsYamlStatus.textContent = `${file.name} loaded. Review it and click Import and apply.`;
    elements.settingsYamlStatus.className = "portable-yaml-status";
  } catch (error) {
    elements.settingsYamlStatus.textContent = error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  }
}

async function copyPortableYaml() {
  await copyText(
    elements.settingsYaml.value,
    elements.copySettingsYaml,
    "YAML copied"
  );
  elements.settingsYamlStatus.textContent = "YAML copied.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

function downloadPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "There is no YAML to download.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  const url = URL.createObjectURL(
    new Blob(
      [yaml],
      {
        type: "application/yaml;charset=utf-8"
      }
    )
  );
  const link = document.createElement("a");
  link.href = url;
  link.download = "agentic-router.yaml";
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
  elements.settingsYamlStatus.textContent = "agentic-router.yaml prepared.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

async function createLocalBackup() {
  elements.localBackupStatus.textContent = "Creating file with manifest and hashes…";

  try {
    const response = await fetch("/api/recovery/backup", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        includeConversations: elements.backupConversations.checked,
        includeSessionSummaries: elements.backupSummaries.checked,
        includeUsageHistory: elements.backupUsage.checked,
        includeReviewData: elements.backupReviews.checked
      })
    });

    if (!response.ok) {
      throw new Error(await response.text());
    }

    const blob = await response.blob();
    const href = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = `agentic-router-backup-${new Date().toISOString().slice(0, 19).replaceAll(":", "-")}.zip`;
    anchor.click();
    URL.revokeObjectURL(href);
    elements.localBackupStatus.textContent =
      "Backup created without keys, approvals, or process state.";
  } catch (error) {
    elements.localBackupStatus.textContent = error.message;
  }
}

async function inspectLocalBackup() {
  const file = elements.backupRestoreFile.files?.[0];

  if (!file) {
    return;
  }

  elements.localBackupStatus.textContent = "Validating manifest and hashes…";

  try {
    const base64 = await fileToBase64(file);
    const inspection = await fetchJson("/api/recovery/backup/inspect", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        archiveBase64: base64
      })
    });
    state.inspectedBackup = inspection;
    state.inspectedBackupBase64 = base64;
    elements.restoreLocalBackup.disabled = false;
    elements.localBackupStatus.textContent =
      `${inspection.manifest.categories.join(", ")} · `
      + `${inspection.manifest.entries.length} files · valid hashes · `
      + `${inspection.conflicts.length} conflitos atuais`;
  } catch (error) {
    state.inspectedBackup = null;
    state.inspectedBackupBase64 = null;
    elements.restoreLocalBackup.disabled = true;
    elements.localBackupStatus.textContent = error.message;
  } finally {
    elements.backupRestoreFile.value = "";
  }
}

async function restoreLocalBackup() {
  const inspection = state.inspectedBackup;

  if (!inspection || !state.inspectedBackupBase64) {
    return;
  }

  const categories = inspection.manifest.categories;

  if (!await showAppConfirm(
    `Restore the categories ${categories.join(", ")}? `
      + "The current state will be saved before atomic application.",
    { title: "Restore backup?", confirmLabel: "Restore" }
  )) {
    return;
  }

  try {
    const result = await fetchJson("/api/recovery/backup/restore", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        archiveBase64: state.inspectedBackupBase64,
        categories,
        confirmed: true
      })
    });
    elements.localBackupStatus.textContent =
      `Restored: ${result.restoredCategories.join(", ")}. `
      + `Previous backup: ${result.currentDataBackup}. Restart to reload.`;
  } catch (error) {
    elements.localBackupStatus.textContent = error.message;
  }
}

function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      const value = String(reader.result);
      resolve(value.slice(value.indexOf(",") + 1));
    });
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

async function importPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "Enter a YAML configuration.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  if (
    state.settingsDirty
    && !await showAppConfirm(
      "Importing will replace this form's unsaved changes. Continue?",
      { title: "Import configuration?", confirmLabel: "Import" }
    )
  ) {
    return;
  }

  elements.importSettingsYaml.disabled = true;
  elements.settingsYamlStatus.textContent = "Validating and applying…";
  elements.settingsYamlStatus.className = "portable-yaml-status";

  try {
    state.settings = await fetchJson(
      "/api/settings/yaml",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          yaml
        })
      }
    );
    state.settingsDirty = false;
    updateSettingsDirtyState();
    renderSettings();
    await loadPortableYaml();
    elements.settingsYamlStatus.textContent = "YAML configuration imported and applied.";
    elements.settingsYamlStatus.className = "portable-yaml-status success";
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  } catch (error) {
    const errors = error.payload?.errors;
    elements.settingsYamlStatus.textContent = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  } finally {
    elements.importSettingsYaml.disabled = false;
  }
}

function selectSettingsSection(event) {
  setSettingsSection(
    event.currentTarget.dataset.settingsTarget,
    true
  );
}

function setSettingsSubsection(subsection, moveFocus) {
  const advancedSection = sectionElementById("settings-advanced");
  if (!advancedSection) {
    return;
  }

  const normalizedSubsection = subsection ?? "portable-yaml";
  const subsectionPanels = advancedSection.querySelectorAll(
    "[data-settings-subsection]"
  );
  let foundPanel = null;

  subsectionPanels.forEach(
    panel => {
      const isActive = panel.dataset.settingsSubsection === normalizedSubsection;
      panel.classList.toggle("active", isActive);
      if (isActive) {
        foundPanel = panel;
      }
    }
  );

  advancedSection.querySelectorAll(
    "[data-settings-subtarget]"
  ).forEach(
    button => button.setAttribute(
      "aria-current",
      button.dataset.settingsSubtarget === normalizedSubsection
        ? "page"
        : "false"
    )
  );

  if (!foundPanel) {
    return;
  }

  state.settingsSubsection = normalizedSubsection;
  if (moveFocus) {
    foundPanel.focus({
      preventScroll: true
    });
  }
}

function selectSettingsSubsection(event) {
  setSettingsSubsection(
    event.currentTarget.dataset.settingsSubtarget,
    true
  );
}

function setSettingsSection(section, moveFocus) {
  const normalizedSection = normalizeSettingsSection(section);
  const sectionIds = visibleSettingsSectionIds(normalizedSection);
  const allSections = document.querySelectorAll(".settings-section");
  const activeSections = sectionIds
    .map(
      sectionId => sectionElementById(sectionId)
    )
    .filter(Boolean);

  if (!activeSections.length) {
    return;
  }

  allSections.forEach(
    sectionElement => sectionElement.classList.remove("active")
  );
  activeSections.forEach(
    sectionElement => sectionElement.classList.add("active")
  );

  state.settingsSection = normalizedSection;
  elements.settingsSectionSelect.value = normalizedSection;
  elements.settingsDialog.dataset.section = normalizedSection;
  elements.settingsNavigation.querySelectorAll("[data-settings-target]").forEach(
    button => button.setAttribute(
      "aria-current",
      button.dataset.settingsTarget === normalizedSection
        ? "page"
        : "false"
    )
  );

  if (normalizedSection === "advanced") {
    setSettingsSubsection(
      state.settingsSubsection
    );
  }

  if (moveFocus) {
    activeSections[0].focus({
      preventScroll: true
    });
  }
}

function navigateToSettingsError(field) {
  const section = !field
    ? "general"
    : field === "execution.maxDirectPlanSteps" || field.startsWith("execution.phaseEffort")
      ? "general"
    : field.startsWith("ollama")
      ? "general"
      : field.startsWith("router") || field.startsWith("intentions")
        ? "models-routing"
        : field.startsWith("coordinator") || field.startsWith("action")
          ? "models-routing"
          : field.startsWith("execution")
            ? "execution"
            : field.startsWith("runtime") || field.startsWith("context")
              ? "harnesses"
              : field.startsWith("usage")
                ? "harnesses"
                : field.startsWith("validation")
                  ? "workspaces"
                  : field.startsWith("git")
                    ? "workspaces"
                    : field.startsWith("session")
                      ? "workspaces"
                      : "general";
  setSettingsSection(
    section,
    true
  );
  elements.settingsForm.querySelector(".field-invalid")?.focus();
}

function handleSettingsCancel(event) {
  event.preventDefault();
  closeSettings();
}

function openWorkspaceFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  const active = activeWorkspaceProfile();
  if (active) {
    openProjectEditor(active.id);
  } else {
    openWorkspace();
  }
}

function openRecentFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  elements.sessionHistory.scrollIntoView({
    block: "nearest"
  });
  elements.openSessionSearch.focus();
}

function openGitFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  void openGitPanel();
}

function openValidationFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  const active = activeWorkspaceProfile();
  if (active) {
    openProjectEditor(active.id);
    elements.validationProfileSection.open = true;
    elements.validationProfileName.focus();
  } else {
    openWorkspace();
  }
}

async function handleModelSelectionChange() {
  updateHarnessControls();
  updateInteractionControls();
  updateComposerModelTitle();
  updateComposerStatus();
  state.activeAgentModel = null;
  state.activeAgentRole = null;
  state.activeHarness = null;
  state.webEnabled = false;
  state.webControlState = "unavailable";
  await refreshSelectedModelCapabilities();
}

async function refreshSelectedModelCapabilities(
  model = null,
  role = null
) {
  const selected = model
    ?? state.activeAgentModel
    ?? elements.modelSelector.value;
  const capabilityModel = selected && selected !== "auto"
    ? selected
    : state.settings?.defaultModel;
  const requestId = ++state.capabilityRequestId;

  if (!capabilityModel) {
    state.modelCapability = null;
    renderCapabilityContext();
    return;
  }

  elements.activeProviderModel.textContent = "Checking capabilities…";

  try {
    const view = await fetchJson(
      `/api/capabilities/model?model=${encodeURIComponent(capabilityModel)}`
        + `&interactionMode=${encodeURIComponent(state.interactionMode)}`
        + `&harness=${encodeURIComponent(state.harness)}`
    );

    if (requestId !== state.capabilityRequestId) {
      return;
    }

    state.modelCapability = {
      ...view,
      role: role ?? state.activeAgentRole ?? view.role
    };

    state.webEnabled = Boolean(view.webAvailable);
    state.webControlState = state.webEnabled ? "enabled" : "unavailable";
  } catch (error) {
    if (requestId !== state.capabilityRequestId) {
      return;
    }

    state.modelCapability = {
      model: capabilityModel,
      provider: providerFromModel(capabilityModel),
      providerDisplayName: providerLabel(providerFromModel(capabilityModel)),
      role: role ?? "primary",
      capabilities: null,
      webAvailable: false,
      webUnavailableReason: error.message
    };
    state.webEnabled = false;
    state.webControlState = "unavailable";
  }

  renderCapabilityContext();
  renderPendingContextUsage();
}

function renderCapabilityContext() {
  const view = state.modelCapability;
  elements.capabilityTags.replaceChildren();

  if (!view?.capabilities) {
    elements.activeProviderModel.textContent =
      view?.webUnavailableReason ?? "Capabilities unavailable";
    renderWebControl();
    updateImageAttachmentControls();
    elements.fallbackIndicator.hidden = true;
    return;
  }

  const capabilities = view.capabilities;
  const routerConfigurationDocumentation =
    "https://github.com/Shakansis/agentic-router#configuration";
  const routerCapabilityDocumentation =
    "https://github.com/Shakansis/agentic-router#web-search-citations-and-image-input";
  const isLocal = view.provider === "ollama-local";
  elements.activeProviderModel.textContent =
    `${view.providerDisplayName} · ${view.model}`;
  const tags = [
    {
      label: isLocal ? "Local" : "Cloud",
      kind: isLocal ? "local" : "cloud",
      status: "Active in this conversation",
      enabled: true,
      description: isLocal
        ? `Model ${view.model} is running through Ollama Local.`
        : `Model ${view.model} is running through provider ${view.providerDisplayName}.`,
      documentationUrl: isLocal
        ? "https://docs.ollama.com/api/introduction"
        : routerConfigurationDocumentation
    },
    capabilities.nativeTools
      ? {
        label: "Tools",
        kind: "tools",
        status: capabilities.toolProtocolConfirmed
          ? "Enabled and confirmed"
          : "Enabled; behavioral confirmation pending",
        enabled: true,
        description: capabilities.toolProtocolConfirmed
          ? "The model can call structured tools and that protocol has been confirmed by the Router."
          : `The model advertises tool calls in ${capabilities.source}; behavioral conformance is verified separately.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/tool-calling"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.webSearch
      ? {
        label: "Web",
        kind: "web",
        status: state.webEnabled
          ? "Automatically available for this route"
          : "Unavailable for this route",
        enabled: state.webEnabled,
        description: state.modelCapability.webSource === "provider-native"
          ? "Provider-native web search is automatically available; the provider decides when current web evidence is needed."
          : state.modelCapability.webSource === "harness-native"
            ? "The selected harness exposes its native web tools automatically; the model decides when current web evidence is needed."
            : state.modelCapability.webSource === "host-and-harness-native"
              ? "Both harness-native web tools and bounded Agentic Router Host web_search are automatically available."
              : "The Agentic Router Host automatically offers bounded read-only web search; the model decides when current web evidence is needed.",
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/web-search"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.vision
      ? {
        label: "Vision",
        kind: "vision",
        status: "Enabled for this model",
        enabled: true,
        description: `Accepts up to ${capabilities.maximumImageCount} images, with ${formatBytes(capabilities.maximumImageBytes)} per image.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/vision"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.structuredOutput
      ? {
        label: "Structured",
        kind: "structured",
        status: "Enabled for this model",
        enabled: true,
        description: `The model can respond using a structured schema; evidence from ${capabilities.source}.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/structured-outputs"
          : routerCapabilityDocumentation
      }
      : null,
    {
      label: view.role === "fallback" ? "Fallback" : "Primary",
      kind: view.role === "fallback" ? "fallback" : "primary",
      status: "Active role in this conversation",
      enabled: true,
      description: view.role === "fallback"
        ? "This model is serving as the configured fallback after eligible primary-model unavailability."
        : "This model is the primary target selected for this conversation.",
      documentationUrl: routerConfigurationDocumentation
    }
  ].filter(Boolean);

  for (const [index, tag] of tags.entries()) {
    const container = document.createElement("span");
    const trigger = document.createElement("button");
    const popover = document.createElement("span");
    const heading = document.createElement("strong");
    const status = document.createElement("span");
    const description = document.createElement("span");
    const documentation = document.createElement("a");
    const popoverId = `capability-help-${tag.kind}-${index}`;

    container.className = "capability-info";
    trigger.type = "button";
    trigger.className = "capability-tag";
    trigger.dataset.kind = tag.kind;
    trigger.textContent = tag.label;
    trigger.setAttribute("aria-expanded", "false");
    trigger.setAttribute("aria-controls", popoverId);
    trigger.setAttribute("aria-haspopup", "dialog");
    trigger.setAttribute(
      "aria-label",
      `${tag.label}: ${tag.status}. Open details.`
    );

    popover.id = popoverId;
    popover.className = "capability-popover";
    popover.setAttribute("role", "dialog");
    popover.setAttribute("aria-label", `Details for ${tag.label}`);
    heading.textContent = tag.label;
    status.className = "capability-popover-status";
    status.dataset.enabled = String(tag.enabled);
    status.textContent = tag.status;
    description.className = "capability-popover-description";
    description.textContent = tag.description;
    documentation.className = "capability-popover-link";
    documentation.href = tag.documentationUrl;
    documentation.target = "_blank";
    documentation.rel = "noopener noreferrer";
    documentation.textContent = "Learn more in the official documentation ↗";
    popover.append(heading, status, description, documentation);
    container.append(trigger, popover);
    elements.capabilityTags.append(container);
  }

  elements.fallbackIndicator.hidden =
    view.provider === "ollama-local"
    || !hasConfiguredLocalFallback(view.model);
  renderWebControl();
  updateImageAttachmentControls();
}

function handleCapabilityTagClick(event) {
  const trigger = event.target.closest(".capability-tag");
  if (!trigger) {
    return;
  }

  event.stopPropagation();
  const container = trigger.closest(".capability-info");
  const shouldOpen = container.dataset.open !== "true";
  closeCapabilityPopovers();
  if (shouldOpen) {
    container.dataset.open = "true";
    trigger.setAttribute("aria-expanded", "true");
  }
}

function handleCapabilityDocumentClick(event) {
  if (!event.target.closest(".capability-info")) {
    closeCapabilityPopovers();
  }
}

function handleCapabilityKeyDown(event) {
  if (event.key !== "Escape") {
    return;
  }

  if (!elements.projectMenuPopover.hidden) {
    event.preventDefault();
    const anchor = projectMenuAnchor;
    closeProjectMenu();
    anchor?.focus();
    return;
  }

  const openTrigger = elements.capabilityTags.querySelector(
    ".capability-info[data-open=\"true\"] .capability-tag"
  );
  if (!openTrigger) {
    return;
  }

  event.preventDefault();
  closeCapabilityPopovers();
  openTrigger.focus();
}

function closeCapabilityPopovers() {
  for (const container of elements.capabilityTags.querySelectorAll(
    ".capability-info[data-open=\"true\"]"
  )) {
    delete container.dataset.open;
    container.querySelector(".capability-tag")?.setAttribute(
      "aria-expanded",
      "false"
    );
  }
}

function renderWebControl() {
  const available = Boolean(state.modelCapability?.webAvailable);
  state.webEnabled = available;
  state.webControlState = available ? "enabled" : "unavailable";

  const labels = {
    unavailable: "Web unavailable",
    enabled: "Web available automatically"
  };
  elements.webToggle.dataset.state = state.webControlState;
  elements.webToggleLabel.textContent = labels[state.webControlState];
  elements.webToggle.setAttribute(
    "aria-label",
    labels[state.webControlState]
  );
  elements.webToggle.disabled = true;
  elements.webToggle.setAttribute(
    "aria-pressed",
    String(state.webEnabled)
  );
    elements.webToggle.title = available
      ? state.modelCapability.webSource === "provider-native"
        ? "Official provider search is automatically available for this route."
        : state.modelCapability.webSource === "harness-native"
          ? "Harness-native web search is automatically available; the model decides when to use it."
          : state.modelCapability.webSource === "host-and-harness-native"
            ? "Harness-native and bounded Host web search are automatically available."
            : "Bounded Host web search is automatically available; the model decides when to use it."
      : state.modelCapability?.webUnavailableReason
      ?? "No authorized search integration is available.";
}

function providerFromModel(model) {
  const separator = model.indexOf("::");
  return separator > 0 ? model.slice(0, separator) : "ollama-local";
}

function providerLabel(provider) {
  return {
    "ollama-local": "Ollama Local",
    groq: "Groq",
    "google-ai-studio": "Google AI Studio",
    cerebras: "Cerebras"
  }[provider] ?? provider;
}

function hasConfiguredLocalFallback(model) {
  return Object.values(state.settings?.intentions ?? {}).some(
    intention => {
      const primary = intention.model === "default"
        ? state.settings.defaultModel
        : intention.model;
      const fallback = intention.fallbackModel === "default"
        ? state.settings.defaultModel
        : intention.fallbackModel;
      return primary === model
        && fallback
        && fallback !== "none"
        && providerFromModel(fallback) === "ollama-local";
    }
  );
}

async function handleImageSelection(event) {
  await addImageFiles(event.currentTarget.files);
  event.currentTarget.value = "";
}

function handleImagePaste(event) {
  const files = Array.from(event.clipboardData?.files ?? []).filter(
    file => file.type.startsWith("image/")
  );

  if (files.length > 0) {
    event.preventDefault();
    void addImageFiles(files);
  }
}

function handleImageDragOver(event) {
  if (Array.from(event.dataTransfer?.items ?? []).some(
    item => item.kind === "file"
  )) {
    event.preventDefault();
    elements.composer.classList.add("drag-active");
  }
}

function handleImageDragLeave(event) {
  if (!elements.composer.contains(event.relatedTarget)) {
    elements.composer.classList.remove("drag-active");
  }
}

function handleImageDrop(event) {
  event.preventDefault();
  elements.composer.classList.remove("drag-active");
  void addImageFiles(event.dataTransfer?.files);
}

async function addImageFiles(fileList) {
  if (elements.attachImage.disabled) {
    elements.composerStatus.textContent = elements.attachImage.title;
    return;
  }
  const files = Array.from(fileList ?? []);
  const acceptedTypes = new Set([
    "image/jpeg",
    "image/png",
    "image/webp",
    "image/gif"
  ]);

  if (state.attachments.length + files.length > 4) {
    elements.composerStatus.textContent = "Up to 4 images per request.";
    return;
  }

  for (const file of files) {
    if (!acceptedTypes.has(file.type)) {
      elements.composerStatus.textContent =
        "Only JPEG, PNG, WebP, and GIF are accepted; SVG is not allowed.";
      continue;
    }

    if (file.size <= 0 || file.size > 10 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "Each image must be no larger than 10 MiB.";
      continue;
    }

    const total = state.attachments.reduce(
      (sum, attachment) => sum + attachment.declaredBytes,
      file.size
    );

    if (total > 20 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "Combined images must be no larger than 20 MiB.";
      break;
    }

    const dataUrl = await readFileDataUrl(file);
    state.attachments.push({
      id: createSessionId(),
      fileName: file.name || "clipboard-image",
      mimeType: file.type,
      declaredBytes: file.size,
      base64Data: dataUrl.slice(dataUrl.indexOf(",") + 1),
      previewUrl: URL.createObjectURL(file)
    });
  }

  renderAttachmentPreviews();
  updateComposerStatus();
}

function readFileDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => resolve(String(reader.result)));
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

function renderAttachmentPreviews() {
  elements.attachmentPreviews.replaceChildren();
  elements.attachmentPreviews.hidden = state.attachments.length === 0;

  for (const attachment of state.attachments) {
    const preview = document.createElement("figure");
    preview.className = "attachment-preview";
    const image = document.createElement("img");
    image.src = attachment.previewUrl;
    image.alt = attachment.fileName;
    const caption = document.createElement("span");
    caption.textContent = attachment.fileName;
    caption.title = `${attachment.fileName} · ${formatBytes(attachment.declaredBytes)}`;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attachment-remove";
    remove.dataset.attachmentRemove = attachment.id;
    remove.setAttribute("aria-label", `Remove ${attachment.fileName}`);
    remove.title = `Remove ${attachment.fileName}`;
    remove.textContent = "×";
    preview.append(image, caption, remove);
    elements.attachmentPreviews.append(preview);
  }
}

function handleAttachmentPreviewClick(event) {
  const button = event.target.closest("[data-attachment-remove]");

  if (!button) {
    return;
  }

  const index = state.attachments.findIndex(
    attachment => attachment.id === button.dataset.attachmentRemove
  );

  if (index >= 0) {
    URL.revokeObjectURL(state.attachments[index].previewUrl);
    state.attachments.splice(index, 1);
    renderAttachmentPreviews();
    updateComposerStatus();
  }
}

function clearAttachments() {
  for (const attachment of state.attachments) {
    URL.revokeObjectURL(attachment.previewUrl);
  }

  state.attachments = [];
  renderAttachmentPreviews();
}

async function ensureCloudImageApproval(model) {
  if (state.attachments.length === 0) {
    return true;
  }

  if (!model || model === "auto") {
    elements.composerStatus.textContent =
      "Explicitly select a Vision-capable model before sending images.";
    return false;
  }

  const provider = providerFromModel(model);

  if (provider === "ollama-local") {
    return true;
  }

  const key = `${state.browserSessionId}\n${provider}`;

  if (state.cloudImageApprovals.has(key)) {
    return true;
  }

  const bytes = state.attachments.reduce(
    (sum, attachment) => sum + attachment.declaredBytes,
    0
  );
  const approved = await showAppConfirm(
    `${providerLabel(provider)} will receive ${formatBytes(bytes)} of images. `
      + "These bytes will leave this computer. Authorize this provider for this session?",
    { title: "Authorize image submission?", confirmLabel: "Authorize" }
  );

  if (!approved) {
    elements.composerStatus.textContent = "Cloud image submission was not authorized.";
    return false;
  }

  await fetchJson(
    "/api/privacy/cloud-images/approve",
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        browserSessionId: state.browserSessionId,
        provider
      })
    }
  );
  state.cloudImageApprovals.add(key);
  return true;
}

async function resetCloudImagePrivacy(browserSessionId) {
  state.cloudImageApprovals.clear();
  state.webEnabled = Boolean(state.modelCapability?.webAvailable);
  state.webControlState = state.webEnabled ? "enabled" : "unavailable";
  clearAttachments();

  if (!browserSessionId) {
    return;
  }

  try {
    await fetchJson(
      "/api/privacy/cloud-images/reset",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ browserSessionId })
      }
    );
  } catch {
    // The old browser-session identifier is no longer reused.
  }
}

