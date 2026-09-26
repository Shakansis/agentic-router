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

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KiB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}

function formatPercent(value) {
  return value == null
    ? "n/d"
    : `${Math.round(value)}%`;
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  let payload = null;
  if (response.status !== 204) {
    try {
      payload = await response.json();
    } catch {
      // Development error pages and empty gateway responses are not JSON.
      // Keep the HTTP failure actionable without exposing raw server output.
      const error = new Error(
        response.ok
          ? "The application returned an unreadable response. Please try again."
          : `The application could not complete the request (HTTP ${response.status}). Please try again.`
      );
      error.status = response.status;
      throw error;
    }
  }

  if (!response.ok) {
    const error = new Error(
      payload?.message
      ?? payload?.detail
      ?? payload?.diagnostic
      ?? `HTTP ${response.status}`
    );
    error.payload = payload;
    error.status = response.status;
    throw error;
  }

  return payload;
}

async function fetchText(url, options) {
  const response = await fetch(url, options);
  const payload = await response.text();

  if (!response.ok) {
    let message = `HTTP ${response.status}`;

    try {
      message = JSON.parse(payload)?.message ?? message;
    } catch {
      if (payload.trim()) {
        message = payload.trim();
      }
    }

    throw new Error(message);
  }

  return payload;
}

function restoreBenchmarkBatch() {
  try {
    const stored = JSON.parse(sessionStorage.getItem(benchmarkBatchStorageKey));
    if (
      !stored
      || !Number.isInteger(stored.total)
      || stored.total < 1
      || stored.total > 20
      || !Number.isInteger(stored.started)
      || stored.started < 0
      || stored.started > stored.total
      || !Number.isInteger(stored.completed)
      || stored.completed < 0
      || stored.completed > stored.started
      || !Array.isArray(stored.runIds)
      || !stored.request
      || !Array.isArray(stored.request.models)
      || !Array.isArray(stored.request.harnesses)
      || !Array.isArray(stored.request.suites)
    ) {
      sessionStorage.removeItem(benchmarkBatchStorageKey);
      return null;
    }
    return stored;
  } catch {
    try {
      sessionStorage.removeItem(benchmarkBatchStorageKey);
    } catch {
      // Session storage is optional; the active page still owns the batch.
    }
    return null;
  }
}

function persistBenchmarkBatch() {
  try {
    if (state.benchmarkBatch) {
      sessionStorage.setItem(
        benchmarkBatchStorageKey,
        JSON.stringify(state.benchmarkBatch)
      );
    } else {
      sessionStorage.removeItem(benchmarkBatchStorageKey);
    }
  } catch {
    // Session storage is optional; the active page still owns the batch.
  }
}

function clearBenchmarkBatch() {
  state.benchmarkBatch = null;
  persistBenchmarkBatch();
}

function createSessionId() {
  return globalThis.crypto?.randomUUID?.()
    ?? `browser-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function restoreBrowserSessionId() {
  try {
    const stored = sessionStorage.getItem(browserSessionStorageKey);
    if (stored && stored.length <= 128) {
      return stored;
    }
  } catch {
    // Browser storage is optional; a page-local identity remains valid.
  }

  const created = createSessionId();
  persistBrowserSessionId(created);
  return created;
}

function persistBrowserSessionId(browserSessionId) {
  try {
    sessionStorage.setItem(browserSessionStorageKey, browserSessionId);
  } catch {
    // Browser storage is optional; a page-local identity remains valid.
  }
}

function toCamelCase(value) {
  return value.replace(
    /-([a-z])/g,
    (_, letter) => letter.toUpperCase()
  );
}
