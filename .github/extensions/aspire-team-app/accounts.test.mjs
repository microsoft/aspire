import assert from "node:assert/strict";
import test from "node:test";

import { accountId, detectCandidates, endpoints, isEmuAccountId, isEmuLogin, isProximaAccountId } from "./accounts.mjs";
import { accountConfig } from "./state.mjs";

const originalEnv = { ...process.env };
const originalFetch = globalThis.fetch;

test.afterEach(() => {
  restoreEnvironment();
  globalThis.fetch = originalFetch;
});

test("accountId includes normalized host and login", () => {
  assert.equal(accountId("Octo", "HTTPS://API.GITHUB.COM/foo"), "acct:github.com/octo");
  assert.equal(accountId("Octo", "GHE.EXAMPLE.COM"), "acct:ghe.example.com/octo");
});

test("isEmuLogin matches org alias suffixes but not the bare suffix or plain logins", () => {
  assert.equal(isEmuLogin("dapine_microsoft"), true);
  assert.equal(isEmuLogin("DAPINE_MICROSOFT"), true);
  assert.equal(isEmuLogin("octocat"), false);
  assert.equal(isEmuLogin("_microsoft"), false);
  assert.equal(isEmuLogin(""), false);
  assert.equal(isEmuLogin(null), false);
});

test("isEmuAccountId classifies host-scoped and legacy ids by their login", () => {
  assert.equal(isEmuAccountId("acct:github.com/dapine_microsoft"), true);
  assert.equal(isEmuAccountId("acct:dapine_microsoft"), true);
  assert.equal(isEmuAccountId("acct:github.com/octo"), false);
  assert.equal(isEmuAccountId("acct:octo"), false);
  // A GHES account whose login happens to end in an org-alias suffix must NOT be treated as
  // an EMU (github.com) account. EMU aliases only exist on github.com.
  assert.equal(isEmuAccountId("acct:ghe.example.com/alice_microsoft"), false);
  assert.equal(isEmuAccountId("acct:ghe.example.com/octo"), false);
});

test("isProximaAccountId recognizes the configured enterprise host", () => {
  assert.equal(isProximaAccountId("acct:msft.ghe.com/ankj"), true);
  assert.equal(isProximaAccountId("acct:ghe.example.com/ankj"), false);
  assert.equal(isProximaAccountId("acct:github.com/ankj"), false);
});

test("endpoints use the API origin for GHE.com and GHES paths for server hosts", () => {
  assert.deepEqual(endpoints("github.com"), {
    rest: "https://api.github.com",
    graphql: "https://api.github.com/graphql",
  });
  assert.deepEqual(endpoints("msft.ghe.com"), {
    rest: "https://api.msft.ghe.com",
    graphql: "https://api.msft.ghe.com/graphql",
  });
  assert.deepEqual(endpoints("ghe.example.com"), {
    rest: "https://ghe.example.com/api/v3",
    graphql: "https://ghe.example.com/api/graphql",
  });
});

test("standalone discovery retains healthy CLI accounts when another host fails", async () => {
  const calls = [];
  const candidates = await detectCandidates({
    env: { PATH: "cli-path", GH_CONFIG_DIR: "cli-config" },
    run: async (command, args, options) => {
      calls.push({ command, args, options });
      if (args[1] === "status") {
        assert.deepEqual(args, ["auth", "status", "--json", "hosts"]);
        return { stdout: JSON.stringify({ hosts: {
          "github.com": [
            { host: "github.com", login: "octo", state: "success" },
            { host: "github.com", login: "expired", state: "error" },
          ],
          "ghe.example.com": [{ host: "ghe.example.com", login: "octo-enterprise", state: "success" }],
          "broken.example.com": [{ host: "broken.example.com", state: "error" }],
        } }) };
      }
      return { stdout: args.at(-1) === "octo" ? "stored-dotcom\n" : "stored-enterprise\n" };
    },
  });

  assert.deepEqual(candidates.map(({ source, host, login, token }) => ({ source, host, login, token })), [
    { source: "gh", host: "github.com", login: "octo", token: "stored-dotcom" },
    { source: "gh", host: "ghe.example.com", login: "octo-enterprise", token: "stored-enterprise" },
  ]);
  assert.deepEqual(calls.map(({ command, args }) => ({ command, args })), [
    { command: "gh", args: ["auth", "status", "--json", "hosts"] },
    { command: "gh", args: ["auth", "token", "--hostname", "github.com", "--user", "octo"] },
    { command: "gh", args: ["auth", "token", "--hostname", "ghe.example.com", "--user", "octo-enterprise"] },
  ]);
  for (const { options } of calls) {
    assert.deepEqual(options, { timeout: 8000, env: { PATH: "cli-path", GH_CONFIG_DIR: "cli-config" } });
  }
});

