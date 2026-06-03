#!/usr/bin/env node
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

const extensionRoot = path.resolve(__dirname, '..');
const extensionPackageJson = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
const repoRoot = path.resolve(extensionRoot, '..');
const verifyExtesterFeedOnly = process.argv.includes('--verify-extester-feed');
const artifactsDir = path.join(extensionRoot, '.test-artifacts');
const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');
const resultsDir = path.join(extensionRoot, '.test-results', 'e2e', shardName);
const runId = `${process.pid}-${Date.now()}`;
const diagnosticsStorageRoot = path.join(extensionRoot, '.test-storage');
const requestedTempRoot = verifyExtesterFeedOnly ? '' : process.env.ASPIRE_EXTENSION_E2E_TEMP_ROOT || os.tmpdir();
if (!verifyExtesterFeedOnly) {
  fs.mkdirSync(requestedTempRoot, { recursive: true });
}
const tempRoot = verifyExtesterFeedOnly ? '' : fs.realpathSync.native(requestedTempRoot);
const shortRunRoot = verifyExtesterFeedOnly ? '' : fs.mkdtempSync(path.join(tempRoot, 'aev-'));
const isolatedAspireHome = path.join(shortRunRoot, 'aspire-home');
const storageDir = path.join(shortRunRoot, 'storage');
const extensionsDir = path.join(shortRunRoot, 'extensions');
const workspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT
  ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT)
  : path.join(shortRunRoot, 'workspace');
const workspaceMarkerFile = path.join(workspaceRoot, '.aspire-extension-e2e-workspace');
const storageDiagnosticsDir = path.join(diagnosticsStorageRoot, shardName, runId);
const workspaceDiagnosticsDir = path.join(extensionRoot, '.test-workspaces', shardName, runId);
const recordingsDir = path.join(extensionRoot, '.test-recordings', shardName);
const defaultVsixPath = path.join(artifactsDir, 'aspire-extension-e2e.vsix');
const stateFile = path.join(resultsDir, 'extension-state.json');
const controlFile = path.join(resultsDir, 'extension-control.json');
const testSpec = process.env.ASPIRE_EXTENSION_E2E_SPEC || 'out/test-e2e/**/*.e2e.test.js';
const matchedTestSpecs = verifyExtesterFeedOnly ? [] : findSpecMatches(testSpec);
const vscodeVersion = process.env.ASPIRE_EXTENSION_E2E_VSCODE_VERSION || '1.122.1';
const extesterVersion = extensionPackageJson.devDependencies?.['vscode-extension-tester'];
if (!extesterVersion) {
  throw new Error('vscode-extension-tester must be pinned in extension/package.json devDependencies.');
}
const extesterNodeModules = path.join(extensionRoot, 'node_modules');
const extesterModule = path.join(extesterNodeModules, 'vscode-extension-tester');
const extesterCli = path.join(extesterModule, 'out', 'cli.js');
const primaryAppHostProject = path.join(workspaceRoot, 'AspireE2E.AppHost', 'AspireE2E.AppHost.csproj');
const workspaceNuGetConfigPath = path.join(workspaceRoot, 'NuGet.config');
let cliPathForCleanup;
const csharpFileHeader = `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

`;

if (!verifyExtesterFeedOnly) {
  removePath(resultsDir, { recursive: true, force: true });
  removePath(recordingsDir, { recursive: true, force: true });
  for (const directory of [artifactsDir, resultsDir, diagnosticsStorageRoot, isolatedAspireHome, storageDir, extensionsDir]) {
    fs.mkdirSync(directory, { recursive: true });
  }
}

function runWithProcessTreeTimeout(command, args, extraEnv, timeout) {
  return new Promise((resolve, reject) => {
    const useShell = shouldUseShellForCommand(command);
    const child = useShell
      ? spawn([command, ...args].map(quoteWindowsShellArgument).join(' '), [], {
        cwd: extensionRoot,
        env: { ...process.env, ...extraEnv },
        shell: true,
        stdio: 'inherit',
        detached: process.platform !== 'win32',
      })
      : spawn(command, args, {
        cwd: extensionRoot,
        env: { ...process.env, ...extraEnv },
        shell: false,
        stdio: 'inherit',
        detached: process.platform !== 'win32',
      })

    let timedOut = false;
    let settled = false;
    let forceTimeout;
    const timer = setTimeout(() => {
      timedOut = true;
      terminateProcessTree(child.pid, 'SIGTERM');
      forceTimeout = setTimeout(() => {
        if (settled) {
          return;
        }

        terminateProcessTree(child.pid, 'SIGKILL');
        child.removeAllListeners();
        child.unref();
        settle();
        reject(new Error(`${command} ${args.join(' ')} timed out after ${timeout}ms and did not exit after process-tree termination. Diagnostics are under ${path.relative(extensionRoot, resultsDir)} and ${path.relative(extensionRoot, storageDiagnosticsDir)}.`));
      }, 15000);
    }, timeout);

    child.on('error', error => {
      if (settled) {
        return;
      }

      settle();
      reject(error);
    })

    child.on('close', (exitCode, signal) => {
      if (settled) {
        return;
      }

      settle();
      if (timedOut) {
        reject(new Error(`${command} ${args.join(' ')} timed out after ${timeout}ms. Diagnostics are under ${path.relative(extensionRoot, resultsDir)} and ${path.relative(extensionRoot, storageDiagnosticsDir)}.`));
        return;
      }

      if (exitCode !== 0) {
        reject(new Error(`${command} ${args.join(' ')} exited with code ${exitCode ?? `signal ${signal ?? 'unknown'}`}. Diagnostics are under ${path.relative(extensionRoot, resultsDir)} and ${path.relative(extensionRoot, storageDiagnosticsDir)}.`));
        return;
      }

      resolve();
    });

    function settle() {
      settled = true;
      clearTimeout(timer);
      if (forceTimeout) {
        clearTimeout(forceTimeout);
      }
    }
  });
}

function getRunTestsTimeoutMs() {
  const configured = Number(process.env.ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS || 2400000);
  if (!Number.isFinite(configured) || configured <= 0) {
    throw new Error(`ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS must be a positive number. Got '${process.env.ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS}'.`);
  }

  return configured;
}

function redactStateFileForArtifacts() {
  const state = readJsonIfExists(stateFile);
  if (!state) {
    return;
  }

  redactDashboardUrls(state);
  fs.writeFileSync(stateFile, JSON.stringify(state, null, 2));
}

function redactDashboardUrls(value) {
  if (!value || typeof value !== 'object') {
    return;
  }

  if (Array.isArray(value)) {
    for (const item of value) {
      redactDashboardUrls(item);
    }
    return;
  }

  for (const [key, item] of Object.entries(value)) {
    if (key === 'dashboardUrl' && typeof item === 'string') {
      value[key] = sanitizeDashboardUrlForDiagnostics(item);
    }
    else {
      redactDashboardUrls(item);
    }
  }
}

