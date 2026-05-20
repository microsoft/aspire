#!/usr/bin/env node
'use strict';

const childProcess = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

// Package names are generated at pack time so changing the npm package name in
// MSBuild does not require editing this launcher.
const ridPackageNames = loadRidPackageNames();

function loadRidPackageNames() {
  const packageMapPath = path.join(__dirname, 'aspire-package-map.json');
  return new Map(Object.entries(JSON.parse(fs.readFileSync(packageMapPath, 'utf8'))));
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
    if (arch === 'x64' && isMusl()) {
      return 'linux-musl-x64';
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

  // Copy through a temp file so concurrent first runs never observe a partial
  // executable at the final path.
  const tempPath = path.join(targetDirectory, `${binaryName}.${process.pid}.${Date.now()}.tmp`);
  fs.copyFileSync(sourcePath, tempPath);

  if (process.platform !== 'win32') {
    fs.chmodSync(tempPath, 0o755);
  }

  try {
    fs.rmSync(targetPath, { force: true });
    fs.renameSync(tempPath, targetPath);
  } catch (error) {
    fs.rmSync(tempPath, { force: true });
    throw error;
  }

  return targetPath;
}

function needsCopy(sourcePath, targetPath) {
  try {
    const source = fs.statSync(sourcePath);
    const target = fs.statSync(targetPath);
    return source.size !== target.size;
  } catch {
    return true;
  }
}

function main() {
  const packageJson = require(path.join(__dirname, '..', 'package.json'));
  const rid = detectRid();
  const nativeBinary = resolveNativeBinary(rid);
  const executablePath = ensureCachedBinary(nativeBinary.binaryPath, nativeBinary.binaryName, packageJson.version, rid);
  const child = childProcess.spawn(executablePath, process.argv.slice(2), {
    stdio: 'inherit',
    env: {
      ...process.env,
      // Future CLI-side update detection can use these values to distinguish
      // npm installs from dotnet tool installs.
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

try {
  main();
} catch (error) {
  console.error(error.message);
  process.exit(1);
}
