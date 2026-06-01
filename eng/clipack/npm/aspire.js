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

function detectRid(platform = process.platform, arch = process.arch, musl = null) {

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
    if (musl === null) {
      musl = isMusl();
    }
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
  // it avoids spawning a process on glibc systems.
  if (process.report && typeof process.report.getReport === 'function') {
    const report = process.report.getReport();
    // glibcVersionRuntime is present only when the process is dynamically
    // linked against glibc, so its presence definitively rules out musl.
    if (report && report.header && report.header.glibcVersionRuntime) {
      return false;
    }

    // Some Node builds list the loaded shared objects; a musl loader/libc path
    // proves musl even when `ldd` is missing (common on minimal Alpine images).
    if (report && Array.isArray(report.sharedObjects) &&
        report.sharedObjects.some(entry => typeof entry === 'string' && /(\/ld-musl-|\/libc\.musl-)/.test(entry))) {
      return true;
    }
  }

  // `ldd --version` prints a "musl libc" banner on musl and a GNU/glibc banner
  // on glibc. Treat ldd as authoritative in BOTH directions when it produces a
  // recognizable banner: this keeps a glibc host that happens to have musl
  // installed side-by-side (so a /lib/ld-musl-*.so file exists) from being
  // misclassified as musl by the filesystem probe below.
  const lddResult = childProcess.spawnSync('ldd', ['--version'], { encoding: 'utf8' });
  const lddOutput = `${lddResult.stdout || ''}${lddResult.stderr || ''}`.toLowerCase();
  if (lddOutput.includes('musl')) {
    return true;
  }
  if (lddOutput.includes('glibc') || lddOutput.includes('gnu libc') || lddOutput.includes('gnu c library')) {
    return false;
  }

  // Last resort when `ldd` is absent or gave no recognizable banner (common on
  // stripped-down Alpine images): the musl dynamic linker is installed as
  // /lib/ld-musl-<arch>.so.1. Without this probe we would wrongly assume glibc
  // and resolve a binary whose dynamic linker is missing, crashing at exec.
  for (const dir of ['/lib', '/usr/lib']) {
    try {
      if (fs.readdirSync(dir).some(name => /^ld-musl-.*\.so/.test(name))) {
        return true;
      }
    } catch {
      // Directory unreadable or absent; try the next candidate.
    }
  }

  return false;
}