function redactDebugSessionForDiagnostics(session) {
  return {
    ...session,
    dashboardUrl: sanitizeDashboardUrlForDiagnostics(session.dashboardUrl),
  };
}

function sanitizeDashboardUrlForDiagnostics(url) {
  if (!url) {
    return url;
  }

  try {
    return new URL(stripResourceSuffix(url)).origin;
  }
  catch {
    return '<redacted>';
  }
}

function stripResourceSuffix(url) {
  const idx = url.indexOf('/?resource=');
  return idx !== -1 ? url.substring(0, idx) : url;
}

main().catch(error => {
  console.error(error instanceof Error ? error.stack ?? error.message : String(error));
  process.exitCode = 1;
});

function shouldUseShellForCommand(command) {
  // npm and corepack are .cmd shims on Windows. Node.js 20+ intentionally refuses
  // to spawn .cmd/.bat files with shell:false, so use cmd.exe only for those tools.
  return process.platform === 'win32' && (command === 'npm' || command === 'corepack');
}

function assertSpecMatches(spec) {
  if (matchedTestSpecs.length === 0) {
    throw new Error(`E2E spec '${spec}' did not match any compiled test files under ${path.relative(extensionRoot, path.join(extensionRoot, 'out', 'test-e2e'))}. Run corepack yarn@1.22.22 compile-e2e and check ASPIRE_EXTENSION_E2E_SPEC.`);
  }
}

function logE2eConfiguration() {
  console.log('Aspire extension E2E configuration:');
  console.log(`  shard: ${shardName}`);
  console.log(`  spec: ${testSpec}`);
  console.log(`  matched specs: ${matchedTestSpecs.map(file => path.relative(extensionRoot, file)).join(', ')}`);
  console.log(`  VS Code: ${vscodeVersion}`);
  console.log(`  ExTester: ${extesterVersion}`);
  console.log(`  current CLI regressions: ${process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true' ? 'skipped' : 'included'}`);
  console.log(`  results: ${path.relative(extensionRoot, resultsDir)}`);
  console.log(`  storage diagnostics: ${path.relative(extensionRoot, storageDiagnosticsDir)}`);
  console.log(`  workspace diagnostics: ${path.relative(extensionRoot, workspaceDiagnosticsDir)}`);
}

function logStep(name) {
  console.log(`\n--- ${name} ---`);
}

function findSpecMatches(spec) {
  const absolutePattern = path.resolve(extensionRoot, spec);
  if (!hasGlobSyntax(spec)) {
    return fs.existsSync(absolutePattern) ? [absolutePattern] : [];
  }

  const root = getGlobSearchRoot(absolutePattern);
  if (!root || !fs.existsSync(root)) {
    return [];
  }

  const patternRegex = globToRegExp(toPosixPath(absolutePattern));
  return getFilesRecursive(root).filter(file => patternRegex.test(toPosixPath(file)));
}

function getGlobSearchRoot(pattern) {
  const firstGlobIndex = pattern.search(/[*?\[\]{}]/);
  if (firstGlobIndex === -1) {
    return path.dirname(pattern);
  }

  const prefix = pattern.slice(0, firstGlobIndex);
  const lastSeparator = Math.max(prefix.lastIndexOf(path.sep), prefix.lastIndexOf('/'), prefix.lastIndexOf('\\'));
  return lastSeparator === -1 ? extensionRoot : prefix.slice(0, lastSeparator);
}

function getFilesRecursive(directory) {
  const entries = fs.readdirSync(directory, { withFileTypes: true });
  return entries.flatMap(entry => {
    const entryPath = path.join(directory, entry.name);
    return entry.isDirectory() ? getFilesRecursive(entryPath) : [entryPath];
  });
}

function hasGlobSyntax(value) {
  return /[*?\[\]{}]/.test(value);
}

