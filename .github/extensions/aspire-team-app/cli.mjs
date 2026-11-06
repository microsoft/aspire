import { execFile } from "node:child_process";
import { userInfo } from "node:os";
import { basename, isAbsolute } from "node:path";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

function discoveryError(name, kind, message) {
  return Object.assign(new Error(message), { code: `${name}_cli_${kind}` });
}

export function createCliRunner(name, label, {
  run = execFileAsync,
  platform = process.platform,
  getUserShell = () => userInfo().shell,
} = {}) {
  let executable = name;
  return async function runCli(command, args, options) {
    if (command !== name) return run(command, args, options);
    const attemptedExecutable = executable;
    try {
      return await run(attemptedExecutable, args, options);
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
      if (attemptedExecutable !== name || platform === "win32") {
        throw discoveryError(name, "missing", `${label} was not found. Install ${name} or make it available on PATH.`);
      }
      // Another invocation may resolve the CLI while this PATH lookup is in flight.
      // Its ENOENT belongs to the original command, not the newly cached path.
      if (executable !== attemptedExecutable) return runCli(command, args, options);
    }

    // Desktop-launched canvases can inherit only /usr/bin:/bin:/usr/sbin:/sbin.
    // Discover the executable through the login shell, then run it directly.
    // CLI arguments and credentials are never interpolated into shell commands.
    let shell = options.env.SHELL;
    if (!shell) {
      try {
        shell = getUserShell();
      } catch {
        throw discoveryError(name, "lookup_failed", `Unable to determine the account's login shell for ${label}. Set SHELL to its absolute path.`);
      }
    }
    shell ||= platform === "darwin" ? "/bin/zsh" : "/bin/sh";
    if (!isAbsolute(shell)) {
      throw discoveryError(name, "lookup_failed", `Unable to locate ${label}: the login shell path must be absolute.`);
    }
    // Fish binds -c arguments to $argv rather than POSIX $0/$1.
    // https://fishshell.com/docs/current/cmds/fish.html
    const isFish = basename(shell) === "fish";
    const lookupArgs = isFish
      ? ["-lc", 'command -s "$argv[1]"', name]
      : ["-lc", 'command -v "$1"', "canvas-cli", name];
    let stdout;
    try {
      ({ stdout } = await run(shell, lookupArgs, options));
    } catch (error) {
      if ((error.code === 1 || error.code === 127) && !error.stdout?.trim()) {
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
