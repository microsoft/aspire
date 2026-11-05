import assert from "node:assert/strict";
import test from "node:test";

import { createCliRunner } from "./cli.mjs";

test("CLI discovery shares the fallback across GitHub and Azure without changing arguments or environment", async () => {
  for (const [name, label] of [["gh", "GitHub CLI"], ["az", "Azure CLI"]]) {
    const calls = [];
    const options = { env: { PATH: "/usr/bin:/bin", SHELL: "/bin/zsh" }, timeout: 8000, maxBuffer: 1024 };
    const args = ["query", "branch with spaces", "quote'and\"double", "%NAME%", "$(not-executed)"];
    const runner = createCliRunner(name, label, {
      platform: "darwin",
      run: async (command, childArgs, childOptions) => {
        calls.push({ command, args: childArgs });
        assert.equal(childOptions, options);
        if (command === name) throw Object.assign(new Error("missing"), { code: "ENOENT" });
        if (command === "/bin/zsh") return { stdout: `/Users/octo/CLI Tools/${name}\n` };
        assert.equal(command, `/Users/octo/CLI Tools/${name}`);
        assert.equal(childArgs, args);
        return { stdout: "result" };
      },
    });
    assert.deepEqual(await runner(name, args, options), { stdout: "result" });
    assert.deepEqual(await runner(name, args, options), { stdout: "result" });
    assert.deepEqual(calls, [
      { command: name, args },
      { command: "/bin/zsh", args: ["-lc", 'command -v "$1"', "canvas-cli", name] },
      { command: `/Users/octo/CLI Tools/${name}`, args },
      { command: `/Users/octo/CLI Tools/${name}`, args },
    ]);
    assert.deepEqual(options.env, { PATH: "/usr/bin:/bin", SHELL: "/bin/zsh" });
  }
});

test("CLI execution never invokes a shell for an existing executable or a command failure", async () => {
  for (const code of [null, 1, "ETIMEDOUT"]) {
    const calls = [];
    const error = Object.assign(new Error("query failed"), { code });
    const runner = createCliRunner("az", "Azure CLI", {
      platform: "darwin",
      run: async (command) => {
        calls.push(command);
        if (code) throw error;
        return { stdout: "result" };
      },
    });
    if (code) await assert.rejects(runner("az", [], { env: {} }), (actual) => actual === error);
    else assert.deepEqual(await runner("az", [], { env: {} }), { stdout: "result" });
    assert.deepEqual(calls, ["az"]);
  }
});

test("CLI runner leaves Windows bundled Python and auxiliary commands untouched", async () => {
  const options = { env: {} };
  for (const [name, command, args] of [
    ["az", "C:\\Azure CLI\\python.exe", ["-IBm", "azure.cli", "version"]],
    ["gh", "powershell.exe", ["-NoProfile", "-Command", "known-folder-query"]],
  ]) {
    const runner = createCliRunner(name, "CLI", {
      platform: "win32",
      run: async (actualCommand, actualArgs, actualOptions) => {
        assert.equal(actualCommand, command);
        assert.equal(actualArgs, args);
        assert.equal(actualOptions, options);
        return { stdout: "result" };
      },
    });
    assert.deepEqual(await runner(command, args, options), { stdout: "result" });
  }
});

test("a missing resolved executable produces a CLI-specific error without another shell lookup", async () => {
  for (const name of ["gh", "az"]) {
    const calls = [];
    const runner = createCliRunner(name, "CLI", {
      platform: "darwin",
      getUserShell: () => "/bin/zsh",
      run: async (command) => {
        calls.push(command);
        if (command === "/bin/zsh") return { stdout: `/missing/${name}` };
        throw Object.assign(new Error("missing"), { code: "ENOENT" });
      },
    });
    await assert.rejects(runner(name, [], { env: {} }), { code: `${name}_cli_missing` });
    assert.deepEqual(calls, [name, "/bin/zsh", `/missing/${name}`]);
  }
});

test("concurrent PATH failures retry an executable resolved by another invocation", async () => {
  for (const name of ["gh", "az"]) {
    const calls = [];
    let attempts = 0;
    let rejectLate;
    const lateFailure = new Promise((_, reject) => { rejectLate = reject; });
    const runner = createCliRunner(name, "CLI", {
      platform: "darwin",
      run: async (command) => {
        calls.push(command);
        if (command === name) {
          if (++attempts === 2) return lateFailure;
          throw Object.assign(new Error("missing on PATH"), { code: "ENOENT" });
        }
        if (command === "/bin/zsh") return { stdout: `/opt/tools/${name}\n` };
        assert.equal(command, `/opt/tools/${name}`);
        return { stdout: "success" };
      },
    });
    const options = { env: { SHELL: "/bin/zsh" } };
    const first = runner(name, ["version"], options);
    const second = runner(name, ["version"], options);
    assert.deepEqual(await first, { stdout: "success" });
    rejectLate(Object.assign(new Error("missing on PATH"), { code: "ENOENT" }));
    assert.deepEqual(await second, { stdout: "success" });
    assert.deepEqual(calls, [name, name, "/bin/zsh", `/opt/tools/${name}`, `/opt/tools/${name}`]);
  }
});