function globToRegExp(pattern) {
  let expression = '^';
  for (let i = 0; i < pattern.length; i++) {
    const character = pattern[i];
    const nextCharacter = pattern[i + 1];
    if (character === '*' && nextCharacter === '*' && pattern[i + 2] === '/') {
      expression += '(?:.*/)?';
      i += 2;
    }
    else if (character === '*' && nextCharacter === '*') {
      expression += '.*';
      i++;
    }
    else if (character === '*') {
      expression += '[^/]*';
    }
    else if (character === '?') {
      expression += '[^/]';
    }
    else if (character === '{') {
      const endBrace = pattern.indexOf('}', i + 1);
      if (endBrace !== -1) {
        const alternatives = pattern.slice(i + 1, endBrace).split(',').map(escapeRegExp).join('|');
        expression += `(?:${alternatives})`;
        i = endBrace;
      }
      else {
        expression += escapeRegExp(character);
      }
    }
    else {
      expression += escapeRegExp(character);
    }
  }

  return new RegExp(`${expression}$`);
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function toPosixPath(value) {
  return path.resolve(value).replace(/^\\\\\?\\/, '').split(path.sep).join('/');
}

function writeVsCodeLocaleFile() {
  const userDataDirectory = path.join(storageDir, 'settings', 'User');
  fs.mkdirSync(userDataDirectory, { recursive: true });
  fs.writeFileSync(path.join(userDataDirectory, 'locale.json'), JSON.stringify({ locale: 'en' }, undefined, 2));
}

function startRecording() {
  const mode = getRecordingMode();
  if (mode === 'off') {
    return undefined;
  }

  if (process.platform !== 'linux') {
    console.warn(`Skipping Aspire extension E2E recording because '${mode}' recording is only supported on Linux runners.`);
    return undefined;
  }

  const display = process.env.DISPLAY;
  if (!display) {
    console.warn('Skipping Aspire extension E2E recording because DISPLAY is not set.');
    return undefined;
  }

  const ffmpegCheck = spawnSync('ffmpeg', ['-version'], { encoding: 'utf8', stdio: 'ignore', timeout: 15000 });
  if (ffmpegCheck.error || ffmpegCheck.status !== 0) {
    console.warn('Skipping Aspire extension E2E recording because ffmpeg is not available.');
    return undefined;
  }

  fs.mkdirSync(recordingsDir, { recursive: true });
  const outputPath = path.join(recordingsDir, `${runId}.mp4`);
  const displayInput = display.includes('.') ? display : `${display}.0`;
  const args = [
    '-y',
    '-video_size',
    process.env.ASPIRE_EXTENSION_E2E_RECORDING_SIZE || '1280x1024',
    '-framerate',
    process.env.ASPIRE_EXTENSION_E2E_RECORDING_FRAMERATE || '15',
    '-f',
    'x11grab',
    '-draw_mouse',
    '1',
    '-i',
    displayInput,
    '-an',
    '-c:v',
    'libx264',
    '-preset',
    'ultrafast',
    '-pix_fmt',
    'yuv420p',
    outputPath,
  ];
  const logPath = path.join(recordingsDir, `${runId}.ffmpeg.log`);
  const logFd = fs.openSync(logPath, 'w');
  const ffmpeg = spawn('ffmpeg', args, {
    stdio: ['ignore', logFd, logFd],
    detached: false,
  });

  ffmpeg.on('error', error => {
    console.warn(`Aspire extension E2E recording failed to start: ${error.message}`);
  });
  const closed = new Promise(resolve => {
    ffmpeg.once('close', (exitCode, signal) => resolve({ exitCode, signal }));
    ffmpeg.once('error', error => resolve({ error }));
  });

  return {
    mode,
    outputPath,
    logPath,
    pid: ffmpeg.pid,
    closed,
    closeLog: () => fs.closeSync(logFd),
  };
}

function getRecordingMode() {
  const configured = (process.env.ASPIRE_EXTENSION_E2E_RECORDING_MODE || 'off').toLowerCase();
  if (configured === 'off' || configured === 'failure' || configured === 'always') {
    return configured;
  }

  throw new Error(`ASPIRE_EXTENSION_E2E_RECORDING_MODE must be 'off', 'failure', or 'always'. Got '${process.env.ASPIRE_EXTENSION_E2E_RECORDING_MODE}'.`);
}

async function stopRecording(recording, testFailure) {
  if (!recording) {
    return;
  }

  let stoppedGracefully = false;
  try {
    if (recording.pid) {
      stoppedGracefully = await stopRecordingProcess(recording.pid, recording.closed);
    }
    else {
      await waitForProcessClose(recording.closed, 15000);
      stoppedGracefully = true;
    }
  }
  finally {
    recording.closeLog();
  }

  const keepRecording = recording.mode === 'always' || (recording.mode === 'failure' && testFailure);
  if (!keepRecording) {
    fs.rmSync(recording.outputPath, { force: true });
    fs.rmSync(recording.logPath, { force: true });
    return;
  }

  if (stoppedGracefully && fs.existsSync(recording.outputPath)) {
    console.log(`Aspire extension E2E recording saved to ${recording.outputPath}`);
  }
  else {
    console.warn(`Aspire extension E2E recording was requested but was not saved cleanly. Check ${recording.logPath}.`);
  }
}

async function stopRecordingProcess(pid, closed) {
  signalProcess(pid, 'SIGINT');
  if (await waitForProcessClose(closed, 15000)) {
    return true;
  }

  signalProcess(pid, 'SIGTERM');
  if (await waitForProcessClose(closed, 5000)) {
    return false;
  }

  signalProcess(pid, 'SIGKILL');
  if (await waitForProcessClose(closed, 5000)) {
    return false;
  }

  throw new Error(`ffmpeg recording process ${pid} did not exit after SIGINT, SIGTERM, and SIGKILL.`);
}

function signalProcess(pid, signal) {
  try {
    process.kill(pid, signal);
  }
  catch (error) {
    if (!error || error.code !== 'ESRCH') {
      throw error;
    }
  }
}

function waitForProcessClose(closed, timeoutMs) {
  return new Promise(resolve => {
    const timeout = setTimeout(() => resolve(false), timeoutMs);
    closed.then(() => {
      clearTimeout(timeout);
      resolve(true);
    }, () => {
      clearTimeout(timeout);
      resolve(true);
    });
  });
}

async function main() {
  let recording;
  let testFailure;
  let completedTests = false;
  try {
    if (verifyExtesterFeedOnly) {
      verifyExtesterFeed();
      return;
    }

    assertSpecMatches(testSpec);
    logE2eConfiguration();

    const cliPath = isolateCliPath(resolveCliPath());
    cliPathForCleanup = cliPath;
    validateCliPath(cliPath);
    const appHostSdkVersion = resolveAppHostSdkVersion(cliPath);
    prepareWorkspaceFixture(cliPath, appHostSdkVersion);
    restoreWorkspaceFixture();
    const vsixPath = process.env.ASPIRE_EXTENSION_E2E_VSIX
      ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_VSIX)
      : packageVsix();

    if (!fs.existsSync(vsixPath)) {
      throw new Error(`VSIX not found at ${vsixPath}`);
    }
    validateVsix(vsixPath);

    ensureExtester();
    patchExtesterLaunchLocale();
    writeVsCodeLocaleFile();

    const extestEnv = getAspireCliEnvironment({
      ASPIRE_EXTENSION_E2E_CLI_PATH: cliPath,
      ASPIRE_EXTENSION_E2E_EXTENSION_ROOT: extensionRoot,
      ASPIRE_EXTENSION_E2E_REPO_ROOT: repoRoot,
      ASPIRE_EXTENSION_E2E_RESULTS_DIR: resultsDir,
      ASPIRE_EXTENSION_E2E_RUN_ROOT: shortRunRoot,
      ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT: workspaceRoot,
      ASPIRE_EXTENSION_E2E_STATE_FILE: stateFile,
      ASPIRE_EXTENSION_E2E_CONTROL_FILE: controlFile,
      ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE: 'true',
      ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS: process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true' ? 'true' : 'false',
      ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST: primaryAppHostProject,
      ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION: appHostSdkVersion,
      ASPIRE_EXTENSION_E2E_EXTESTER_MODULE: extesterModule,
      VSCODE_NLS_CONFIG: JSON.stringify({ locale: 'en', availableLanguages: {} }),
      LANG: 'C.UTF-8',
      LC_ALL: 'C.UTF-8',
      NODE_PATH: [extesterNodeModules, process.env.NODE_PATH].filter(Boolean).join(path.delimiter),
    });

    logStep('Downloading VS Code');
    runWithRetry(process.execPath, [extesterCli, 'get-vscode', '--storage', storageDir, '--code_version', vscodeVersion], extestEnv, { attempts: 2, retryDelayMs: 5000, beforeRetry: cleanPartialExtesterDownloads, timeout: 240000 });
    logStep('Downloading ChromeDriver');
    runWithRetry(process.execPath, [extesterCli, 'get-chromedriver', '--storage', storageDir, '--code_version', vscodeVersion], extestEnv, { attempts: 2, retryDelayMs: 5000, beforeRetry: cleanPartialExtesterDownloads, timeout: 240000 });
    logStep('Installing VSIX');
    run(process.execPath, [extesterCli, 'install-vsix', '--storage', storageDir, '--extensions_dir', extensionsDir, '--vsix_file', vsixPath], extestEnv, { timeout: 300000 });

    recording = startRecording();
    try {
      logStep('Running VS Code extension E2E tests');
      await runWithProcessTreeTimeout(process.execPath, [extesterCli, 'run-tests', testSpec, '--storage', storageDir, '--extensions_dir', extensionsDir, '--code_version', vscodeVersion, '--code_settings', path.join(extensionRoot, 'test-e2e', 'settings.json'), '--mocha_config', path.join(extensionRoot, '.mocharc.e2e.js')], extestEnv, getRunTestsTimeoutMs());
    }
    catch (error) {
      testFailure = error;
    }
    completedTests = true;
  }
  finally {
    const cleanupErrors = [];
    await runCleanupStep('stop recording', () => stopRecording(recording, testFailure), cleanupErrors);
    await runCleanupStep('stop workspace AppHost', stopWorkspaceAppHost, cleanupErrors);
    await runCleanupStep('redact extension state', redactStateFileForArtifacts, cleanupErrors);
    await runCleanupStep('redact test results', () => redactTextFilesForArtifacts(resultsDir), cleanupErrors);
    await runCleanupStep('copy storage diagnostics', copyStorageDiagnostics, cleanupErrors);
    await runCleanupStep('copy workspace diagnostics', copyWorkspaceDiagnostics, cleanupErrors);
    await runCleanupStep('cleanup temporary run root', cleanupTemporaryRunRoot, cleanupErrors);

    if (cleanupErrors.length > 0) {
      const cleanupFailure = new AggregateError(cleanupErrors, 'One or more E2E cleanup steps failed.');
      if (testFailure) {
        console.error(cleanupFailure);
      }
      else {
        testFailure = cleanupFailure;
      }
    }
  }

  if (testFailure) {
    printFailureDiagnosticsSummary();
    throw testFailure;
  }

  if (completedTests) {
    printSuccessDiagnosticsSummary();
  }
}