test("injected tokens do not override stored CLI credentials and remain separate candidates", async () => {
  const env = {
    GH_TOKEN: "injected-dotcom",
    GITHUB_TOKEN: "injected-github",
    GH_ENTERPRISE_TOKEN: "injected-enterprise",
    GITHUB_ENTERPRISE_TOKEN: "injected-github-enterprise",
    COPILOT_GH_ACCOUNT_github_2E_com_octo: "copilot-token",
  };
  const before = { ...env };
  const candidates = await detectCandidates({
    env,
    platform: "linux",
    run: async (_command, args, { env: childEnv }) => {
      assert.deepEqual(childEnv, { COPILOT_GH_ACCOUNT_github_2E_com_octo: "copilot-token" });
      return { stdout: args[1] === "status"
        ? JSON.stringify({ hosts: { "github.com": [{ host: "github.com", login: "octo", state: "success" }] } })
        : "stored-token\n" };
    },
  });

  assert.deepEqual(candidates.map(({ source, token }) => ({ source, token })), [
    { source: "gh", token: "stored-token" },
    { source: "env", token: "injected-dotcom" },
    { source: "env", token: "injected-github" },
    { source: "copilot", token: "copilot-token" },
  ]);
  assert.deepEqual(env, before);
});

test("discovery still uses environment credentials when gh is unavailable", async () => {
  const candidates = await detectCandidates({
    env: { GH_TOKEN: "env-token" },
    run: async () => { throw Object.assign(new Error("gh unavailable"), { code: "ENOENT" }); },
  });

  assert.deepEqual(candidates.map(({ source, token }) => ({ source, token })), [
    { source: "env", token: "env-token" },
  ]);
});

test("standalone discovery uses the login-shell executable for status and token reads", async () => {
  for (const platform of ["darwin", "linux"]) {
    const calls = [];
    const env = { PATH: "/usr/bin:/bin", SHELL: "/bin/zsh", GH_TOKEN: "injected-token" };
    const candidates = await detectCandidates({
      env,
      platform,
      run: async (command, args, options) => {
        calls.push({ command, args });
        assert.deepEqual(options, { timeout: 8000, env: { PATH: "/usr/bin:/bin", SHELL: "/bin/zsh" } });
        if (command === "gh") throw Object.assign(new Error("missing"), { code: "ENOENT" });
        if (command === "/bin/zsh") return { stdout: "/Users/octo/CLI Tools/gh\n" };
        assert.equal(command, "/Users/octo/CLI Tools/gh");
        return { stdout: args[1] === "status"
          ? JSON.stringify({ hosts: { "github.com": [{ host: "github.com", login: "octo", state: "success" }] } })
          : "stored-token\n" };
      },
    });
    assert.deepEqual(calls, [
      { command: "gh", args: ["auth", "status", "--json", "hosts"] },
      { command: "/bin/zsh", args: ["-lc", 'command -v "$1"', "canvas-cli", "gh"] },
      { command: "/Users/octo/CLI Tools/gh", args: ["auth", "status", "--json", "hosts"] },
      { command: "/Users/octo/CLI Tools/gh", args: ["auth", "token", "--hostname", "github.com", "--user", "octo"] },
    ]);
    assert.deepEqual(candidates.map(({ source, token }) => ({ source, token })), [
      { source: "gh", token: "stored-token" },
      { source: "env", token: "injected-token" },
    ]);
    assert.equal(env.GH_TOKEN, "injected-token");
  }
});

