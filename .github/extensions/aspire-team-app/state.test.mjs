import assert from "node:assert/strict";
import test from "node:test";

import { accountConfig, setAccountActive, setAccountRepos } from "./state.mjs";

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