async function runCleanupStep(name, action, cleanupErrors) {
  try {
    await action();
  }
  catch (error) {
    const cleanupError = error instanceof Error ? error : new Error(String(error));
    cleanupError.message = `${name}: ${cleanupError.message}`;
    cleanupErrors.push(cleanupError);
  }
}

function resolveCliPath() {
  if (process.env.ASPIRE_EXTENSION_E2E_CLI_PATH) {
    const configuredPath = path.resolve(process.env.ASPIRE_EXTENSION_E2E_CLI_PATH);
    if (!fs.existsSync(configuredPath)) {
      throw new Error(`ASPIRE_EXTENSION_E2E_CLI_PATH points to a missing file: ${configuredPath}`);
    }

    return configuredPath;
  }

  if (process.env.CI) {
    throw new Error('ASPIRE_EXTENSION_E2E_CLI_PATH is required in CI so E2E tests run against a known Aspire CLI build.');
  }

  const candidatePaths = process.platform === 'win32'
    ? [
      path.join(repoRoot, 'artifacts', 'bin', 'aspire', 'Debug', 'net10.0', 'aspire.exe'),
      path.join(repoRoot, 'artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', 'aspire.exe'),
    ]
    : [
      path.join(repoRoot, 'artifacts', 'bin', 'aspire', 'Debug', 'net10.0', 'aspire'),
      path.join(repoRoot, 'artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', 'aspire'),
    ];

  const candidatePath = candidatePaths.find(p => fs.existsSync(p));
  if (!candidatePath) {
    throw new Error(`ASPIRE_EXTENSION_E2E_CLI_PATH is not set and no local Aspire CLI was found. Checked: ${candidatePaths.join(', ')}`);
  }

  return candidatePath;
}

function isolateCliPath(resolvedCliPath) {
  const sourceDirectory = path.dirname(resolvedCliPath);
  const isolatedDirectory = path.join(shortRunRoot, 'cli');
  fs.rmSync(isolatedDirectory, { recursive: true, force: true });
  fs.cpSync(sourceDirectory, isolatedDirectory, { recursive: true });
  fs.rmSync(path.join(isolatedDirectory, '.aspire-install.json'), { force: true });

  const isolatedCliPath = path.join(isolatedDirectory, path.basename(resolvedCliPath));
  if (!fs.existsSync(isolatedCliPath)) {
    throw new Error(`Isolated Aspire CLI copy did not contain ${path.basename(resolvedCliPath)} from ${sourceDirectory}.`);
  }

  if (process.platform !== 'win32') {
    fs.chmodSync(isolatedCliPath, fs.statSync(isolatedCliPath).mode | 0o700);
  }

  return isolatedCliPath;
}