test("login-shell discovery preserves legacy status support", async () => {
  for (const [platform, shell] of [["darwin", "/bin/zsh"], ["linux", "/bin/sh"]]) {
    const calls = [];
    const candidates = await detectCandidates({
      env: { SHELL: shell },
      platform,
      run: async (command, args) => {
        calls.push({ command, args });
        if (command === "gh") throw Object.assign(new Error("missing"), { code: "ENOENT" });
        if (command === shell) return { stdout: "/opt/tools/gh\n" };
        assert.equal(command, "/opt/tools/gh");
        if (args.includes("--json")) {
          throw Object.assign(new Error("unsupported"), { code: 1, stderr: "unknown flag: --json" });
        }
        return { stdout: args[1] === "status" ? "Logged in to github.com account octo (keyring)\n" : "stored-token\n" };
      },
    });
    assert.equal(candidates[0].token, "stored-token");
    assert.deepEqual(calls.map(({ command }) => command), ["gh", shell, "/opt/tools/gh", "/opt/tools/gh", "/opt/tools/gh"]);
  }
});

test("missing gh reports executable discovery failure rather than suggesting login", async () => {
  for (const platform of ["darwin", "linux", "win32"]) {
    const calls = [];
    await assert.rejects(detectCandidates({
      env: { SHELL: "/test/login-shell" },
      platform,
      run: async (command) => {
        calls.push(command);
        throw Object.assign(new Error("private details"), { code: command === "gh" ? "ENOENT" : 1, stdout: "" });
      },
    }), { code: "gh_cli_missing" });
    assert.deepEqual(calls, platform === "win32" ? ["gh"] : ["gh", "/test/login-shell"]);
  }
});

test("login-shell lookup rejects ambiguous output and keeps independent credentials", async () => {
  for (const stdout of ["", "gh", "gh is an alias", "/opt/bin/gh\nstartup output", "/opt/bin/gh\0"]) {
    const diagnostics = [];
    const candidates = await detectCandidates({
      env: { GH_TOKEN: "env-token", SHELL: "/bin/zsh" },
      platform: "darwin",
      diagnostics,
      run: async (command) => {
        if (command === "gh") throw Object.assign(new Error("missing"), { code: "ENOENT" });
        assert.equal(command, "/bin/zsh");
        return { stdout };
      },
    });
    assert.deepEqual(candidates.map(({ source, token }) => ({ source, token })), [{ source: "env", token: "env-token" }]);
    assert.equal(diagnostics.length, 1);
    assert.equal(diagnostics[0].code, "gh_cli_lookup_failed");
  }
});

test("login-shell lookup failures surface safe diagnostics", async () => {
  for (const shell of ["relative-shell", "/bin/zsh"]) {
    await assert.rejects(detectCandidates({
      env: { SHELL: shell },
      platform: "darwin",
      run: async (command) => {
        if (command === "gh") throw Object.assign(new Error("missing"), { code: "ENOENT" });
        throw Object.assign(new Error("private shell error"), { code: 2, stderr: "private details" });
      },
    }), (error) => {
      assert.equal(error.code, "gh_cli_lookup_failed");
      assert.match(error.message, /^Unable to locate GitHub CLI/);
      assert.equal(error.message.includes("private"), false);
      return true;
    });
  }
});

