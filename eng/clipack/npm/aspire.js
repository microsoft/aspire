#!/usr/bin/env node
'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

// Package names are generated at pack time so changing the npm package name in
// MSBuild does not require editing this launcher. Resolved lazily inside main()
// so a missing/corrupt aspire-package-map.json surfaces through the same
// friendly error path used by the rest of the launcher (try/catch around main).
let ridPackageNames = null;

function loadRidPackageNames() {
  const packageMapPath = path.join(__dirname, 'aspire-package-map.json');
  let raw;
  try {
    raw = fs.readFileSync(packageMapPath, 'utf8');
  } catch (error) {
    throw new Error(
      `Aspire CLI installation is corrupted: package map '${packageMapPath}' could not be read. ` +
      'Reinstall @microsoft/aspire-cli.',
      { cause: error });
  }
  try {
    return new Map(Object.entries(JSON.parse(raw)));
  } catch (error) {
    throw new Error(
      `Aspire CLI installation is corrupted: package map '${packageMapPath}' is not valid JSON. ` +
      'Reinstall @microsoft/aspire-cli.',
      { cause: error });
  }
}

function detectRid() {
  const platform = process.platform;
  const arch = process.arch;

  if (platform === 'win32' && (arch === 'x64' || arch === 'arm64')) {
    return `win-${arch}`;
  }

  if (platform === 'darwin' && (arch === 'x64' || arch === 'arm64')) {
    return `osx-${arch}`;
  }

  if (platform === 'linux') {
    // libc-mismatched binaries crash at exec with cryptic dynamic-linker errors
    // (e.g. missing ld-linux-aarch64.so.1 / "GLIBC_X.Y not found"). Detect musl
    // for all supported arches so unsupported combinations fall through to the
    // friendly "Unsupported platform" error below, instead of silently
    // resolving the glibc-linked RID package.
    const musl = isMusl();
    if (arch === 'x64' && musl) {
      return 'linux-musl-x64';
    }
    if (arch === 'arm64' && musl) {
      throw new Error(`Unsupported platform: ${platform} musl ${arch}`);
    }

    if (arch === 'x64' || arch === 'arm64') {
      return `linux-${arch}`;
    }
  }

  throw new Error(`Unsupported platform: ${platform} ${arch}`);
}

function isMusl() {
  // npm supports libc-specific packages. Prefer Node's runtime report because
  // it avoids spawning a process on glibc systems, then fall back to ldd.
  if (process.report && typeof process.report.getReport === 'function') {
    const report = process.report.getReport();
    if (report && report.header && report.header.glibcVersionRuntime) {
      return false;
    }
  }

  const lddResult = childProcess.spawnSync('ldd', ['--version'], { encoding: 'utf8' });
  const lddOutput = `${lddResult.stdout || ''}${lddResult.stderr || ''}`.toLowerCase();
  return lddOutput.includes('musl');
}

function resolveNativeBinary(rid) {
  const packageName = ridPackageNames.get(rid);
  if (!packageName) {
    throw new Error(`No Aspire CLI npm package is available for RID '${rid}'.`);
  }

  let packageJsonPath;
  try {
    packageJsonPath = require.resolve(`${packageName}/package.json`);
  } catch (error) {
    throw new Error(
      `The Aspire CLI native package '${packageName}' was not installed. ` +
      'Reinstall @microsoft/aspire-cli with optional dependencies enabled.',
      { cause: error });
  }

  const binaryName = process.platform === 'win32' ? 'aspire.exe' : 'aspire';
  const binaryPath = path.join(path.dirname(packageJsonPath), 'bin', binaryName);
  if (!fs.existsSync(binaryPath)) {
    throw new Error(`The Aspire CLI native package '${packageName}' is missing '${binaryName}'.`);
  }

  return { binaryPath, packageName, binaryName };
}

