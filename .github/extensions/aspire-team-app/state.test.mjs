import assert from "node:assert/strict";
import fs, { readFile, readdir, rm } from "node:fs/promises";
import { syncBuiltinESMExports } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { DEFAULT_PROXIMA_REPOS, DEFAULT_REPOS } from "./github.mjs";

const artifactsRoot = fileURLToPath(new URL("../../../artifacts/copilot-extension-state-tests/", import.meta.url));
const originalHome = process.env.COPILOT_HOME;
process.env.COPILOT_HOME = join(artifactsRoot, "copilot-home");
const preferencesPath = join(process.env.COPILOT_HOME, "extensions", "aspire-team-app", "artifacts", "preferences.json");

const {
  accountConfig,
  activateNewProximaAccounts,
  addAzurePipeline,
  DEFAULT_PREFS,
  normalizeHealthOrder,
  normalizeAzurePipelines,
  removeAzurePipeline,
  loadPrefs,
  savePrefs,
  setAccountActive,
  setAccountRepos,
  setHealthOrder,
  updatePrefs,
} = await import("./state.mjs");

test.after(async () => {
  if (originalHome === undefined) delete process.env.COPILOT_HOME;
  else process.env.COPILOT_HOME = originalHome;
  await rm(artifactsRoot, { recursive: true, force: true });
});

test("reads during a preference save see complete saved settings until replacement", async (t) => {
  await rm(artifactsRoot, { recursive: true, force: true });
  const before = {
    ...DEFAULT_PREFS,
    detachedSessions: true,
    autoApplyUpdates: false,
    accounts: { "acct:msft.ghe.com/octo": { repos: ["coreai/aspire-1p"], active: true } },
  };
  await savePrefs(before);
  let signalEntered;
  let releaseSave;
  const entered = new Promise((resolve) => { signalEntered = resolve; });
  const resume = new Promise((resolve) => { releaseSave = resolve; });
  const writeFile = fs.writeFile;
  const write = t.mock.method(fs, "writeFile", async (path, data, options) => {
    await writeFile(path, data.slice(0, 20), options);
    signalEntered();
    await resume;
    await writeFile(path, data, "utf8");
  });
  syncBuiltinESMExports();
  t.after(() => { write.mock.restore(); syncBuiltinESMExports(); });

  const saving = updatePrefs((prefs) => { prefs.showDrafts = true; });
  try {
    await entered;
    assert.deepEqual(await loadPrefs(), before);
    assert.deepEqual(JSON.parse(await readFile(preferencesPath, "utf8")), before);
  } finally {
    releaseSave();
    await saving;
  }

  assert.deepEqual(await loadPrefs(), { ...before, showDrafts: true });
  assert.deepEqual(await readdir(dirname(preferencesPath)), ["preferences.json"]);
});

test("failed preference replacement preserves saved settings and removes the pending file", async (t) => {
  await rm(artifactsRoot, { recursive: true, force: true });
  const before = { ...DEFAULT_PREFS, detachedSessions: true };
  await savePrefs(before);
  const rename = t.mock.method(fs, "rename", async () => { throw new Error("replacement failed"); });
  syncBuiltinESMExports();
  t.after(() => { rename.mock.restore(); syncBuiltinESMExports(); });

  await assert.rejects(savePrefs({ ...before, detachedSessions: false }), /replacement failed/);

  assert.deepEqual(await loadPrefs(), before);
  assert.deepEqual(await readdir(dirname(preferencesPath)), ["preferences.json"]);
});

test("background updates apply automatically by default", () => {
  assert.equal(DEFAULT_PREFS.autoApplyUpdates, true);
});

test("new sessions remain children by default", () => {
  assert.equal(DEFAULT_PREFS.detachedSessions, false);
});

test("accountConfig defaults unconfigured accounts to the public repos", () => {
  const emu = accountConfig({ accounts: {} }, "acct:github.com/dapine_microsoft");
  assert.deepEqual(emu.repos, DEFAULT_REPOS);
  assert.equal(emu.configured, false);

  const normal = accountConfig({ accounts: {} }, "acct:github.com/octo");
  assert.deepEqual(normal.repos, DEFAULT_REPOS);
  assert.equal(normal.configured, false);
});

test("accountConfig defaults the Proxima enterprise account to the default Proxima repo set", () => {
  const proxima = accountConfig({ accounts: {} }, "acct:msft.ghe.com/ankj");

  assert.deepEqual(proxima.repos, DEFAULT_PROXIMA_REPOS);
  assert.equal(proxima.configured, false);
});

test("accountConfig keeps Proxima defaults on its host", () => {
  assert.deepEqual(
    accountConfig({ accounts: {} }, "acct:github.com/dapine_microsoft").repos,
    DEFAULT_REPOS,
  );
  assert.deepEqual(
    accountConfig({ accounts: {} }, "acct:dapine_microsoft").repos,
    DEFAULT_REPOS,
  );
  assert.deepEqual(
    accountConfig({ accounts: {} }, "acct:msft.ghe.com/ankj").repos,
    ["coreai/aspire-1p", "coreai/sisyphus"],
  );
});

test("accountConfig does not override an EMU account's explicitly configured repos", () => {
  const prefs = {
    accounts: {
      "acct:github.com/dapine_microsoft": { repos: ["microsoft/aspire"], active: true },
    },
  };

  assert.deepEqual(accountConfig(prefs, "acct:github.com/dapine_microsoft"), {
    repos: ["microsoft/aspire"],
    active: true,
    configured: true,
  });
});