test("Windows sidebar discovery restores missing APPDATA for account and token reads", async () => {
  const env = { PATH: "cli-path", USERPROFILE: "C:\\Users\\octo" };
  const calls = [];
  const candidates = await detectCandidates({
    env,
    platform: "win32",
    run: async (command, args, options) => {
      calls.push({ command, args, env: { ...options.env } });
      if (command === "powershell.exe") {
        assert.deepEqual(args, [
          "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
          "[Environment]::GetFolderPath('ApplicationData')",
        ]);
        return { stdout: "D:\\Redirected AppData\\octo\r\n" };
      }
      if (args[1] === "status") {
        return { stdout: JSON.stringify({ hosts: options.env.APPDATA
          ? { "github.com": [{ host: "github.com", login: "octo", state: "success" }] }
          : {} }) };
      }
      return { stdout: "stored-token\n" };
    },
  });

  assert.deepEqual(candidates.map(({ source, login, token }) => ({ source, login, token })), [
    { source: "gh", login: "octo", token: "stored-token" },
  ]);
  assert.deepEqual(calls.map(({ command, args }) => [command, args[1]]), [
    ["gh", "status"], ["powershell.exe", "-NoProfile"], ["gh", "status"], ["gh", "token"],
  ]);
  assert.deepEqual(calls.map(({ env: childEnv }) => childEnv.APPDATA), [
    undefined, undefined, "D:\\Redirected AppData\\octo", "D:\\Redirected AppData\\octo",
  ]);
  assert.deepEqual(env, { PATH: "cli-path", USERPROFILE: "C:\\Users\\octo" });
});

test("discovery preserves explicit config locations and non-Windows defaults", async () => {
  for (const config of [
    { platform: "win32", env: { APPDATA: "custom-appdata" } },
    { platform: "win32", env: { AppData: "custom-appdata" } },
    { platform: "win32", env: { GH_CONFIG_DIR: "custom-config" } },
    { platform: "win32", env: { XDG_CONFIG_HOME: "custom-xdg" } },
    { platform: "linux", env: {} },
    { platform: "darwin", env: {} },
  ]) {
    const calls = [];
    const candidates = await detectCandidates({
      ...config,
      run: async (command, args) => {
        calls.push({ command, args });
        return { stdout: JSON.stringify({ hosts: {} }) };
      },
    });

    assert.deepEqual(candidates, []);
    assert.deepEqual(calls, [{ command: "gh", args: ["auth", "status", "--json", "hosts"] }]);
  }
});

test("Windows discovery keeps existing home-directory accounts without resolving APPDATA", async () => {
  const candidates = await detectCandidates({
    env: {},
    platform: "win32",
    run: async (command, args) => {
      assert.equal(command, "gh");
      return { stdout: args[1] === "status"
        ? JSON.stringify({ hosts: { "github.com": [{ host: "github.com", login: "octo", state: "success" }] } })
        : "stored-token" };
    },
  });

  assert.deepEqual(candidates.map(({ source, login }) => ({ source, login })), [
    { source: "gh", login: "octo" },
  ]);
});

test("Windows discovery surfaces known-folder lookup failures", async () => {
  for (const fail of [false, true]) {
    await assert.rejects(detectCandidates({
      env: {},
      platform: "win32",
      run: async (command) => {
        if (command === "gh") return { stdout: JSON.stringify({ hosts: {} }) };
        if (fail) throw new Error("Known-folder lookup failed");
        return { stdout: "\r\n" };
      },
    }), { code: "gh_cli_discovery_failed", message: /Unable to locate Windows application data/ });
  }
});

