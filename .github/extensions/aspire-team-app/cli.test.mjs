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