function validateCliPath(resolvedCliPath) {
  const result = spawnSync(resolvedCliPath, ['--version'], {
    cwd: extensionRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: 60000,
  });

  if (result.error) {
    throw new Error(`Unable to execute Aspire CLI at ${resolvedCliPath}: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(`Aspire CLI at ${resolvedCliPath} failed --version with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
  }
}

function packageVsix() {
  run('corepack', ['yarn@1.22.22', 'run', 'vsce', 'package', '--pre-release', '-o', defaultVsixPath], {}, { timeout: 300000 });
  return defaultVsixPath;
}

function validateVsix(resolvedVsixPath) {
  const stat = fs.statSync(resolvedVsixPath);
  if (stat.size < 100 * 1024) {
    throw new Error(`VSIX at ${resolvedVsixPath} is unexpectedly small (${stat.size} bytes).`);
  }

  const header = Buffer.alloc(4);
  const fd = fs.openSync(resolvedVsixPath, 'r');
  try {
    fs.readSync(fd, header, 0, header.length, 0);
  }
  finally {
    fs.closeSync(fd);
  }

  if (header.toString('utf8') !== 'PK\u0003\u0004') {
    throw new Error(`VSIX at ${resolvedVsixPath} does not look like a ZIP package.`);
  }
}

function ensureExtester() {
  const installedPackageJson = path.join(extesterModule, 'package.json');
  if (fs.existsSync(installedPackageJson)) {
    const installed = JSON.parse(fs.readFileSync(installedPackageJson, 'utf8'));
    if (installed.version === extesterVersion && fs.existsSync(extesterCli)) {
      return;
    }

    throw new Error(`Expected vscode-extension-tester@${extesterVersion} from the locked extension dependencies, but found ${installed.version}. Run corepack yarn install --frozen-lockfile after updating package.json/yarn.lock.`);
  }

  throw new Error(`vscode-extension-tester@${extesterVersion} is missing from extension/node_modules. Run corepack yarn install --frozen-lockfile so the E2E runner uses the pinned dependency graph from extension/yarn.lock.`);
}

function verifyExtesterFeed() {
  console.log(`Verifying vscode-extension-tester@${extesterVersion} from the locked extension dependency graph.`);
  ensureExtester();
}

// ExTester 8.23.0 does not expose a supported way to open VS Code with a workspace
// folder. Starting with the workspace already open avoids a slower control-bridge
// reload path and removes a startup race where discovery begins in an empty window.
// Remove this patch when ExTester exposes a stable launch option for a folder/workspace.
function patchExtesterLaunchLocale() {
  const browserPath = path.join(extesterModule, 'out', 'browser.js');
  const source = fs.readFileSync(browserPath, 'utf8');
  const workspaceArgument = JSON.stringify(workspaceRoot);
  const targets = [
    "const args = ['--no-sandbox', '--disable-dev-shm-usage', '--lang=en-US', '--disable-keytar', '--use-inmemory-secretstorage', '--password-store=basic', '--disable-extension', 'vscode.github-authentication', '--disable-extension', 'vscode.microsoft-authentication', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];",
    "const args = ['--no-sandbox', '--disable-dev-shm-usage', '--lang=en-US', '--use-inmemory-secretstorage', '--password-store=basic', '--disable-extension', 'vscode.github-authentication', '--disable-extension', 'vscode.microsoft-authentication', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];",
    "const args = ['--no-sandbox', '--disable-dev-shm-usage', '--lang=en-US', '--use-inmemory-secretstorage', '--password-store=basic', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];",
    "const args = ['--no-sandbox', '--disable-dev-shm-usage', '--lang=en-US', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];",
    "const args = ['--no-sandbox', '--disable-dev-shm-usage', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];",
  ];
  const replacement = `const args = ['--no-sandbox', '--disable-dev-shm-usage', '--disable-telemetry', '--lang=en-US', '--disable-keytar', '--use-inmemory-secretstorage', '--password-store=basic', '--disable-extension', 'vscode.github-authentication', '--disable-extension', 'vscode.microsoft-authentication', \`--user-data-dir=\${path.join(this.storagePath, 'settings')}\`, ${workspaceArgument}];`;

  if (source.includes(replacement)) {
    return;
  }

  const target = targets.find(candidate => source.includes(candidate));
  const argsDeclarationPattern = /const args = \[[^\n]*`--user-data-dir=\$\{path\.join\(this\.storagePath, 'settings'\)\}`(?:, [^\n]+?)?\];/;
  if (target) {
    console.log('Patching ExTester VS Code launch arguments by exact 8.23.0 argument match.');
    fs.writeFileSync(browserPath, source.replace(target, () => replacement));
  } else if (argsDeclarationPattern.test(source)) {
    console.log('Patching ExTester VS Code launch arguments by fallback argument-line match.');
    fs.writeFileSync(browserPath, source.replace(argsDeclarationPattern, () => replacement));
  } else {
    throw new Error(`Unable to patch ExTester VS Code launch arguments in ${browserPath} to force the E2E browser locale.`);
  }
}

function prepareWorkspaceFixture(resolvedCliPath, resolvedAppHostSdkVersion) {
  assertWorkspaceRootSafeForDeletion();
  fs.rmSync(workspaceRoot, { recursive: true, force: true });
  fs.mkdirSync(workspaceRoot, { recursive: true });
  fs.writeFileSync(workspaceMarkerFile, `${runId}\n`);
  writeWorkerProject('AspireE2E.Worker');
  writeAppHostProject('AspireE2E.AppHost', resolvedAppHostSdkVersion);
  writeNuGetConfigIfLocalPackageSourcesExist();

  const vscodeDirectory = path.join(workspaceRoot, '.vscode');
  fs.mkdirSync(vscodeDirectory, { recursive: true });
  fs.writeFileSync(path.join(vscodeDirectory, 'settings.json'), JSON.stringify({
    'aspire.aspireCliExecutablePath': resolvedCliPath,
    'aspire.closeDashboardOnDebugEnd': true,
    'aspire.dashboardBrowser': 'integratedBrowser',
    'aspire.enableAspireDashboardAutoLaunch': 'launch',
    'aspire.enableAutoRestore': false,
    'aspire.enableSettingsFileCreationPromptOnStartup': false,
    'aspire.appHostDiscoveryTimeoutMs': 120000,
    'aspire.globalAppHostsPollingInterval': 1000,
  }, undefined, 2));

  fs.writeFileSync(path.join(workspaceRoot, 'aspire.config.json'), JSON.stringify({
    appHost: {
      path: path.join('AspireE2E.AppHost', 'AspireE2E.AppHost.csproj'),
    },
  }, undefined, 2));
}

function restoreWorkspaceFixture() {
  if (process.env.ASPIRE_EXTENSION_E2E_SKIP_RESTORE_PREWARM === 'true') {
    return;
  }

  if (!fs.existsSync(workspaceNuGetConfigPath)) {
    console.warn('Skipping Aspire E2E fixture restore prewarm because no local NuGet package source was found.');
    return;
  }

  const result = spawnSync('dotnet', ['restore', primaryAppHostProject, '--configfile', workspaceNuGetConfigPath], {
    cwd: workspaceRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: Number(process.env.ASPIRE_EXTENSION_E2E_RESTORE_TIMEOUT_MS || 300000),
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`Restoring the Aspire E2E fixture failed with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
  }
}

function writeAppHostProject(projectName, resolvedAppHostSdkVersion) {
  const projectDirectory = path.join(workspaceRoot, projectName);
  fs.mkdirSync(projectDirectory, { recursive: true });
  fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Aspire.AppHost.Sdk/${resolvedAppHostSdkVersion}">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../AspireE2E.Worker/AspireE2E.Worker.csproj" />
  </ItemGroup>

</Project>
`);

  fs.writeFileSync(path.join(projectDirectory, 'AppHost.cs'), `${csharpFileHeader}#pragma warning disable ASPIREINTERACTION001

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AspireE2E_Worker>("e2e-worker")
    .WithHttpEndpoint(name: "http")
    .WithCommand(
        "echo-arguments",
        "echo-arguments",
        static _ => Task.FromResult(CommandResults.Success()),
        new CommandOptions
        {
            Arguments =
            [
                new InteractionInput { Name = "message", Label = "Message", InputType = InputType.Text, Required = true },
                new InteractionInput
                {
                    Name = "mode",
                    Label = "Mode",
                    InputType = InputType.Choice,
                    Options =
                    [
                        new("alpha", "Alpha"),
                        new("beta", "Beta"),
                    ],
                },
                new InteractionInput { Name = "enabled", Label = "Enabled", InputType = InputType.Boolean, Value = "false" },
                new InteractionInput { Name = "threshold", Label = "Threshold", InputType = InputType.Number },
                new InteractionInput { Name = "token", Label = "Token", InputType = InputType.SecretText },
            ],
        })
    .WithCommand(
        "disabled-e2e-command",
        "disabled-e2e-command",
        static _ => Task.FromResult(CommandResults.Success()),
        new CommandOptions
        {
            Description = "Disabled command shown in the VS Code tree.",
            UpdateState = _ => ResourceCommandState.Disabled,
        })
    .WithCommand(
        "hidden-e2e-command",
        "hidden-e2e-command",
        static _ => Task.FromResult(CommandResults.Success()),
        new CommandOptions
        {
            Description = "Hidden command excluded from the VS Code tree.",
            UpdateState = _ => ResourceCommandState.Hidden,
        })
    .WithCommand(
        "api-only-e2e-command",
        "api-only-e2e-command",
        static _ => Task.FromResult(CommandResults.Success()),
        new CommandOptions
        {
            Description = "API-only command excluded from the VS Code tree.",
            Visibility = ResourceCommandVisibility.Api,
        })
    .WithCommand(
        "unknown-state-e2e-command",
        "unknown-state-e2e-command",
        static _ => Task.FromResult(CommandResults.Success()),
        new CommandOptions
        {
            Description = "Unknown-state command excluded from the VS Code tree.",
            UpdateState = _ => (ResourceCommandState)999,
        });

builder.AddResource(new NoCommandsResource("e2e-no-commands"));

builder.Build().Run();

sealed class NoCommandsResource(string name) : Aspire.Hosting.ApplicationModel.Resource(name);
`);
}

function writeWorkerProject(projectName) {
  const projectDirectory = path.join(workspaceRoot, projectName);
  fs.mkdirSync(projectDirectory, { recursive: true });
  fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
`);

  fs.writeFileSync(path.join(projectDirectory, 'Program.cs'), `${csharpFileHeader}var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "ok");

app.Run();
`);
}

function resolveAppHostSdkVersion(resolvedCliPath) {
  if (process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION) {
    return process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
  }

  const availablePackageVersions = getAvailableAppHostSdkVersions();
  const versionResult = spawnSync(resolvedCliPath, ['--version'], {
    cwd: extensionRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
  });
  if (versionResult.status === 0) {
    const version = versionResult.stdout.trim().split('+')[0];
    if (/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) {
      if (availablePackageVersions.includes(version) || process.env.CI) {
        return version;
      }

      const localVersion = availablePackageVersions[0];
      if (localVersion) {
        console.warn(`Using local Aspire.AppHost.Sdk ${localVersion} for E2E fixture restore because ${version} is not available in local package sources.`);
        return localVersion;
      }

      return version;
    }
  }

  const versionsProps = fs.readFileSync(path.join(repoRoot, 'eng', 'Versions.props'), 'utf8');
  const major = getXmlProperty(versionsProps, 'MajorVersion');
  const minor = getXmlProperty(versionsProps, 'MinorVersion');
  const patch = getXmlProperty(versionsProps, 'PatchVersion');
  const prerelease = getXmlProperty(versionsProps, 'PreReleaseVersionLabel');
  return `${major}.${minor}.${patch}-${prerelease}`;
}

function getAspireCliEnvironment(extraEnv = {}) {
  return {
    ...process.env,
    ASPIRE_HOME: process.env.ASPIRE_EXTENSION_E2E_ASPIRE_HOME || isolatedAspireHome,
    ASPIRE_CLI_START_TIMEOUT: process.env.ASPIRE_EXTENSION_E2E_CLI_START_TIMEOUT || '300',
    ASPIRE_CLI_TELEMETRY_OPTOUT: 'true',
    ASPIRE_VERSION_CHECK_DISABLED: 'true',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: '1',
    DOTNET_NOLOGO: '1',
    MSBUILDTERMINALLOGGER: 'false',
    features__updateNotificationsEnabled: 'false',
    ...extraEnv,
  };
}

function writeNuGetConfigIfLocalPackageSourcesExist() {
  const packageSources = getLocalPackageSourceDirectories();
  if (packageSources.length === 0) {
    return;
  }

  const sourceEntries = packageSources
    .map((source, index) => `    <add key="e2e-source-${index}" value="${escapeXml(source)}" />`)
    .join('\n');
  const fallbackSourceEntries = getApprovedFallbackPackageSources()
    .map(source => `    <add key="${escapeXml(source.key)}" value="${escapeXml(source.value)}" />`)
    .join('\n');
  fs.writeFileSync(workspaceNuGetConfigPath, `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
${sourceEntries}
${fallbackSourceEntries}
  </packageSources>
</configuration>
`);
}

function getApprovedFallbackPackageSources() {
  return [
    { key: 'dotnet-public', value: 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json' },
    { key: 'dotnet-eng', value: 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json' },
    { key: 'dotnet9', value: 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json' },
    { key: 'dotnet10', value: 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json' },
    { key: 'dotnet-libraries', value: 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-libraries/nuget/v3/index.json' },
  ];
}

function getAvailablePackageVersions(packageId) {
  const versions = [];
  for (const sourceDirectory of getLocalPackageSourceDirectories()) {
    for (const packagePath of getFilesRecursive(sourceDirectory)) {
      const packageName = path.basename(packagePath);
      const escapedPackageId = packageId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const match = packageName.match(new RegExp(`^${escapedPackageId}\\.(.+)\\.nupkg$`));
      if (match) {
        versions.push(match[1]);
      }
    }
  }

  return Array.from(new Set(versions)).sort(comparePackageVersionsDescending);
}

function getAvailableAppHostSdkVersions() {
  const appHostVersions = getAvailablePackageVersions('Aspire.AppHost.Sdk');
  const redisVersions = new Set(getAvailablePackageVersions('Aspire.Hosting.Redis'));
  const versionsWithRedis = appHostVersions.filter(version => redisVersions.has(version));
  return versionsWithRedis.length > 0 ? versionsWithRedis : appHostVersions;
}

function getLocalPackageSourceDirectories() {
  const candidateRoots = [
    path.join(repoRoot, 'artifacts', 'nugets'),
    path.join(repoRoot, 'artifacts', 'nugets-rid'),
    path.join(repoRoot, 'artifacts', 'packages'),
    path.join(repoRoot, 'artifacts', 'nugets', 'Debug', 'Shipping'),
    path.join(repoRoot, 'artifacts', 'nugets', 'Release', 'Shipping'),
    path.join(repoRoot, 'artifacts', 'packages', 'Debug', 'Shipping'),
    path.join(repoRoot, 'artifacts', 'packages', 'Release', 'Shipping'),
    path.join(repoRoot, 'artifacts', 'packages', 'local'),
  ];

  const aspireHivesRoot = path.join(os.homedir(), '.aspire', 'hives');
  if (fs.existsSync(aspireHivesRoot)) {
    for (const hive of fs.readdirSync(aspireHivesRoot, { withFileTypes: true })) {
      if (hive.isDirectory()) {
        candidateRoots.push(path.join(aspireHivesRoot, hive.name, 'packages'));
      }
    }
  }

  const packageDirectories = [];
  for (const root of candidateRoots) {
    if (!fs.existsSync(root)) {
      continue;
    }

    packageDirectories.push(...getDirectoriesContainingPackages(root));
  }

  return Array.from(new Set(packageDirectories));
}

function getDirectoriesContainingPackages(directory) {
  const entries = fs.readdirSync(directory, { withFileTypes: true });
  const directories = entries
    .filter(entry => entry.isDirectory())
    .flatMap(entry => getDirectoriesContainingPackages(path.join(directory, entry.name)));

  if (entries.some(entry => entry.isFile() && entry.name.endsWith('.nupkg'))) {
    directories.push(directory);
  }

  return directories;
}

function comparePackageVersionsDescending(left, right) {
  return right.localeCompare(left, undefined, { numeric: true, sensitivity: 'base' });
}

function escapeXml(value) {
  return value
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function stopWorkspaceAppHost() {
  if (!cliPathForCleanup || !fs.existsSync(primaryAppHostProject)) {
    return;
  }

  const result = spawnSync(cliPathForCleanup, ['stop', '--non-interactive', '--apphost', primaryAppHostProject], {
    cwd: workspaceRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: 60000,
  });

  if (result.error) {
    console.warn(`Failed to stop Aspire E2E AppHost during cleanup: ${result.error.message}`);
    return;
  }

  if (result.status !== 0 && !/not running|No running AppHost|No AppHost/i.test(`${result.stdout}\n${result.stderr}`)) {
    console.warn(`Aspire E2E AppHost cleanup exited with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
  }
}