test("CLI lookup prefers SHELL, then the configured account shell, then platform defaults", async () => {
  for (const { platform, env, configuredShell, expectedShell } of [
    { platform: "linux", env: { SHELL: "/bin/zsh" }, configuredShell: "/bin/bash", expectedShell: "/bin/zsh" },
    { platform: "linux", env: {}, configuredShell: "/bin/bash", expectedShell: "/bin/bash" },
    { platform: "darwin", env: {}, configuredShell: "/bin/bash", expectedShell: "/bin/bash" },
    { platform: "linux", env: {}, configuredShell: null, expectedShell: "/bin/sh" },
    { platform: "darwin", env: {}, configuredShell: null, expectedShell: "/bin/zsh" },
  ]) {
    let accountLookups = 0;
    const runner = createCliRunner("gh", "GitHub CLI", {
      platform,
      getUserShell: () => { accountLookups++; return configuredShell; },
      run: async (command, args) => {
        if (command === "gh") throw Object.assign(new Error("missing"), { code: "ENOENT" });
        if (command === expectedShell) {
          assert.deepEqual(args, ["-lc", 'command -v "$1"', "canvas-cli", "gh"]);
          return { stdout: "/opt/tools/gh\n" };
        }
        assert.equal(command, "/opt/tools/gh");
        return { stdout: "success" };
      },
    });
    assert.deepEqual(await runner("gh", [], { env }), { stdout: "success" });
    assert.equal(accountLookups, env.SHELL ? 0 : 1);
  }
});

test("configured shell lookup failures produce a safe diagnostic", async () => {
  const runner = createCliRunner("az", "Azure CLI", {
    platform: "linux",
    getUserShell: () => { throw new Error("private account details"); },
    run: async (command) => {
      assert.equal(command, "az");
      throw Object.assign(new Error("missing"), { code: "ENOENT" });
    },
  });
  await assert.rejects(runner("az", [], { env: {} }), {
    code: "az_cli_lookup_failed",
    message: "Unable to determine the account's login shell for Azure CLI. Set SHELL to its absolute path.",
  });
});

test("Fish discovery binds the executable name through argv for both CLIs", async () => {
  for (const name of ["gh", "az"]) {
    for (const env of [{ SHELL: "/opt/shells/fish" }, {}]) {
      const calls = [];
      const args = ["query", "branch'with\"quotes", "$(not-executed)"];
      const runner = createCliRunner(name, "CLI", {
        platform: "linux",
        getUserShell: () => "/opt/shells/fish",
        run: async (command, childArgs) => {
          calls.push({ command, args: childArgs });
          if (command === name) throw Object.assign(new Error("missing"), { code: "ENOENT" });
          if (command === "/opt/shells/fish") {
            assert.deepEqual(childArgs, ["-lc", 'command -s "$argv[1]"', name]);
            return { stdout: `/opt/tools/${name}\n` };
          }
          assert.equal(command, `/opt/tools/${name}`);
          assert.equal(childArgs, args);
          return { stdout: "success" };
        },
      });
      assert.deepEqual(await runner(name, args, { env }), { stdout: "success" });
      assert.equal(calls.length, 3);
    }
  }
});

test("empty-output status 1 and 127 identify missing commands in POSIX shells and Fish", async () => {
  for (const name of ["gh", "az"]) {
    for (const shell of ["/bin/sh", "/bin/dash", "/bin/zsh", "/usr/bin/fish"]) {
      for (const code of [1, 127]) {
        const runner = createCliRunner(name, "CLI", {
          platform: "linux",
          run: async (command) => {
            throw Object.assign(new Error("missing"), { code: command === name ? "ENOENT" : code, stdout: "\n" });
          },
        });
        await assert.rejects(runner(name, [], { env: { SHELL: shell } }), { code: `${name}_cli_missing` });
      }
    }
  }
});

test("failed command lookups with output retain shell configuration diagnostics", async () => {
  for (const code of [1, 127]) {
    const runner = createCliRunner("az", "Azure CLI", {
      platform: "linux",
      run: async (command) => {
        throw Object.assign(new Error("lookup failed"), { code: command === "az" ? "ENOENT" : code, stdout: "startup output" });
      },
    });
    await assert.rejects(runner("az", [], { env: { SHELL: "/bin/sh" } }), { code: "az_cli_lookup_failed" });
  }
});
