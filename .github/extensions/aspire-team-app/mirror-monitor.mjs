import { loadMirrorObservation, updateMirrorState, MIRROR_ID, MIRROR_URL, GITHUB_URL } from "./mirror.mjs";
import { associateHealthSources, healthCounts } from "./health.mjs";

export const MIRROR_POLL_INTERVAL = 15 * 60 * 1000;

// This timer belongs to the extension session, not an iframe or the selected tab.
export function createMirrorMonitor({
  getTokens,
  loadState,
  saveState,
  observe = loadMirrorObservation,
  onChange = () => {},
  log = () => {},
  now = () => new Date(),
  setTimer = setInterval,
  clearTimer = clearInterval,
}) {
  let state = null;
  let enabled = false;
  let inflight = null;
  let timer = null;
  let lastAttempt = null;

  async function check() {
    const checkedAt = now().toISOString();
    try {
      const tokens = await getTokens();
      enabled = tokens !== null;
      if (!enabled) {
        state = null;
        await onChange();
        return state;
      }
      // Re-read durable state so a reopened session retains notification acknowledgements
      // and the lower-bound clock even when the canvas has never been opened here.
      state = await loadState();
      if (!state) {
        state = { level: "checking", checkedAt };
        await onChange();
      }
      let observation;
      let failure;
      for (const token of tokens.length ? tokens : [null]) {
        try {
          if (!token) throw new Error("GitHub authentication is unavailable. Enable an account that can read microsoft/aspire.");
          observation = await observe({ token, now: now() });
          break;
        } catch (error) {
          failure = error;
        }
      }
      if (!observation) throw failure;
      const next = updateMirrorState(state, observation, now());
      await saveState(next);
      state = next;
    } catch (error) {
      enabled = true;
      state = {
        ...state,
        level: "unknown",
        checkedAt,
        error: error.message,
      };
      // Keep the last successful durable observation intact on access/storage failures.
      try {
        await log(`Mirror monitor unavailable: ${error.message}`);
      } catch {
        // A disconnected timeline must not prevent the canvas from showing Unknown.
      }
    }
    await onChange();
    return state;
  }

  function refresh(force = false) {
    if (inflight) return inflight;
    if (!force && lastAttempt !== null && now().getTime() - lastAttempt < MIRROR_POLL_INTERVAL) {
      return Promise.resolve(state);
    }
    lastAttempt = now().getTime();
    inflight = check().finally(() => { inflight = null; });
    return inflight;
  }

  function start() {
    if (timer !== null) return;
    const tick = () => {
      refresh(true).catch((error) => {
        // Logging itself may fail during session shutdown.
        Promise.resolve().then(() => log(`Mirror monitor refresh failed: ${error.message}`)).catch(() => {});
      });
    };
    timer = setTimer(tick, MIRROR_POLL_INTERVAL);
    timer?.unref?.();
    tick();
  }

  return {
    start,
    stop() { if (timer !== null) clearTimer(timer); timer = null; },
    invalidate() { lastAttempt = null; },
    refresh,
    getState: () => enabled ? state : null,
  };
}

export function mirrorHealthItem(record, now = new Date()) {
  if (!record) return null;
  const observation = record.observation;
  const ageStart = observation?.landedAt || record.firstObservedAt;
  const ageEnd = record.level === "unknown" ? Date.parse(observation?.checkedAt) : now.getTime();
  const age = ageStart ? Math.max(0, (ageEnd - Date.parse(ageStart)) / 3_600_000) : null;
  const ageText = Number.isFinite(age)
    ? `${observation?.landedAt ? "" : "at least "}${Math.floor(age)}h`
    : "not established";
  const labels = { checking: "Checking", synced: "In sync", "catching-up": "Catching up", warning: "Warning", critical: "Critical", diverged: "History differs", unknown: "Unknown" };
  const states = { checking: "running", synced: "healthy", "catching-up": "running", warning: "degraded", critical: "failing", diverged: "failing", unknown: "unknown" };
  const level = Object.hasOwn(labels, record.level) ? record.level : "unknown";
  const summary = level === "unknown"
    ? `${record.error || "Mirror status is unavailable."}${observation ? " Last successful data is stale." : ""}`
    : level === "checking"
      ? "Checking GitHub and internal main tips and arrival-time evidence."
    : level === "synced"
      ? "GitHub and the internal mirror have the same main tip."
      : level === "diverged"
        ? "The branch histories differ. Investigate before interpreting this as mirror lag."
        : `The oldest outstanding main change has waited ${ageText}. Warning at 6h; critical at 24h.`;
  return {
    id: MIRROR_ID,
    provider: "mirror",
    name: "Internal mirror",
    repository: "microsoft/aspire",
    mappedRepository: "microsoft/aspire",
    groupId: "repository:github.com/microsoft/aspire",
    groupName: "microsoft/aspire",
    groupMatch: "provider",
    branch: "main",
    url: MIRROR_URL,
    state: states[level],
    statusLabel: labels[level],
    mirror: {
      sourceSha: observation?.sourceSha ?? null,
      mirrorSha: observation?.mirrorSha ?? null,
      missingCount: observation?.missingCount ?? null,
      ageText: level === "synced" ? "None" : ageText,
      lastSuccessAt: record.lastSuccessAt ?? observation?.checkedAt ?? null,
      checkedAt: record.checkedAt ?? observation?.checkedAt ?? null,
      stale: level === "unknown",
    },
    reasons: [{ code: `mirror_${level.replaceAll("-", "_")}`, summary }],
    evidence: [
      { label: "GitHub main", detail: observation?.sourceSha ?? "Not read", url: `${GITHUB_URL}/tree/main` },
      { label: "Internal main", detail: observation?.mirrorSha ?? "Not read", url: MIRROR_URL },
      ...(observation?.compareUrl ? [{ label: "Outstanding changes", detail: "Pinned comparison", url: observation.compareUrl }] : []),
    ],
  };
}

export function decorateMirrorDashboard(dashboard, record, prefs, now = new Date()) {
  const item = mirrorHealthItem(record, now);
  dashboard.mirror = item;
  dashboard.notifications = (dashboard.notifications ?? []).filter((notification) => notification.kind !== "mirror");
  if (item && prefs.notifications?.mirrorLag !== false) {
    const dismissed = new Set(prefs.dismissedNotifications ?? []);
    for (const notification of record.notifications ?? []) {
      if (dismissed.has(notification.id)) continue;
      const recovery = notification.level === "recovery";
      dashboard.notifications.push({
        id: notification.id,
        kind: "mirror",
        tone: recovery ? "success" : notification.level === "critical" ? "danger" : "warning",
        title: recovery ? "Internal mirror recovered" : `Internal mirror: ${notification.level}`,
        detail: `${notification.at} - main${item.mirror.stale ? " (current status unavailable)" : ""}`,
        repository: "microsoft/aspire",
        url: MIRROR_URL,
      });
    }
  }
  if (dashboard.mode === "health") {
    const items = (dashboard.health?.items ?? []).filter((source) => source.id !== MIRROR_ID);
    if (item) items.push(item);
    const counts = healthCounts(items);
    dashboard.health = { ...dashboard.health, items: associateHealthSources(items), counts };
    dashboard.counts = counts;
  }
  return dashboard;
}