function resolveNativeBinary(rid, expectedVersion) {
  const packageName = ridPackageNames.get(rid);
  if (!packageName) {
    throw new Error(`No Aspire CLI npm package is available for RID '${rid}'.`);
  }

  let packageJsonPath;
  try {
    packageJsonPath = require.resolve(`${packageName}/package.json`);
  } catch (error) {
    if (error && error.code === 'ERR_INVALID_PACKAGE_CONFIG') {
      throw new Error(
        `Aspire CLI installation is corrupted: native package '${packageName}' package.json is not valid JSON. ` +
        'Reinstall @microsoft/aspire-cli.',
        { cause: error });
    }

    throw new Error(
      `The Aspire CLI native package '${packageName}' was not installed. ` +
      'Reinstall @microsoft/aspire-cli with optional dependencies enabled.',
      { cause: error });
  }

  // Verify the RID package version matches the pointer package version to catch
  // partial or mismatched installs before caching or spawning the binary.
  let ridPackageJson;
  try {
    ridPackageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
  } catch (error) {
    throw new Error(
      `Aspire CLI installation is corrupted: native package '${packageName}' package.json '${packageJsonPath}' is not valid JSON. ` +
      'Reinstall @microsoft/aspire-cli.',
      { cause: error });
  }
  if (ridPackageJson.version !== expectedVersion) {
    throw new Error(
      `The Aspire CLI native package '${packageName}' version ${ridPackageJson.version} ` +
      `does not match the pointer package version ${expectedVersion}. ` +
      'Reinstall @microsoft/aspire-cli.');
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
  // Create the cache as owner-only (0700) so that, if the cache root ever lands
  // in a shared location (e.g. an ASPIRE_NPM_CACHE_DIR override or the
  // os.tmpdir() fallback when no home directory is available), another local
  // user cannot pre-create or read the cached executable. Mode is ignored on
  // Windows and on pre-existing directories, so this only hardens dirs we make.
  fs.mkdirSync(targetDirectory, { recursive: true, mode: 0o700 });

  if (!needsCopy(sourcePath, targetPath)) {
    return targetPath;
  }

  // Copy through a temp file and atomically rename it over the previous cache
  // entry so concurrent first runs never observe a missing or partial executable.
  const tempPath = path.join(targetDirectory, `${binaryName}.${process.pid}.${Date.now()}.tmp`);

  // Wrap copy and chmod in try-finally to ensure temp file cleanup even when
  // copyFileSync or chmodSync fails. Without this, an I/O error or permission
  // failure during chmod would leave the .tmp file behind.
  try {
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
  } catch (error) {
    // Clean up temp file if copy or chmod failed before rename was attempted
    fs.rmSync(tempPath, { force: true });
    throw error;
  }

  return targetPath;
}

function needsCopy(sourcePath, targetPath) {
  try {
    // lstat (not stat) so the final path component is never followed: a cached
    // entry that is a symlink (or any non-regular file) is never trusted and is
    // replaced via the atomic temp-file rename below. This blocks the cheap
    // attack where a same-user process swaps the cached binary for a symlink
    // pointing at attacker-controlled content between launches.
    const target = fs.lstatSync(targetPath);
    if (!target.isFile()) {
      return true;
    }

    const source = fs.statSync(sourcePath);

    // Size mismatch always means stale cache. Even when the size matches, the
    // cached binary is only trusted if its mtime is at or after the source's
    // mtime. This catches the case where a same-version reinstall replaces the
    // source binary but the cache was left from a prior install with identical
    // content size (e.g., partial overwrite, corruption, or a swapped build).
    //
    // We deliberately do NOT byte-compare the cached binary against the source
    // on every launch. The source is tens of MB and this runs on the hot CLI
    // startup path; a same-user attacker who can pre-plant a same-size,
    // newer-mtime regular file at the cache path can equally tamper with
    // node_modules, PATH, or shell startup files, so per-launch content
    // verification would tax startup for a threat that already defeats this
    // process's trust boundary (and a TOCTOU window to exec would remain). The
    // symlink rejection above closes the only cheap, low-privilege vector.
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
  const nativeBinary = resolveNativeBinary(rid, packageJson.version);
  const executablePath = ensureCachedBinary(nativeBinary.binaryPath, nativeBinary.binaryName, packageJson.version, rid);

  // Forward terminating signals to the child so programmatic `kill <wrapper>`
  // does not orphan the native CLI (especially important for long-lived
  // `aspire run` sessions that keep an AppHost alive). In TTY usage the kernel
  // already broadcasts SIGINT to the whole foreground process group, so this
  // primarily covers tooling that targets the wrapper PID directly.
  //
  // Register the handlers BEFORE spawning so a signal that arrives between
  // spawn and registration cannot terminate the wrapper and orphan the child;
  // `child` is captured by reference and is always assigned before any handler
  // can run (signal callbacks fire on a later tick, after this stack unwinds).
  //
  // SIGQUIT is POSIX-only: on Windows `process.once('SIGQUIT', ...)` throws
  // `uv_signal_start EINVAL` because libuv cannot map it, which would crash the
  // launcher (and orphan the child spawned below) on every Windows invocation.
  // So build the list per platform: on Windows Node maps SIGINT/SIGTERM/SIGHUP
  // to TerminateProcess on the child and SIGBREAK covers Ctrl+Break; on POSIX
  // add SIGQUIT. Each registration is additionally wrapped so an unexpected
  // platform/libuv mismatch can never crash the launcher.
  // Use `once` so a second signal can still terminate the wrapper if the child
  // ignores the first one.
  let child;
  const forwardedSignals = process.platform === 'win32'
    ? ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']
    : ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT'];
  for (const signal of forwardedSignals) {
    try {
      process.once(signal, () => {
        if (child && !child.killed) {
          try {
            child.kill(signal);
          } catch {
            // Best-effort: the child may have exited between the check and kill,
            // or the signal may be unsupported on this platform. Either way we
            // let the 'exit' handler below run to propagate the final state.
          }
        }
      });
    } catch {
      // Registering a listener for a signal this platform's libuv rejects must
      // not crash the launcher.
    }
  }

  child = childProcess.spawn(executablePath, process.argv.slice(2), {
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

if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exit(1);
  }
}

module.exports = {
  __testing: {
    detectRid
  }
};
