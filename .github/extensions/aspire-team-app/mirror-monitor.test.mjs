import assert from "node:assert/strict";
import test from "node:test";
import { createMirrorMonitor, decorateMirrorDashboard, mirrorHealthItem, MIRROR_POLL_INTERVAL } from "./mirror-monitor.mjs";
import { MIRROR_ID } from "./mirror.mjs";

const time = new Date("2026-09-17T12:00:00Z");
const observation = {
  status: "synced",
  sourceSha: "a".repeat(40),
  mirrorSha: "a".repeat(40),
  missingCount: 0,
  oldestSha: null,
  landedAt: null,
  checkedAt: time.toISOString(),
  compareUrl: null,
};

function harness(overrides = {}) {
  let persisted = null;
  const logs = [];
  const monitor = createMirrorMonitor({
    getTokens: async () => ["token"],
    loadState: async () => persisted,
    saveState: async (state) => { persisted = state; },
    observe: async () => observation,
    now: () => time,
    log: (message) => { logs.push(message); },
    ...overrides,
  });
  return { monitor, logs, saved: () => persisted };
}

test("session monitor starts without a canvas, uses fifteen-minute cadence and stops cleanly", async () => {
  let callback;
  let cleared;
  let unref = false;
  const timer = { unref() { unref = true; } };
  let checks = 0;
  const { monitor } = harness({
    setTimer(fn, ms) { assert.equal(ms, MIRROR_POLL_INTERVAL); callback = fn; return timer; },
    clearTimer(value) { cleared = value; },
    observe: async () => { checks++; return observation; },
  });
  monitor.start();
  monitor.start();
  await monitor.refresh();
  assert.equal(checks, 1);
  assert.equal(unref, true);
  callback();
  await monitor.refresh();
  assert.equal(checks, 2);
  monitor.stop();
  assert.equal(cleared, timer);
});

test("concurrent checks share a single query and cached reads do not poll again", async () => {
  let release;
  let checking;
  let checks = 0;
  const gate = new Promise((resolve) => { release = resolve; });
  const started = new Promise((resolve) => { checking = resolve; });
  const { monitor } = harness({
    observe: async () => { checks++; await gate; return observation; },
    onChange: () => checking(),
  });
  const first = monitor.refresh(true);
  assert.equal(monitor.refresh(true), first);
  await started;
  assert.equal(monitor.getState().level, "checking");
  assert.equal(mirrorHealthItem(monitor.getState()).statusLabel, "Checking");
  release();
  await first;
  await monitor.refresh();
  assert.equal(checks, 1);
});

test("query failure preserves last successful evidence, is unknown, and never persists false recovery", async () => {
  let fail = false;
  const { monitor, saved, logs } = harness({
    observe: async () => {
      if (fail) throw new Error("Azure DevOps authentication unavailable");
      return observation;
    },
  });
  await monitor.refresh(true);
  const previous = saved();
  fail = true;
  await monitor.refresh(true);
  assert.equal(monitor.getState().level, "unknown");
  assert.deepEqual(monitor.getState().observation, observation);
  assert.equal(saved(), previous);
  assert.equal(logs.length, 1);
  const item = mirrorHealthItem(monitor.getState(), time);
  assert.equal(item.mirror.stale, true);
  assert.equal(item.state, "unknown");
  assert.match(item.reasons[0].summary, /authentication unavailable/);
});

test("unwatched repository disables monitoring; missing credentials remain visible", async () => {
  const disabled = harness({ getTokens: async () => null, observe: () => assert.fail("must not query") });
  await disabled.monitor.refresh(true);
  assert.equal(disabled.monitor.getState(), null);
  const unauthenticated = harness({ getTokens: async () => [] });
  await unauthenticated.monitor.refresh(true);
  assert.equal(unauthenticated.monitor.getState().level, "unknown");
  assert.match(unauthenticated.monitor.getState().error, /GitHub authentication/);
});

test("another active account can recover a failed GitHub query", async () => {
  const attempts = [];
  const { monitor } = harness({
    getTokens: async () => ["expired", "working"],
    observe: async ({ token }) => {
      attempts.push(token);
      if (token === "expired") throw new Error("GitHub access unavailable");
      return observation;
    },
  });
  await monitor.refresh(true);
  assert.deepEqual(attempts, ["expired", "working"]);
  assert.equal(monitor.getState().level, "synced");
});

test("persistence failure is visible and does not claim a successful check", async () => {
  const { monitor, logs } = harness({ saveState: async () => { throw new Error("Storage unavailable"); } });
  await monitor.refresh(true);
  assert.equal(monitor.getState().level, "unknown");
  assert.match(logs[0], /Storage unavailable/);
});

test("mirror cards and notifications are provider-neutral, dismissible and available in every mode", () => {
  const record = {
    level: "critical",
    firstObservedAt: "2026-09-16T10:00:00Z",
    observation: { ...observation, status: "behind", missingCount: 10 },
    notifications: [
      { id: "warning-1", level: "warning", at: "2026-09-16T16:00:00Z" },
      { id: "critical-1", level: "critical", at: "2026-09-17T10:00:00Z" },
    ],
  };
  for (const mode of ["review", "issues", "ship", "health"]) {
    const dashboard = decorateMirrorDashboard({ mode }, record, { dismissedNotifications: ["warning-1"] }, time);
    assert.equal(dashboard.mirror.statusLabel, "Critical");
    assert.equal(dashboard.mirror.mirror.ageText, "at least 26h");
    assert.deepEqual(dashboard.notifications.map((n) => n.id), ["critical-1"]);
    if (mode === "health") {
      assert.equal(dashboard.health.items[0].id, MIRROR_ID);
      assert.equal(dashboard.health.items[0].groupId, "repository:github.com/microsoft/aspire");
      assert.equal(dashboard.counts.failing, 1);
    }
    decorateMirrorDashboard(dashboard, record, { notifications: { mirrorLag: false } }, time);
    assert.deepEqual(dashboard.notifications, []);
    assert.equal(dashboard.mirror.statusLabel, "Critical");
  }
});

test("redecorating preserves other notifications and removes a disabled mirror", () => {
  const pr = { id: "pr-1", title: "Review requested" };
  const dashboard = { mode: "health", notifications: [pr] };
  const record = { level: "synced", observation, notifications: [{ id: "recovered", level: "recovery", at: time.toISOString() }] };
  decorateMirrorDashboard(dashboard, record, {}, time);
  decorateMirrorDashboard(dashboard, record, {}, time);
  assert.deepEqual(dashboard.notifications.map((n) => n.id), ["pr-1", "recovered"]);
  assert.equal(dashboard.health.items.length, 1);
  decorateMirrorDashboard(dashboard, null, {}, time);
  assert.deepEqual(dashboard.notifications, [pr]);
  assert.deepEqual(dashboard.health.items, []);
});