function ensureCachedBinary(sourcePath, binaryName, version, rid) {
  const home = os.homedir() || os.tmpdir();
  const cacheRoot = process.env.ASPIRE_NPM_CACHE_DIR || path.join(home, '.aspire', 'npm');
  const targetDirectory = path.join(cacheRoot, version, rid, 'bin');
  const targetPath = path.join(targetDirectory, binaryName);

  // The Aspire CLI self-extracts relative to its process path on first run.
  // Running directly from node_modules could write into read-only package
  // stores, so copy the native binary to an Aspire-owned writable layout.
  fs.mkdirSync(targetDirectory, { recursive: true });

  if (!needsCopy(sourcePath, targetPath)) {
    return targetPath;
  }

  // Copy through a temp file and atomically rename it over the previous cache
  // entry so concurrent first runs never observe a missing or partial executable.
  const tempPath = path.join(targetDirectory, `${binaryName}.${process.pid}.${Date.now()}.tmp`);
  fs.copyFileSync(sourcePath, tempPath);

  if (process.platform !== 'win32') {
    fs.chmodSync(tempPath, 0o755);
  }

  // Node's rename uses replace-existing semantics on POSIX. On Windows, the
  // rename fails with EBUSY/EPERM if the cached executable is currently
  // running (e.g., another concurrent first-run already populated the cache
  // and is executing it). When that happens, check whether the existing
  // target is already a valid copy of the source - if it is, the other
  // process won the race and our tmp can be discarded without failing the
  // launcher. Any other error is unexpected and must propagate.
  try {
    fs.renameSync(tempPath, targetPath);
  } catch (error) {
    try {
      if (!needsCopy(sourcePath, targetPath)) {
        fs.rmSync(tempPath, { force: true });
        return targetPath;
      }
    } catch {
      // Fall through and rethrow the original rename error below.
    }

    fs.rmSync(tempPath, { force: true });
    throw error;
  }

  return targetPath;
}

function needsCopy(sourcePath, targetPath) {
  try {
    const source = fs.statSync(sourcePath);
    const target = fs.statSync(targetPath);

    // Size mismatch always means stale cache. Even when the size matches, the
    // cached binary is only trusted if its mtime is at or after the source's
    // mtime. This catches the case where a same-version reinstall replaces the
    // source binary but the cache was left from a prior install with identical
    // content size (e.g., partial overwrite, corruption, or a swapped build).
    if (source.size !== target.size) {
      return true;
    }

    if (target.mtimeMs < source.mtimeMs) {
      return true;
    }

    return false;
  } catch {
    return true;
  }
}

function main() {
  // Lazy-initialize so a missing/corrupt aspire-package-map.json reaches the
  // top-level try/catch and produces a friendly error instead of a Node stack.
  if (ridPackageNames === null) {
    ridPackageNames = loadRidPackageNames();
  }
  const packageJson = require(path.join(__dirname, '..', 'package.json'));
  const rid = detectRid();
  const nativeBinary = resolveNativeBinary(rid);
  const executablePath = ensureCachedBinary(nativeBinary.binaryPath, nativeBinary.binaryName, packageJson.version, rid);
  const child = childProcess.spawn(executablePath, process.argv.slice(2), {
    stdio: 'inherit',
    env: {
      ...process.env,
      // Surface the install context to the CLI so `aspire update --self` and
      // update notifications can route through `npm install -g` instead of
      // overwriting npm-owned files with the GitHub-binary downloader. See
      // Aspire.Cli.Utils.NpmInstallDetection.
      ASPIRE_NPM_PACKAGE: packageJson.name,
      ASPIRE_NPM_PACKAGE_VERSION: packageJson.version,
      ASPIRE_NPM_PACKAGE_RID: rid
    }
  });

  // Forward terminating signals to the child so programmatic `kill <wrapper>`
  // does not orphan the native CLI (especially important for long-lived
  // `aspire run` sessions that keep an AppHost alive). In TTY usage the kernel
  // already broadcasts SIGINT/SIGQUIT to the whole foreground process group, so
  // this primarily covers tooling that targets the wrapper PID directly.
  // SIGHUP/SIGQUIT are POSIX-only; on Windows Node maps SIGTERM/SIGINT to
  // TerminateProcess on the child, which is semantically what callers expect.
  // Use `once` so a second signal can still terminate the wrapper if the child
  // ignores the first one.
  const forwardedSignals = ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT'];
  for (const signal of forwardedSignals) {
    process.once(signal, () => {
      if (!child.killed) {
        try {
          child.kill(signal);
        } catch {
          // Best-effort: the child may have exited between the check and kill,
          // or the signal may be unsupported on this platform. Either way we
          // let the 'exit' handler below run to propagate the final state.
        }
      }
    });
  }

  child.on('error', error => {
    console.error(error.message);
    process.exit(1);
  });

  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    process.exit(code === null ? 1 : code);
  });
}

try {
  main();
} catch (error) {
  console.error(error.message);
  process.exit(1);
}