test("legacy gh status retains accounts from either stream when a host fails", async () => {
  for (const exit of [0, 1]) {
    const calls = [];
    const candidates = await detectCandidates({
      env: { GH_TOKEN: "injected-token", APPDATA: "config" },
      platform: "win32",
      run: async (command, args, { env }) => {
        calls.push(args);
        assert.equal(command, "gh");
        assert.deepEqual(env, { APPDATA: "config" });
        if (args.includes("--json")) {
          throw Object.assign(new Error("unsupported"), { code: 1, stderr: "unknown flag: --json" });
        }
        if (args[1] === "status") {
          const output = {
            stdout: "  ✓ Logged in to github.com account octo (keyring)\n",
            stderr: "Failed to log in to broken.example.com\n  ✓ Logged in to ghe.example.com account enterprise (keyring)\n",
          };
          if (exit) throw Object.assign(new Error("one host failed"), { code: exit, ...output });
          return output;
        }
        return { stdout: args.at(-1) === "octo" ? "stored-dotcom" : "stored-enterprise" };
      },
    });
    assert.deepEqual(candidates.map(({ source, host, login }) => ({ source, host, login })), [
      { source: "gh", host: "github.com", login: "octo" },
      { source: "gh", host: "ghe.example.com", login: "enterprise" },
      { source: "env", host: "github.com", login: null },
    ]);
    assert.equal(calls.length, 4);
  }
});

test("Windows lookup failures retain independent tokens and a safe diagnostic", async () => {
  for (const empty of [true, false]) {
    const diagnostics = [];
    const candidates = await detectCandidates({
      env: { GH_TOKEN: "env-token", COPILOT_GH_ACCOUNT_github_2E_com_octo: "copilot-token" },
      platform: "win32",
      diagnostics,
      run: async (command) => {
        if (command === "gh") return { stdout: JSON.stringify({ hosts: {} }) };
        if (!empty) throw new Error("private subprocess error");
        return { stdout: "" };
      },
    });
    assert.deepEqual(candidates.map(({ source, token }) => ({ source, token })), [
      { source: "env", token: "env-token" },
      { source: "copilot", token: "copilot-token" },
    ]);
    assert.equal(diagnostics.length, 1);
    assert.match(diagnostics[0].message, /^Unable to locate Windows application data/);
  }
});

test("Windows token overrides are removed and collected case-insensitively", async () => {
  const env = {
    Gh_ToKeN: "dotcom", Github_Token: "github",
    Gh_Enterprise_Token: "enterprise", Github_Enterprise_Token: "github-enterprise",
    Gh_Host: "ghe.example.com", AppData: "custom-config",
  };
  const candidates = await detectCandidates({
    env,
    platform: "win32",
    run: async (_command, _args, { env: childEnv }) => {
      assert.deepEqual(childEnv, { GH_HOST: "ghe.example.com", APPDATA: "custom-config" });
      return { stdout: JSON.stringify({ hosts: {} }) };
    },
  });
  assert.deepEqual(candidates.map(({ token, host }) => ({ token, host })), [
    { token: "dotcom", host: "ghe.example.com" },
    { token: "github", host: "ghe.example.com" },
    { token: "enterprise", host: "ghe.example.com" },
    { token: "github-enterprise", host: "ghe.example.com" },
  ]);
  assert.equal(env.Gh_ToKeN, "dotcom");
  assert.equal(Object.hasOwn(env, "GH_TOKEN"), false);
});

