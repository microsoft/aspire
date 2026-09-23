import assert from "node:assert/strict";
import test from "node:test";

import {
  accountConfig,
  activateNewProximaAccounts,
  addAzurePipeline,
  DEFAULT_PREFS,
  normalizeHealthOrder,
  normalizeAzurePipelines,
  removeAzurePipeline,
  setAccountActive,
  setAccountRepos,
  setHealthOrder,
} from "./state.mjs";
import { DEFAULT_PROXIMA_REPOS, DEFAULT_REPOS } from "./github.mjs";

test("background updates apply automatically by default", () => {
  assert.equal(DEFAULT_PREFS.autoApplyUpdates, true);
});

test("accountConfig defaults unconfigured accounts to the public repos", () => {
  const emu = accountConfig({ accounts: {} }, "acct:github.com/dapine_microsoft");
  assert.deepEqual(emu.repos, DEFAULT_REPOS);
  assert.equal(emu.configured, false);

  const normal = accountConfig({ accounts: {} }, "acct:github.com/octo");
  assert.deepEqual(normal.repos, DEFAULT_REPOS);
  assert.equal(normal.configured, false);
});

test("accountConfig defaults the Proxima enterprise account to the first-party repo", () => {
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
    ["coreai/aspire-1p"],
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
    "acct:msft.ghe.com/ankj": { repos: ["coreai/aspire-1p"], active: true },
  });
});

test("activateNewProximaAccounts also handles a fresh preferences file", () => {
  const prefs = { accounts: {} };

  activateNewProximaAccounts(prefs, [
    { id: "acct:github.com/octo", status: "ok", accessible: 5 },
    { id: "acct:msft.ghe.com/ankj", status: "ok", accessible: 1 },
  ]);

  assert.deepEqual(prefs.accounts, {
    "acct:msft.ghe.com/ankj": { repos: ["coreai/aspire-1p"], active: true },
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