test("setAccountRepos falls back to the public default when cleared for an EMU account", () => {
  const prefs = { accounts: {} };

  setAccountRepos(prefs, "acct:github.com/dapine_microsoft", []);

  assert.deepEqual(prefs.accounts["acct:github.com/dapine_microsoft"].repos, DEFAULT_REPOS);
});

test("activateNewProximaAccounts activates only usable accounts without saved preferences", () => {
  const prefs = {
    accounts: {
      "acct:github.com/octo": { repos: ["microsoft/aspire"], active: true },
      "acct:msft.ghe.com/disabled": { repos: ["coreai/aspire-1p"], active: false },
    },
  };

  activateNewProximaAccounts(prefs, [
    { id: "acct:msft.ghe.com/ankj", status: "ok", accessible: 1 },
    { id: "acct:msft.ghe.com/failed", status: "failed", accessible: 1 },
    { id: "acct:msft.ghe.com/inaccessible", status: "ok", accessible: 0 },
    { id: "acct:github.com/new", status: "ok", accessible: 1 },
    { id: "acct:msft.ghe.com/disabled", status: "ok", accessible: 1 },
  ]);

  assert.deepEqual(prefs.accounts, {
    "acct:github.com/octo": { repos: ["microsoft/aspire"], active: true },
    "acct:msft.ghe.com/disabled": { repos: ["coreai/aspire-1p"], active: false },
    "acct:msft.ghe.com/ankj": { repos: ["coreai/aspire-1p", "coreai/sisyphus"], active: true },
  });
});

test("activateNewProximaAccounts also handles a fresh preferences file", () => {
  const prefs = { accounts: {} };

  activateNewProximaAccounts(prefs, [
    { id: "acct:github.com/octo", status: "ok", accessible: 5 },
    { id: "acct:msft.ghe.com/ankj", status: "ok", accessible: 1 },
  ]);

  assert.deepEqual(prefs.accounts, {
    "acct:msft.ghe.com/ankj": { repos: ["coreai/aspire-1p", "coreai/sisyphus"], active: true },
  });
});

test("setAccountActive preserves legacy login-only repos when writing the host-scoped id", () => {
  const prefs = {
    accounts: {
      "acct:octo": { repos: ["microsoft/aspire.dev"], active: false },
    },
  };

  setAccountActive(prefs, "acct:github.com/octo", true);

  assert.deepEqual(prefs.accounts["acct:github.com/octo"], { repos: ["microsoft/aspire.dev"], active: true });
  assert.equal(prefs.accounts["acct:octo"], undefined);
});

test("setAccountRepos preserves legacy login-only active state when writing the host-scoped id", () => {
  const prefs = {
    accounts: {
      "acct:octo": { repos: ["microsoft/aspire.dev"], active: true },
    },
  };

  setAccountRepos(prefs, "acct:github.com/octo", ["microsoft/dcp"]);

  assert.deepEqual(prefs.accounts["acct:github.com/octo"], { repos: ["microsoft/dcp"], active: true });
  assert.equal(prefs.accounts["acct:octo"], undefined);
});

test("accountConfig reads legacy login-only prefs for github.com host-scoped ids", () => {
  const prefs = {
    accounts: {
      "acct:octo": { repos: ["microsoft/aspire.dev"], active: true },
    },
  };

  assert.deepEqual(accountConfig(prefs, "acct:github.com/octo"), {
    repos: ["microsoft/aspire.dev"],
    active: true,
    configured: true,
  });
});

test("Azure DevOps pipeline preferences normalize, replace, and remove without credentials", () => {
  const prefs = { azurePipelines: [] };
  const pipeline = {
    id: "azdo:dnceng/internal/1602",
    url: "https://dev.azure.com/dnceng/internal/_build?definitionId=1602",
    organization: "https://dev.azure.com/dnceng",
    organizationName: "dnceng",
    project: "internal",
    definitionId: 1602,
    name: "microsoft-aspire",
    branch: "refs/heads/main",
    repository: { id: "repo", name: "microsoft-aspire", type: "TfsGit", url: "https://example", defaultBranch: "refs/heads/main" },
    token: "must-not-survive",
  };

  addAzurePipeline(prefs, pipeline);
  addAzurePipeline(prefs, { ...pipeline, name: "Updated" });

  assert.equal(prefs.azurePipelines.length, 1);
  assert.equal(prefs.azurePipelines[0].name, "Updated");
  assert.equal("token" in prefs.azurePipelines[0], false);
  assert.deepEqual(normalizeAzurePipelines([{ id: "bad", url: "https://example", definitionId: 0 }]), []);

  removeAzurePipeline(prefs, pipeline.id);
  assert.deepEqual(prefs.azurePipelines, []);
});

test("health source ordering is stable, unique, and bounded", () => {
  const tooLong = "x".repeat(513);
  const prefs = {};

  setHealthOrder(prefs, ["github:one", "github:two", "github:one", "", tooLong, null]);

  assert.deepEqual(prefs.healthOrder, ["github:one", "github:two"]);
  assert.deepEqual(normalizeHealthOrder([" azdo:one ", "azdo:one"]), ["azdo:one"]);
  assert.equal(normalizeHealthOrder(Array.from({ length: 510 }, (_, index) => `source:${index}`)).length, 500);
});