function getXmlProperty(xml, name) {
  const match = xml.match(new RegExp(`<${name}>([^<]+)</${name}>`));
  if (!match) {
    throw new Error(`Unable to find ${name} in eng/Versions.props.`);
  }

  return match[1];
}

function run(command, args, extraEnv = {}, options = {}) {
  const useShell = shouldUseShellForCommand(command);
  const spawnOptions = {
    cwd: extensionRoot,
    env: { ...process.env, ...extraEnv },
    stdio: 'inherit',
    timeout: options.timeout,
  };
  const result = useShell
    ? spawnSync([command, ...args].map(quoteWindowsShellArgument).join(' '), [], {
      ...spawnOptions,
      shell: true,
    })
    : spawnSync(command, args, {
    ...spawnOptions,
    shell: false,
  });

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} exited with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}. Diagnostics are under ${path.relative(extensionRoot, resultsDir)} and ${path.relative(extensionRoot, storageDiagnosticsDir)}.`);
  }
}

function quoteWindowsShellArgument(value) {
  if (!/[()\s!%&^<>"|]/.test(value)) {
    return value;
  }

  return `"${value.replace(/(["^&<>|])/g, '^$1').replace(/%/g, '%%')}"`;
}

function runWithRetry(command, args, extraEnv = {}, options) {
  let lastError;
  for (let attempt = 1; attempt <= options.attempts; attempt++) {
    try {
      run(command, args, extraEnv, options);
      return;
    }
    catch (error) {
      lastError = error;
      if (attempt === options.attempts) {
        break;
      }

      console.warn(`${command} ${args.join(' ')} failed on attempt ${attempt}/${options.attempts}: ${error instanceof Error ? error.message : String(error)}`);
      options.beforeRetry?.();
      sleepSynchronously(options.retryDelayMs);
    }
  }

  throw lastError;
}

