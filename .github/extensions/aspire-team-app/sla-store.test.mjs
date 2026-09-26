import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

const repo = "coreai/aspire-1p";
const first = "2025-01-06T17:00:00.000Z";
const later = "2025-01-06T18:00:00.000Z";
const latest = "2025-01-06T19:00:00.000Z";
const moduleUrl = new URL("./sla.mjs", import.meta.url).href;

// Pause the actual store read inside one process, and report a competing process's
// failed lock acquisition. IPC barriers force the race without sleep-based ordering.
const workerSource = `
import fs from "node:fs/promises";
import { once } from "node:events";
import { syncBuiltinESMExports } from "node:module";
const options = JSON.parse(process.env.SLA_TEST_OPTIONS);
const originalRead = fs.readFile;
const originalRename = fs.rename;
let paused = false;
fs.readFile = async (...args) => {
  const raw = await originalRead(...args);
  if (String(args[0]).endsWith("sla-tracking.json") && options.pause && !paused) {
    paused = true;
    const resume = once(process, "message");
    process.send({ type: "loaded" });
    await resume;
  }
  return raw;
};
fs.rename = async (...args) => {
  if (options.failSave && String(args[1]).endsWith("sla-tracking.json")) {
    throw Object.assign(new Error("simulated save failure"), { code: "EACCES" });
  }
  try {
    return await originalRename(...args);
  } catch (error) {
    if (String(args[1]).endsWith("sla-tracking.json.lock")) {
      process.send({ type: "contended" });
    }
    throw error;
  }
};
syncBuiltinESMExports();
try {
  const { annotateDashboardSla } = await import(process.env.SLA_TEST_MODULE);
  const cards = options.numbers.map((number) => ({ pr: {
    repository: "${repo}", number, author: "external-dev",
    url: "https://msft.ghe.com/${repo}/pull/" + number,
    review: { reviewerCount: 0 },
  } }));
  await annotateDashboardSla({ attention: { slaCandidates: cards } }, {
    now: Date.parse(options.now), authoritativeRepos: new Set(["${repo}"]),
  });
  process.send({ type: "done" });
} catch (error) {
  process.send({ type: "error", message: error.message, cause: error.cause?.message });
  process.exitCode = 1;
}
process.disconnect();
`;

async function store(t, content = { prs: {
  [`${repo}#1`]: { firstQualifiedAt: first },
  [`${repo}#99`]: { firstQualifiedAt: first },
  "devdiv-microsoft/aspire-1p#7": { firstQualifiedAt: first },
} }) {
  const home = await mkdtemp(join(tmpdir(), "aspire-sla-store-"));
  const workers = [];
  t.after(async () => {
    for (const { child } of workers) {
      if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
    }
    await Promise.all(workers.map((item) => item.exited));
    await rm(home, { recursive: true, force: true });
  });
  const directory = join(home, "extensions", "aspire-team-app", "artifacts");
  await mkdir(directory, { recursive: true });
  const file = join(directory, "sla-tracking.json");
  await writeFile(file, typeof content === "string" ? content : JSON.stringify(content));
  return { directory, file, start(options) {
    const running = worker(home, options);
    workers.push(running);
    return running;
  } };
}

function worker(home, options) {
  const child = spawn(process.execPath, ["--input-type=module", "--eval", workerSource], {
    env: { ...process.env, COPILOT_HOME: home, SLA_TEST_MODULE: moduleUrl, SLA_TEST_OPTIONS: JSON.stringify(options) },
    stdio: ["ignore", "ignore", "pipe", "ipc"],
  });
  let stderr = "";
  child.stderr.on("data", (data) => { stderr += data; });
  const messages = [];
  const waiters = new Map();
  child.on("message", (message) => {
    messages.push(message);
    waiters.get(message.type)?.resolve(message);
  });
  const exited = new Promise((resolve) => {
    child.once("exit", (code, signal) => {
      for (const waiter of waiters.values()) waiter.reject(new Error(`Worker exited ${code}/${signal}: ${stderr}`));
      resolve({ code, signal });
    });
  });
  return {
    child,
    exited,
    waitFor(type) {
      const message = messages.find((item) => item.type === type);
      if (message) return Promise.resolve(message);
      return new Promise((resolve, reject) => { waiters.set(type, { resolve, reject }); });
    },
  };
}