test("enterprise-only tokens are discovered for the configured enterprise host", async () => {
  for (const name of ["GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]) {
    for (const host of ["ghe.example.com", "msft.ghe.com", "github.com"]) {
      const candidates = await detectCandidates({
        env: { GH_HOST: host, [name]: "enterprise-token" },
        platform: "linux",
        run: async () => ({ stdout: JSON.stringify({ hosts: {} }) }),
      });
      assert.deepEqual(candidates.map(({ source, token, host }) => ({ source, token, host })),
        host === "github.com" ? [] : [{ source: "env", token: "enterprise-token", host }]);
    }
  }
});

test("unexpected CLI failures surface a safe diagnostic rather than using legacy parsing", async () => {
  const calls = [];
  await assert.rejects(detectCandidates({
    env: {},
    platform: "linux",
    run: async (_command, args) => {
      calls.push(args);
      throw Object.assign(new Error("private error"), { code: 2, stderr: "private details" });
    },
  }), { code: "gh_cli_discovery_failed", message: /GitHub CLI account discovery failed/ });
  assert.deepEqual(calls, [["auth", "status", "--json", "hosts"]]);
});

test("resolveAccounts keys accounts by normalized host and login", async () => {
  resetAccountEnv({
    COPILOT_GH_ACCOUNT_github_2E_com_octo: "token-dotcom",
    COPILOT_GH_ACCOUNT_ghe_2E_example_2E_com_octo: "token-ghe",
    COPILOT_GH_ACCOUNT_msft_2E_ghe_2E_com_octo: "token-proxima",
  });
  globalThis.fetch = accountFetch();
  const { resolveAccounts } = await import(`./accounts.mjs?test=host-login-${Date.now()}`);

  const { accounts } = await resolveAccounts(() => ["microsoft/aspire"], () => true);

  assert.deepEqual(accounts.map((a) => a.id).sort(), [
    "acct:ghe.example.com/octo",
    "acct:github.com/octo",
    "acct:msft.ghe.com/octo",
  ]);
  assert.equal(new Set(accounts.map((a) => a.host)).size, 3);
});

test("resolveAccounts reads legacy login-only preferences for github.com accounts", async () => {
  resetAccountEnv({ COPILOT_GH_ACCOUNT_github_2E_com_octo: "token-dotcom" });
  globalThis.fetch = accountFetch();
  const { resolveAccounts } = await import(`./accounts.mjs?test=legacy-${Date.now()}`);
  const legacyPrefs = {
    "acct:octo": { repos: ["microsoft/aspire.dev"], active: true },
  };

  const { accounts } = await resolveAccounts(
    (id) => accountConfig({ accounts: legacyPrefs }, id).repos,
    (id) => accountConfig({ accounts: legacyPrefs }, id).active,
  );

  assert.equal(accounts[0].id, "acct:github.com/octo");
  assert.deepEqual(accounts[0].repos, ["microsoft/aspire.dev"]);
  assert.equal(accounts[0].active, true);
});

function resetAccountEnv(extra) {
  for (const key of Object.keys(process.env)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_")) {
      delete process.env[key];
    }
  }
  delete process.env.GH_TOKEN;
  delete process.env.GITHUB_TOKEN;
  delete process.env.GH_HOST;
  process.env.PATH = "";
  process.env.SHELL = "/nonexistent/aspire-team-app-test-shell";
  Object.assign(process.env, extra);
}

function accountFetch() {
  return async (url, options = {}) => {
    const requestUrl = String(url);
    const body = options.body ? JSON.parse(options.body) : {};
    const query = body.query ?? "";
    if (
      requestUrl.endsWith("/api/v3/") ||
      requestUrl === "https://api.github.com/" ||
      requestUrl === "https://api.msft.ghe.com/"
    ) {
      return jsonResponse({}, { headers: { "x-oauth-scopes": "read:org" } });
    }
    if (query.includes("viewer { login")) {
      return jsonResponse({ data: { viewer: { login: "octo", avatarUrl: `${requestUrl}/octo.png` } } });
    }
    if (query.includes("r0: repository")) {
      return jsonResponse({ data: { r0: { nameWithOwner: "microsoft/aspire" } } });
    }
    throw new Error(`Unexpected fetch: ${requestUrl} ${query}`);
  };
}

function jsonResponse(body, options = {}) {
  const headers = options.headers ?? {};
  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    headers: {
      get(name) { return headers[name.toLowerCase()] ?? null; },
    },
    json: async () => body,
  };
}

function restoreEnvironment() {
  for (const key of Object.keys(process.env)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_") && !(key in originalEnv)) {
      delete process.env[key];
    }
  }
  for (const name of ["GH_TOKEN", "GITHUB_TOKEN", "GH_HOST", "PATH"]) {
    if (originalEnv[name] === undefined) {
      delete process.env[name];
    } else {
      process.env[name] = originalEnv[name];
    }
  }
  for (const [key, value] of Object.entries(originalEnv)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_")) {
      process.env[key] = value;
    }
  }
}