function assertWorkspaceRootSafeForDeletion() {
  const resolvedWorkspaceRoot = resolveExistingPathForSafety(workspaceRoot);
  const resolvedShortRunRoot = fs.realpathSync.native(shortRunRoot);
  const dangerousRoots = [
    repoRoot,
    extensionRoot,
    os.homedir(),
    path.parse(resolvedWorkspaceRoot).root,
  ].map(resolveExistingPathForSafety);

  if (dangerousRoots.some(dangerousRoot => isSamePath(resolvedWorkspaceRoot, dangerousRoot))) {
    throw new Error(`Refusing to delete dangerous E2E workspace root: ${workspaceRoot}`);
  }

  if (isPathInside(resolvedWorkspaceRoot, resolvedShortRunRoot)) {
    return;
  }

  if (process.env.ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP !== 'true') {
    throw new Error(`ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT must be under the runner temp root unless ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP=true is set. Refusing to delete ${workspaceRoot}.`);
  }

  if (fs.existsSync(workspaceRoot) && !fs.existsSync(workspaceMarkerFile)) {
    throw new Error(`Refusing to delete external E2E workspace root without marker file ${workspaceMarkerFile}.`);
  }
}

function resolveExistingPathForSafety(value) {
  return fs.existsSync(value)
    ? fs.realpathSync.native(value)
    : path.resolve(value);
}

function isSamePath(left, right) {
  return getPathComparisonKey(path.resolve(left)) === getPathComparisonKey(path.resolve(right));
}

function isPathInside(candidate, parent) {
  const relative = path.relative(parent, candidate);
  return relative === '' || (!!relative && !relative.startsWith('..') && !path.isAbsolute(relative));
}

function getPathComparisonKey(value) {
  return process.platform === 'win32' ? value.toLowerCase() : value;
}

function terminateProcessTree(pid, signal) {
  if (!pid) {
    return;
  }

  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore', timeout: 15000 });
    return;
  }

  try {
    process.kill(-pid, signal);
  }
  catch {
    try {
      process.kill(pid, signal);
    }
    catch {
      // Best-effort cleanup after a timeout; the process may have already exited.
    }
  }
}

function cleanPartialExtesterDownloads() {
  for (const file of getFilesRecursive(storageDir)) {
    if (file.endsWith('.zip') || file.endsWith('.tar.gz') || file.endsWith('.tgz') || file.endsWith('.gz')) {
      fs.rmSync(file, { force: true });
    }
  }
}

function sleepSynchronously(milliseconds) {
  const buffer = new SharedArrayBuffer(4);
  Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}

function copyStorageDiagnostics() {
  removePath(storageDiagnosticsDir, { recursive: true, force: true });
  copyIfExists(isolatedAspireHome, path.join(storageDiagnosticsDir, 'aspire-home'), skipAspireLeaseFiles);
  copyIfExists(path.join(storageDir, 'screenshots'), path.join(storageDiagnosticsDir, 'screenshots'));
  copyIfExists(path.join(storageDir, 'settings', 'CrashpadMetrics-active.pma'), path.join(storageDiagnosticsDir, 'settings', 'CrashpadMetrics-active.pma'));
  copyIfExists(path.join(storageDir, 'settings', 'logs'), path.join(storageDiagnosticsDir, 'settings', 'logs'));
  copyIfExists(path.join(storageDir, 'settings', 'User', 'settings.json'), path.join(storageDiagnosticsDir, 'settings', 'User', 'settings.json'));
  redactTextFilesForArtifacts(storageDiagnosticsDir);
}

function copyWorkspaceDiagnostics() {
  removePath(workspaceDiagnosticsDir, { recursive: true, force: true });
  copyIfExists(path.join(workspaceRoot, '.aspire'), path.join(workspaceDiagnosticsDir, '.aspire'));
  copyIfExists(path.join(workspaceRoot, '.vscode', 'settings.json'), path.join(workspaceDiagnosticsDir, '.vscode', 'settings.json'));
  copyWorkspaceProjectSources();
  redactTextFilesForArtifacts(workspaceDiagnosticsDir);
}

function copyIfExists(sourcePath, destinationPath, filter) {
  if (!fs.existsSync(sourcePath)) {
    return;
  }

  fs.mkdirSync(path.dirname(destinationPath), { recursive: true });
  fs.cpSync(sourcePath, destinationPath, { recursive: true, force: true, filter });
}

function skipAspireLeaseFiles(sourcePath) {
  // Aspire CLI lease files can remain locked briefly on Windows after the test
  // process exits. They are not useful diagnostics, and failing to copy them can
  // mask the actual E2E failure or prevent artifact upload.
  return !sourcePath.split(/[\\/]/).includes('.leases') && !sourcePath.endsWith('.lease');
}