test("store serializes two processes without resetting newly added or legacy clocks", { timeout: 20_000 }, async (t) => {
  const { start, file, directory } = await store(t);
  const a = start({ numbers: [1, 2], now: later, pause: true });
  await a.waitFor("loaded");
  const b = start({ numbers: [1, 2, 3], now: latest });
  await b.waitFor("contended");
  a.child.send("resume");
  await Promise.all([a.waitFor("done"), b.waitFor("done")]);
  await Promise.all([a.exited, b.exited]);
  assert.deepEqual(JSON.parse(await readFile(file, "utf8")), { prs: {
    [`${repo}#1`]: { firstQualifiedAt: first },
    "devdiv-microsoft/aspire-1p#7": { firstQualifiedAt: first },
    [`${repo}#2`]: { firstQualifiedAt: later },
    [`${repo}#3`]: { firstQualifiedAt: latest },
  } });
  assert.deepEqual(await readdir(directory), ["sla-tracking.json"]);
});

test("store recovers an exited owner's lock even with two competing recoverers", { timeout: 20_000 }, async (t) => {
  const { start, file, directory } = await store(t);
  const abandoned = start({ numbers: [1, 2], now: later, pause: true });
  await abandoned.waitFor("loaded");
  abandoned.child.kill("SIGKILL");
  await abandoned.exited;
  const a = start({ numbers: [1, 2], now: latest });
  const b = start({ numbers: [1, 2], now: latest });
  await Promise.all([a.waitFor("done"), b.waitFor("done")]);
  await Promise.all([a.exited, b.exited]);
  assert.deepEqual(JSON.parse(await readFile(file, "utf8")), { prs: {
    [`${repo}#1`]: { firstQualifiedAt: first },
    "devdiv-microsoft/aspire-1p#7": { firstQualifiedAt: first },
    [`${repo}#2`]: { firstQualifiedAt: latest },
  } });
  assert.deepEqual(await readdir(directory), ["sla-tracking.json"]);
});

for (const [name, content] of [
  ["invalid JSON", "{broken"],
  ["invalid shape", '{"prs":[]}'],
  ["invalid timestamp", '{"prs":{"coreai/aspire-1p#1":{"firstQualifiedAt":"invalid"}}}'],
]) {
  test(`store reports ${name} without overwriting it and releases its lock`, { timeout: 20_000 }, async (t) => {
    const { start, file, directory } = await store(t, content);
    const a = start({ numbers: [1], now: later });
    const error = await a.waitFor("error");
    await a.exited;
    assert.match(error.message, /Cannot read SLA tracking store/);
    assert.equal(await readFile(file, "utf8"), content);
    assert.deepEqual(await readdir(directory), ["sla-tracking.json"]);
  });
}

test("store initializes a missing tracking file", { timeout: 20_000 }, async (t) => {
  const { start, file } = await store(t);
  await rm(file);
  const a = start({ numbers: [1], now: first });
  await a.waitFor("done");
  await a.exited;
  assert.deepEqual(JSON.parse(await readFile(file, "utf8")), { prs: {
    [`${repo}#1`]: { firstQualifiedAt: first },
  } });
});

test("store releases the lock and temporary file after a failed save", { timeout: 20_000 }, async (t) => {
  const { start, file, directory } = await store(t);
  const before = await readFile(file, "utf8");
  const a = start({ numbers: [1, 2], now: later, failSave: true });
  assert.equal((await a.waitFor("error")).message, "simulated save failure");
  await a.exited;
  assert.equal(await readFile(file, "utf8"), before);
  assert.deepEqual(await readdir(directory), ["sla-tracking.json"]);
  const b = start({ numbers: [1, 2], now: latest });
  await b.waitFor("done");
  await b.exited;
  assert.equal(JSON.parse(await readFile(file, "utf8")).prs[`${repo}#2`].firstQualifiedAt, latest);
});

test("store times out rather than evicting a live lock owner", { timeout: 20_000 }, async (t) => {
  const { start, file } = await store(t);
  const before = await readFile(file, "utf8");
  const a = start({ numbers: [1, 2], now: later, pause: true });
  await a.waitFor("loaded");
  const b = start({ numbers: [1, 2], now: latest });
  assert.equal((await b.waitFor("error")).message, "Timed out waiting for the SLA tracking lock");
  await b.exited;
  assert.equal(await readFile(file, "utf8"), before);
  a.child.send("resume");
  await a.waitFor("done");
  await a.exited;
  assert.equal(JSON.parse(await readFile(file, "utf8")).prs[`${repo}#2`].firstQualifiedAt, later);
});
