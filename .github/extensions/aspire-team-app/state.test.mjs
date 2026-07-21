import assert from "node:assert/strict";
import test from "node:test";

import { accountConfig, setAccountActive, setAccountRepos } from "./state.mjs";
import { DEFAULT_REPOS, DEFAULT_EMU_REPOS } from "./github.mjs";

test("accountConfig defaults unconfigured EMU accounts to the first-party repos", () => {
  const emu = accountConfig({ accounts: {} }, "acct:github.com/dapine_microsoft");
  assert.deepEqual(emu.repos, DEFAULT_EMU_REPOS);
  assert.equal(emu.configured, false);

  const normal = accountConfig({ accounts: {} }, "acct:github.com/octo");
  assert.deepEqual(normal.repos, DEFAULT_REPOS);
  assert.equal(normal.configured, false);
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

test("setAccountRepos falls back to the EMU default when cleared for an EMU account", () => {
  const prefs = { accounts: {} };

  setAccountRepos(prefs, "acct:github.com/dapine_microsoft", []);

  assert.deepEqual(prefs.accounts["acct:github.com/dapine_microsoft"].repos, DEFAULT_EMU_REPOS);
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