function copyWorkspaceProjectSources() {
  if (!fs.existsSync(workspaceRoot)) {
    return;
  }

  for (const entry of fs.readdirSync(workspaceRoot, { withFileTypes: true })) {
    if (!entry.isDirectory() || !entry.name.startsWith('AspireE2E.')) {
      continue;
    }

    const sourceDirectory = path.join(workspaceRoot, entry.name);
    const destinationDirectory = path.join(workspaceDiagnosticsDir, entry.name);
    copyIfExists(path.join(sourceDirectory, 'AppHost.cs'), path.join(destinationDirectory, 'AppHost.cs'));
    copyIfExists(path.join(sourceDirectory, 'Program.cs'), path.join(destinationDirectory, 'Program.cs'));
    copyIfExists(path.join(sourceDirectory, `${entry.name}.csproj`), path.join(destinationDirectory, `${entry.name}.csproj`));
  }
}

function redactTextFilesForArtifacts(directory) {
  if (!fs.existsSync(directory)) {
    return;
  }

  for (const file of getFilesRecursive(directory)) {
    if (!isTextArtifact(file)) {
      continue;
    }

    let contents;
    try {
      contents = fs.readFileSync(file, 'utf8');
    }
    catch {
      continue;
    }

    const redacted = redactSensitiveArtifactText(contents);
    if (redacted !== contents) {
      fs.writeFileSync(file, redacted);
    }
  }
}

function isTextArtifact(file) {
  return /\.(log|txt|json|jsonl|xml|config|cs|ts|js|md)$/i.test(file) || path.basename(file).toLowerCase() === 'settings';
}

function redactSensitiveArtifactText(value) {
  return value
    .replace(/\/login\?t=[^"'\s<>\\)]+/gi, '/login?t=<redacted>')
    .replace(/([?&]t=)[^"'\s<>\\)&]+/gi, '$1<redacted>')
    .replace(/(Setting up RPC server with token: )[^\r\n]+/gi, '$1<redacted>')
    .replace(/(token["']?\s*[:=]\s*["']?)[A-Za-z0-9+/=._-]{16,}/gi, '$1<redacted>');
}

function printSuccessDiagnosticsSummary() {
  const results = readMochaResults();
  if (!results) {
    console.log(`Aspire extension E2E shard '${shardName}' completed. Mocha JSON was not found at ${path.relative(extensionRoot, path.join(resultsDir, 'mocha.json'))}.`);
    return;
  }

  const stats = results.stats ?? {};
  console.log(`Aspire extension E2E shard '${shardName}' passed: ${stats.passes ?? results.passes?.length ?? 0}/${stats.tests ?? results.tests?.length ?? 0} tests in ${stats.duration ?? 'unknown'}ms.`);
}

function printFailureDiagnosticsSummary() {
  console.error(`Aspire extension E2E shard '${shardName}' failed.`);
  console.error(`Results directory: ${path.relative(extensionRoot, resultsDir)}`);
  console.error(`VS Code diagnostics directory: ${path.relative(extensionRoot, storageDiagnosticsDir)}`);
  console.error(`Workspace diagnostics directory: ${path.relative(extensionRoot, workspaceDiagnosticsDir)}`);

  const results = readMochaResults();
  if (results?.failures?.length > 0) {
    console.error('Failed E2E tests:');
    for (const failure of results.failures) {
      console.error(`  - ${failure.fullTitle ?? failure.title}`);
      const message = failure.err?.message;
      if (message) {
        console.error(indentBlock(message, '      '));
      }
    }
  }
  else {
    console.error('Mocha failure details were not available in mocha.json.');
  }

  const state = readJsonIfExists(stateFile);
  if (state?.state) {
    console.error('Last exported extension state:');
    console.error(indentBlock(JSON.stringify({
      viewMode: state.state.viewMode,
      isRepositoryLoading: state.state.isRepositoryLoading,
      isWorkspaceAppHostDiscoveryComplete: state.state.isWorkspaceAppHostDiscoveryComplete,
      hasError: state.state.hasError,
      errorMessage: state.state.errorMessage,
      workspaceAppHostPath: state.state.workspaceAppHostPath,
      workspaceAppHostCandidatePaths: state.state.workspaceAppHostCandidatePaths,
      workspaceResources: state.state.workspaceResources?.map(resource => `${resource.name}:${resource.state}`),
      appHosts: state.state.appHosts?.map(appHost => appHost.appHostPath),
      launchingPaths: state.state.launchingPaths,
      debugSessions: state.state.debugSessions?.map(redactDebugSessionForDiagnostics),
    }, null, 2), '  '));
  }

  const extensionLogPath = findLatestExtensionLogPath();
  if (extensionLogPath) {
    console.error(`Last Aspire extension log lines (${path.relative(extensionRoot, extensionLogPath)}):`);
    console.error(indentBlock(redactSensitiveArtifactText(tailLines(fs.readFileSync(extensionLogPath, 'utf8'), 120)), '  '));
  }
}

function readMochaResults() {
  return readJsonIfExists(path.join(resultsDir, 'mocha.json'));
}

function readJsonIfExists(filePath) {
  if (!fs.existsSync(filePath)) {
    return undefined;
  }

  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  }
  catch (error) {
    console.warn(`Failed to parse ${filePath}: ${error instanceof Error ? error.message : String(error)}`);
    return undefined;
  }
}

function findLatestExtensionLogPath() {
  const logsRoot = path.join(storageDiagnosticsDir, 'settings', 'logs');
  if (!fs.existsSync(logsRoot)) {
    return undefined;
  }

  return getFilesRecursive(logsRoot)
    .filter(file => path.basename(file) === 'Aspire Extension.log')
    .sort((left, right) => fs.statSync(right).mtimeMs - fs.statSync(left).mtimeMs)[0];
}

function tailLines(value, lineCount) {
  const lines = value.split(/\r?\n/);
  return lines.slice(Math.max(0, lines.length - lineCount)).join('\n');
}

function indentBlock(value, prefix) {
  return String(value).split(/\r?\n/).map(line => `${prefix}${line}`).join('\n');
}

function cleanupTemporaryRunRoot() {
  if (process.env.ASPIRE_EXTENSION_E2E_KEEP_STORAGE === 'true') {
    console.log(`Keeping Aspire VS Code E2E temporary root: ${shortRunRoot}`);
    return;
  }

  removePath(shortRunRoot, { recursive: true, force: true, warnOnWindowsLock: true });
}

function sanitizePathSegment(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '-');
}

function removePath(targetPath, options = {}) {
  const { warnOnWindowsLock, ...rmOptions } = options;
  try {
    fs.rmSync(targetPath, {
      maxRetries: process.platform === 'win32' ? 20 : 0,
      retryDelay: 250,
      ...rmOptions,
    });
  }
  catch (error) {
    if (warnOnWindowsLock && process.platform === 'win32' && isRetryableWindowsFileLock(error)) {
      console.warn(`Warning: unable to remove locked E2E path '${targetPath}': ${error.message}`);
      return;
    }

    throw error;
  }
}

function isRetryableWindowsFileLock(error) {
  return error && typeof error === 'object' && ['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code);
}
