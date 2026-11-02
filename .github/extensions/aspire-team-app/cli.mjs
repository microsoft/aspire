import { execFile } from "node:child_process";
import { isAbsolute } from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

function discoveryError(name, kind, message) {
  return Object.assign(new Error(message), { code: `${name}_cli_${kind}` });
}

export function createCliRunner(name, label, { run = execFileAsync, platform = process.platform } = {}) {
  let executable = name;
  return async function runCli(command, args, options) {
    if (command !== name) return run(command, args, options);
    try {
      return await run(executable, args, options);
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
      if (executable !== name || platform === "win32") {
        throw discoveryError(name, "missing", `${label} was not found. Install ${name} or make it available on PATH.`);
      }
    }

    // Desktop-launched canvases can inherit only /usr/bin:/bin:/usr/sbin:/sbin.
    // Discover the executable through the login shell, then run it directly.
    // CLI arguments and credentials are never interpolated into shell commands.
    const shell = options.env.SHELL || (platform === "darwin" ? "/bin/zsh" : "/bin/sh");
    if (!isAbsolute(shell)) {
      throw discoveryError(name, "lookup_failed", `Unable to locate ${label}: the login shell path must be absolute.`);
    }
    let stdout;
    try {
      ({ stdout } = await run(shell, ["-lc", 'command -v "$1"', "canvas-cli", name], options));
    } catch (error) {
      if (error.code === 1 && !error.stdout?.trim()) {
        throw discoveryError(name, "missing", `${label} was not found on PATH or by the login shell. Install ${name} or make it available to your login shell.`);
      }
      throw discoveryError(name, "lookup_failed", `Unable to locate ${label} through the login shell. Check your shell startup configuration.`);
    }
    // Accept one absolute path, e.g. "/opt/homebrew/bin/az" or
    // "/Users/octo/CLI Tools/gh", not aliases, functions, or startup-script output.
    const path = stdout.trim();
    if (!isAbsolute(path) || /[\r\n\0]/.test(path)) {
      throw discoveryError(name, "lookup_failed", `The login shell did not return a single absolute ${label} executable path. Check command discovery in your login shell.`);
    }
    executable = path;
    return runCli(command, args, options);
  };
}
