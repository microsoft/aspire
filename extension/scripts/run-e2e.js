#!/usr/bin/env node
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, spawnSync } = require('child_process');
const {
  ensureDownloadCache,
  projectDownloadCache,
  removePathWithoutFollowingLinks,
  resolveDownloadCacheRoot,
} = require('./e2e-download-cache');
const {
  markErrorNonRetryable,
  runWithRetries,
  terminateOrphanedDescendants,
} = require('./e2e-download-retry');
const { shouldAllowAdvisoryTestFailure } = require('./e2e-process-failure.cjs');
const { runWithProcessTreeTimeout } = require('./e2e-process-runner.cjs');

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
// Everything below that can reject the environment runs before the per-run root exists, and
// nothing after `mkdtempSync` does more than join strings. Module scope is outside the cleanup
// `finally` that `main()` installs, so a throw once the root exists leaves an `aev-*` directory
// behind with nothing alive to remove it; work that can fail belongs in `main()` instead, which is
// why `prepareRunDirectories` is a function rather than a module-scope block.
const testSpec = process.env.ASPIRE_EXTENSION_E2E_SPEC || 'out/test-e2e/**/*.e2e.test.js';
const matchedTestSpecs = verifyExtesterFeedOnly ? [] : findSpecMatches(testSpec);
// Java specs need the Java language server and the Java debug adapter (neither of which Aspire
// ships) and a Java workspace in place of the scaffolded C# one, so they cannot share a run with
// the other specs. Which specs are running is the whole requirement, so it is read off them rather
// than from an opt-in variable: as an opt-in, both Java shards ran in CI with nothing setting it,
// skipped every test, and reported green.
//
// ASPIRE_EXTENSION_E2E_ENABLE_JAVA remains as an explicit override for running a Java spec through
// a glob, or for forcing the Java workspace off while debugging the runner.
const matchedJavaTestSpecs = matchedTestSpecs.filter(isJavaSpecPath);
const javaStarterTestSpecs = matchedJavaTestSpecs.filter(isJavaStarterSpecPath);
const javaPlaygroundTestSpecs = matchedJavaTestSpecs.filter(specPath => !isJavaStarterSpecPath(specPath));
const matchedNonJavaTestSpecs = matchedTestSpecs.filter(specPath => !isJavaSpecPath(specPath));
if (matchedJavaTestSpecs.length > 0 && matchedNonJavaTestSpecs.length > 0) {
  const javaMatches = matchedJavaTestSpecs.map(specPath => toPosixPath(path.relative(extensionRoot, specPath))).sort();
  const nonJavaMatches = matchedNonJavaTestSpecs.map(specPath => toPosixPath(path.relative(extensionRoot, specPath))).sort();
  throw new Error(`Java E2E spec selection mixes Java and non-Java workspace fixtures. Java matches: ${javaMatches.join(', ')}. Non-Java matches: ${nonJavaMatches.join(', ')}. Split these specs into separate runs.`);
}
const enableJavaE2E = process.env.ASPIRE_EXTENSION_E2E_ENABLE_JAVA
  ? process.env.ASPIRE_EXTENSION_E2E_ENABLE_JAVA === 'true'
  : matchedJavaTestSpecs.length > 0 && matchedNonJavaTestSpecs.length === 0;
if (javaStarterTestSpecs.length > 0 && javaPlaygroundTestSpecs.length > 0) {
  const starterMatches = javaStarterTestSpecs.map(specPath => toPosixPath(path.relative(extensionRoot, specPath))).sort();
  const playgroundMatches = javaPlaygroundTestSpecs.map(specPath => toPosixPath(path.relative(extensionRoot, specPath))).sort();
  throw new Error(`Java E2E spec selection mixes workspace fixtures. Starter matches: ${starterMatches.join(', ')}. Playground matches: ${playgroundMatches.join(', ')}. Split these specs into separate runs.`);
}
const useJavaStarterWorkspace = enableJavaE2E && javaStarterTestSpecs.length > 0;
// redhat.java supplies the language server, which is what produces workspace diagnostics and the
// classpath the debug adapter launches against. vscjava.vscode-java-debug supplies the `java` debug
// adapter the Aspire debugger delegates to, and vscjava.vscode-java-dependency is a hard activation
// dependency of it. capabilities.ts only advertises the `java` capability - which is what makes the
// CLI hand the AppHost launch back to the extension - when the first two are both installed.
const REQUIRED_JAVA_EXTENSION_IDS = ['redhat.java', 'vscjava.vscode-java-debug', 'vscjava.vscode-java-dependency'];
const extesterVersion = extensionPackageJson.devDependencies?.['vscode-extension-tester'];
if (!extesterVersion) {
  throw new Error('vscode-extension-tester must be pinned in extension/package.json devDependencies.');
}
// The feed preflight must not touch the shared cache: it runs before any download and only
// verifies package availability, so resolving the cache root there would be wasted Git discovery.
const downloadCacheRoot = verifyExtesterFeedOnly ? '' : resolveDownloadCacheRoot(repoRoot);
// Keep this below VS Code 1.131.0 while ExTester is pinned to 8.23.0. VS Code 1.130.0 contains
// Contents/MacOS/Code plus an Electron -> Code compatibility symlink, but VS Code 1.131.0 removes
// that legacy path and ExTester 8.23.0 only launches it. ExTester 8.24.0 adds the fallback, but its
// tarball is not anonymously available from dotnet-public-npm yet.
const vscodeVersion = resolveCachedVsCodeVersion(process.env.ASPIRE_EXTENSION_E2E_VSCODE_VERSION || '1.130.0');
assertVsCodeVersionCompatibleWithExtester(vscodeVersion, extesterVersion);
if (!verifyExtesterFeedOnly) {
  fs.mkdirSync(requestedTempRoot, { recursive: true });
}
const tempRoot = verifyExtesterFeedOnly ? '' : fs.realpathSync.native(requestedTempRoot);
const shortRunRoot = verifyExtesterFeedOnly ? '' : fs.mkdtempSync(path.join(tempRoot, 'aev-'));
const e2eNuGetPackages = path.join(downloadCacheRoot, 'nuget-packages', shardName);
const isolatedAspireHome = path.join(shortRunRoot, 'aspire-home');
const storageDir = path.join(shortRunRoot, 'storage');
const extensionsDir = path.join(shortRunRoot, 'extensions');
// A single-file Java AppHost is restored and launched by the CLI's AppHost server. In a dev build
// that server resolves Aspire packages through the repository's own NuGet configuration, so a
// workspace under the OS temp directory fails with "No code generator found for language: Java"
// (and, with an isolated ASPIRE_HOME, "No Aspire AppHost server is available"). The Java workspace
// therefore lives inside the repository.
//
// It deliberately is not one of the gitignored scratch directories: `aspire ls` skips ignored
// paths, so an AppHost under `.test-workspaces/` is discovered as zero candidates. It also sits
// under `extension/` rather than the repository root, because an AppHost at the root fails code
// generation with "No code generator found for language: Java". The runner deletes this directory
// at the start and end of every run.
const javaScratchWorkspaceRoot = path.join(extensionRoot, 'java-e2e-workspace');
const workspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT
  ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT)
  : enableJavaE2E
    ? javaScratchWorkspaceRoot
    : path.join(shortRunRoot, 'workspace');
const workspaceMarkerFile = path.join(workspaceRoot, '.aspire-extension-e2e-workspace');
const storageDiagnosticsDir = path.join(diagnosticsStorageRoot, shardName, runId);
const workspaceDiagnosticsDir = path.join(extensionRoot, '.test-workspaces', shardName, runId);
const recordingsDir = path.join(extensionRoot, '.test-recordings', shardName);
const defaultVsixPath = path.join(artifactsDir, 'aspire-extension-e2e.vsix');
const stateFile = path.join(resultsDir, 'extension-state.json');
const controlFile = path.join(resultsDir, 'extension-control.json');
const extesterNodeModules = path.join(extensionRoot, 'node_modules');
const extesterModule = path.join(extesterNodeModules, 'vscode-extension-tester');
const extesterCli = path.join(extesterModule, 'out', 'cli.js');
// ExTester unpacks VS Code into `<storage>/vscode-temp-<random>` and removes it in a `finally`
// that a killed process never reaches. See node_modules/vscode-extension-tester/out/util/codeUtil.js.
const EXTESTER_UNPACK_DIRECTORY_PREFIX = 'vscode-temp-';
// `/bin/sh` leaves a word alone only when every character in it is inert: no whitespace to split
// on, no `$` or backtick to expand, no `;`, `&`, `|`, `(`, `)`, `<`, `>` or newline to end the
// command, no `*`, `?` or `[` to glob, and no quote or backslash to change the parse. This is an
// allowlist rather than a metacharacter blocklist so a character whose meaning depends on position
// (`~`, `#`, `!`) forces a projection instead of having to be reasoned about.
const POSIX_SHELL_INERT_PATH_PATTERN = /^[A-Za-z0-9._/+,=:@%-]+$/;
// Windows needs the same allowlist over a different alphabet, because `cmd.exe /d /s /c` strips
// the quotes Node wraps the command in and parses whatever is left. `\` and `:` are ordinary path
// characters rather than escapes there, and `~` has to stay legal: the 8.3 short names Windows
// hands out (`C:\Users\RUNNER~1\AppData\Local\Temp` on hosted runners) would otherwise be unable
// to host the projection that stands in for a rejected path. Excluded are `%` and `!` (variable
// and delayed expansion), `^` (escape), `&`, `|`, `<`, `>`, `(`, `)` and quotes (command syntax),
// and space, `,`, `;` and `=`, every one of which terminates the command token.
const WINDOWS_COMMAND_INERT_PATH_PATTERN = /^[A-Za-z0-9._\\/:+@~-]+$/;
const isWindows = process.platform === 'win32';
const COMMAND_INERT_PATH_PATTERN = isWindows ? WINDOWS_COMMAND_INERT_PATH_PATTERN : POSIX_SHELL_INERT_PATH_PATTERN;
const COMMAND_INTERPRETER_NAME = isWindows ? 'cmd.exe' : '/bin/sh';
const COMMAND_INERT_PATH_ALPHABET = isWindows ? '._-+@~:\\/' : '._-+,=:@%/';
const primaryAppHostProject = path.join(workspaceRoot, 'AspireE2E.AppHost', 'AspireE2E.AppHost.csproj');
const runRootNuGetConfigPath = path.join(shortRunRoot, 'NuGet.config');
const workspaceNuGetConfigPath = path.join(workspaceRoot, 'NuGet.config');
const enableAzureFunctionsE2E = process.env.ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS === 'true';
const advisoryIssue = process.env.ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE || '';
let cliPathForCleanup;
const csharpFileHeader = `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

`;

/**
 * Clears the previous run's results and creates the directories this run writes into.
 *
 * This is deliberately not module scope even though everything it needs is: it removes and creates
 * directories, and every one of those calls can fail on a stale Windows file lock or a read-only
 * mount. Module scope is outside the cleanup `finally` that `main()` installs, so a throw there
 * would strand the `aev-*` root that `mkdtempSync` had already created.
 */
function prepareRunDirectories() {
  removePath(resultsDir, { recursive: true, force: true });
  removePath(recordingsDir, { recursive: true, force: true });
  for (const directory of [artifactsDir, resultsDir, diagnosticsStorageRoot, isolatedAspireHome, storageDir, extensionsDir]) {
    fs.mkdirSync(directory, { recursive: true });
  }
}

function prepareNuGetPackageCache() {
  fs.mkdirSync(e2eNuGetPackages, { recursive: true });
  for (const entry of fs.readdirSync(e2eNuGetPackages, { withFileTypes: true })) {
    // E2E builds reuse package versions, so only repository-built Aspire packages can be stale.
    // Keep third-party packages warm across runs, and isolate each shard so concurrent runs do not
    // remove packages another shard is restoring.
    if (/^aspire(?:\.|$)/i.test(entry.name)) {
      removePathWithoutFollowingLinks(path.join(e2eNuGetPackages, entry.name), { recursive: true, force: true });
    }
  }
}

function getRunTestsTimeoutMs() {
  const configured = Number(process.env.ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS || 2400000);
  if (!Number.isFinite(configured) || configured <= 0) {
    throw new Error(`ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS must be a positive number. Got '${process.env.ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS}'.`);
  }

  return configured;
}

/**
 * Returns a path ExTester can be given for its storage folder that the platform's command
 * interpreter will not reinterpret.
 *
 * ExTester builds shell command strings out of this path and interpolates it unquoted into
 * each of them:
 *
 * - `exec(`unzip -qo ${input}`, { cwd: target })` unpacks `.zip` archives on macOS and Linux --
 *   see `node_modules/vscode-extension-tester/out/util/unpack.js`.
 * - `exec(`${this.getChromeDriverBinaryPath(version)} -v`)` reads the version of an already
 *   downloaded ChromeDriver on every platform, Windows included -- see
 *   `node_modules/vscode-extension-tester/out/util/driverUtil.js`.
 *
 * `exec` hands its string to `/bin/sh -c` or `cmd.exe /d /s /c`, so every construct in the path is
 * live: a space splits it into two arguments, `repo(1)` is a syntax error under `sh`, and
 * `repo&whoami` runs a second command under `cmd`. That path is now the download cache, which
 * lives inside the repository, so it is wherever the developer cloned rather than something this
 * runner chooses.
 *
 * The ChromeDriver check is why this has to happen on Windows even though Windows unpacks
 * in-process with `unzipper`. `downloadChromeDriver` runs it whenever the binary already exists,
 * which is exactly the warm hit this cache is built to produce, and it swallows the failure and
 * downloads again. A checkout under `C:\src\my repo` would therefore never get a warm ChromeDriver
 * and would never say why.
 *
 * Standing a link from the run's own temporary root in front of the cache keeps the command a
 * single inert word. Windows gets a junction rather than a symlink because junctions need neither
 * elevation nor Developer Mode.
 */
function projectCommandSafeStagingDirectory(stagingDirectory) {
  if (COMMAND_INERT_PATH_PATTERN.test(stagingDirectory)) {
    return stagingDirectory;
  }

  const linkPath = path.join(shortRunRoot, 'cache-staging');
  if (!COMMAND_INERT_PATH_PATTERN.test(linkPath)) {
    throw new Error(`The download cache path '${stagingDirectory}' contains characters '${COMMAND_INTERPRETER_NAME}' would reinterpret, which ExTester cannot be pointed at, and the per-run temporary root '${shortRunRoot}' cannot stand in for it because it has the same problem. Point ASPIRE_EXTENSION_E2E_TEMP_ROOT or ASPIRE_EXTENSION_E2E_CACHE_ROOT at a path built only from letters, digits and '${COMMAND_INERT_PATH_ALPHABET}'.`);
  }

  removePathWithoutFollowingLinks(linkPath);
  fs.symlinkSync(stagingDirectory, linkPath, isWindows ? 'junction' : 'dir');
  return linkPath;
}

function getSetupDownloadRetryOptions(stagingDirectory, downloadDirectory) {
  return {
    attempts: getPositiveIntegerEnvironmentVariable('ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_RETRY_ATTEMPTS', 5),
    retryDelayMs: getPositiveIntegerEnvironmentVariable('ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_RETRY_DELAY_MS', 15000),
    beforeRetry: () => cleanPartialExtesterDownloads(stagingDirectory),
    timeout: getPositiveIntegerEnvironmentVariable('ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_TIMEOUT_MS', 240000),
    // Orphans are matched by the path ExTester was actually given, which is the projection rather
    // than the candidate whenever the cache path is not inert to the command interpreter.
    terminateOrphansUnder: downloadDirectory,
  };
}

function getPositiveIntegerEnvironmentVariable(name, defaultValue) {
  const configured = Number(process.env[name] || defaultValue);
  if (!Number.isInteger(configured) || configured <= 0) {
    throw new Error(`${name} must be a positive integer. Got '${process.env[name]}'.`);
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
  console.log(`  download cache: ${downloadCacheRoot}`);
  console.log(`  current CLI regressions: ${process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true' ? 'skipped' : 'included'}`);
  console.log(`  Azure Functions: ${enableAzureFunctionsE2E ? 'enabled' : 'disabled'}`);
  console.log(`  Java: ${enableJavaE2E ? 'enabled' : 'disabled'}`);
  console.log(`  results: ${path.relative(extensionRoot, resultsDir)}`);
  console.log(`  storage diagnostics: ${path.relative(extensionRoot, storageDiagnosticsDir)}`);
  console.log(`  workspace diagnostics: ${path.relative(extensionRoot, workspaceDiagnosticsDir)}`);
}

/**
 * Reports whether a compiled spec file belongs to the Java suites.
 *
 * Naming is the contract: every Java spec is `java*.e2e.test.js`, which is also what the workflow's
 * `java-*` shards point at. Matching on the file name rather than a hard-coded list means a new
 * Java spec is picked up by adding the shard, with nothing else to remember.
 */
function isJavaSpecPath(specPath) {
  return path.basename(specPath).toLowerCase().startsWith('java');
}

function isJavaStarterSpecPath(specPath) {
  return path.basename(specPath).toLowerCase().startsWith('javastarterprojectmodel.');
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
  return value.replace(/^\\\\\?\\/, '').split(path.sep).join('/');
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
  let cleanupFailed = false;
  try {
    if (verifyExtesterFeedOnly) {
      verifyExtesterFeed();
      return;
    }

    assertSpecMatches(testSpec);
    prepareRunDirectories();
    prepareNuGetPackageCache();
    logE2eConfiguration();

    const bundledCliPath = resolveCliPath();
    // A dev-build CLI resolves its AppHost server and code generators relative to its own location,
    // so a copy under the temp root cannot run a single-file Java AppHost - it fails with "No code
    // generator found for language: Java". Java runs therefore use the CLI in place. Nothing in the
    // Java spec writes to the CLI directory, and cleanup only ever invokes `aspire stop`.
    const cliPath = enableJavaE2E ? bundledCliPath : isolateCliPath(bundledCliPath);
    cliPathForCleanup = cliPath;
    validateCliPath(cliPath);
    const appHostSdkVersion = resolveAppHostSdkVersion(cliPath);
    prepareWorkspaceFixture(cliPath, appHostSdkVersion);
    prepareJavaWorkspace(bundledCliPath, appHostSdkVersion);
    restoreWorkspaceFixture();
    const vsixPath = process.env.ASPIRE_EXTENSION_E2E_VSIX
      ? path.resolve(process.env.ASPIRE_EXTENSION_E2E_VSIX)
      : packageVsix();

    if (!fs.existsSync(vsixPath)) {
      throw new Error(`VSIX not found at ${vsixPath}`);
    }
    validateVsix(vsixPath);
    const azureFunctionsVsixPaths = resolveAzureFunctionsVsixPaths();
    if (enableAzureFunctionsE2E) {
      validateAzureFunctionsCoreTools();
    }

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
      // The state and control files live at a stable per-shard path, so both sides stamp this to
      // ignore an extension host left behind by an earlier run that is still polling them.
      ASPIRE_EXTENSION_E2E_RUN_ID: runId,
      ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE: 'true',
      NUGET_PACKAGES: e2eNuGetPackages,
      ASPIRE_EXTENSION_E2E_NUGET_PACKAGES: e2eNuGetPackages,
      ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS: process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS === 'true' ? 'true' : 'false',
      ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST: primaryAppHostProject,
      ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION: appHostSdkVersion,
      ASPIRE_EXTENSION_E2E_EXTESTER_MODULE: extesterModule,
      ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS: enableAzureFunctionsE2E ? 'true' : 'false',
      VSCODE_NLS_CONFIG: JSON.stringify({ locale: 'en', availableLanguages: {} }),
      LANG: 'C.UTF-8',
      LC_ALL: 'C.UTF-8',
      NODE_PATH: [extesterNodeModules, process.env.NODE_PATH].filter(Boolean).join(path.delimiter),
      // ExTester's loadCodeVersion prefers CODE_VERSION over the --code_version argument, so an
      // ambient value would make it download a version the cache key does not describe and leave
      // a later run reusing the wrong install offline. Pinning it here makes the argument and the
      // key authoritative. See node_modules/vscode-extension-tester/out/extester.js.
      CODE_VERSION: vscodeVersion,
      // The cache discovers stable install layouts (`VSCode-linux-x64`, `Visual Studio Code.app`)
      // and the stream is not part of its key, so an ambient CODE_TYPE=insider would download an
      // Insiders build that artifact discovery then cannot find. Nothing here asks for Insiders.
      CODE_TYPE: 'stable',
    });
    if (process.env.ASPIRE_EXTENSION_E2E_UNSET_CLI_START_TIMEOUT === 'true') {
      extestEnv.ASPIRE_CLI_START_TIMEOUT = undefined;
    }

    const downloadCache = ensureDownloadCache({
      cacheRoot: downloadCacheRoot,
      vscodeVersion,
      extesterVersion,
      platform: process.platform,
      architecture: process.arch,
      populate(stagingDirectory) {
        const downloadDirectory = projectCommandSafeStagingDirectory(stagingDirectory);
        const setupDownloadRetryOptions = getSetupDownloadRetryOptions(stagingDirectory, downloadDirectory);
        logStep('Downloading VS Code');
        runWithRetry(process.execPath, [extesterCli, 'get-vscode', '--storage', downloadDirectory, '--code_version', vscodeVersion], extestEnv, setupDownloadRetryOptions);
        logStep('Downloading ChromeDriver');
        runWithRetry(process.execPath, [extesterCli, 'get-chromedriver', '--storage', downloadDirectory, '--code_version', vscodeVersion], extestEnv, setupDownloadRetryOptions);
      },
    });
    console.log(`Extension E2E download cache ${downloadCache.cacheHit ? 'hit' : 'populated'}: ${downloadCache.cacheDirectory}`);
    projectDownloadCache(downloadCache, storageDir);

    // Installed before any VSIX because the fallback path copies unpacked extension directories in.
    // VS Code only scans the extensions directory while extensions.json is absent; once install-vsix
    // has written that file it is the authoritative list, and a directory that is not in it is
    // ignored and then removed. Copying after the first install therefore silently installs nothing.
    installJavaExtensions(extestEnv);

    logStep('Installing VSIX');
    run(process.execPath, [extesterCli, 'install-vsix', '--storage', storageDir, '--extensions_dir', extensionsDir, '--vsix_file', vsixPath], extestEnv, { timeout: 300000 });
    for (const azureFunctionsVsix of azureFunctionsVsixPaths) {
      logStep(`Installing ${azureFunctionsVsix.displayName} VSIX`);
      run(process.execPath, [extesterCli, 'install-vsix', '--storage', storageDir, '--extensions_dir', extensionsDir, '--vsix_file', azureFunctionsVsix.path], extestEnv, { timeout: 300000 });
    }
    assertJavaExtensionsRegistered();

    recording = startRecording();
    try {
      logStep('Running VS Code extension E2E tests');
      const runTestsArgs = [extesterCli, 'run-tests', testSpec, '--storage', storageDir, '--extensions_dir', extensionsDir, '--code_version', vscodeVersion, '--code_settings', path.join(extensionRoot, 'test-e2e', 'settings.json'), '--mocha_config', path.join(extensionRoot, '.mocharc.e2e.js'), '--offline'];
      await runWithProcessTreeTimeout(process.execPath, runTestsArgs, {
        diagnosticsSuffix: ` Diagnostics are under ${path.relative(extensionRoot, resultsDir)} and ${path.relative(extensionRoot, storageDiagnosticsDir)}.`,
        quoteShellArgument: quoteWindowsShellArgument,
        spawn,
        spawnOptions: {
          cwd: extensionRoot,
          env: { ...process.env, ...extestEnv },
          stdio: 'inherit',
          detached: process.platform !== 'win32',
        },
        terminateProcessTree,
        timeout: getRunTestsTimeoutMs(),
        useShell: shouldUseShellForCommand(process.execPath),
      });
    }
    catch (error) {
      testFailure = error;
    }
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
      cleanupFailed = true;
      // Node prints an AggregateError without its `errors`, so a cleanup failure would otherwise
      // reach CI as a bare "one or more steps failed" with nothing naming the step that broke.
      const cleanupFailure = new AggregateError(
        cleanupErrors,
        `One or more E2E cleanup steps failed:\n  ${cleanupErrors.map(error => error.stack ?? error.message).join('\n  ')}`);
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
    // Only completed test failures become advisory. Structured setup, spawn, signal, timeout, and
    // cleanup failures keep the shard blocking even when mocha.json recorded completed test cases.
    if (advisoryIssue && shouldAllowAdvisoryTestFailure(testFailure, readMochaResults(), cleanupFailed)) {
      console.warn(`::warning title=VS Code extension E2E test failure advisory::${shardName} has completed test failures tracked by ${advisoryIssue}. Diagnostics were uploaded for investigation.`);
      return;
    }

    throw testFailure;
  }

  printSuccessDiagnosticsSummary();
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

function resolveAzureFunctionsVsixPaths() {
  if (!enableAzureFunctionsE2E) {
    return [];
  }

  // Aspire advertises its azure-functions launch capability only when both the C# and
  // Azure Functions extensions are installed. Install C# with its required .NET runtime
  // dependency, plus the Azure Resource Groups extension that Functions activates directly.
  // All dependencies must be explicit because the E2E VS Code instance runs offline.
  return [
    {
      displayName: '.NET Install Tool',
      path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_DOTNET_RUNTIME_VSIX'),
    },
    {
      displayName: 'C#',
      path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_CSHARP_VSIX'),
    },
    {
      displayName: 'Azure Resource Groups',
      path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_RESOURCE_GROUPS_VSIX'),
    },
    {
      displayName: 'Azure Functions',
      path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_FUNCTIONS_VSIX'),
    },
  ];
}

/**
 * Copies the Java Spring Boot playground into the run's workspace.
 *
 * The playground cannot be used as the workspace root directly, because `prepareWorkspaceFixture`
 * deletes and rewrites whatever it is pointed at. Copying also keeps each run reproducible: the
 * generated SDK, the Gradle build output, and the language server's own `bin/` all start absent, so
 * a test asserting that build inputs were not copied into the output directory is measuring this
 * run rather than whatever a previous local build left behind.
 */
function copyJavaPlaygroundIntoWorkspace(bundledCliPath) {
  if (!enableJavaE2E) {
    return;
  }

  const source = path.join(repoRoot, 'playground', 'JavaSpringBoot');
  if (!fs.existsSync(source)) {
    throw new Error(`The Java E2E specs require the Java playground at ${source}.`);
  }

  assertWorkspaceRootIsNotGitIgnored();

  // `.aspire/` is generated rather than checked in, so it has to exist before the copy: it is what
  // the AppHost's `import aspire.*` statements resolve against, and the generated sources are the
  // very thing the diagnostics test measures.
  ensureJavaAppHostSdkGenerated(bundledCliPath, path.join(source, 'JavaSpringBoot.AppHost.Java'));

  logStep('Copying the Java Spring Boot playground into the E2E workspace');
  fs.cpSync(source, workspaceRoot, {
    recursive: true,
    // Anything the language server or a build produced locally would defeat the point of a clean
    // run, and `bin/` in particular is what one of the tests asserts about. `.aspire/` is kept
    // deliberately - it holds the generated SDK under test.
    filter: sourcePath => !/[\\/](?:\.gradle|build|bin|target|node_modules)(?:[\\/]|$)/.test(sourcePath),
  });

  // The scaffolded settings point at the isolated CLI copy, which the Java AppHost needs just as
  // much as the C# one. Merge rather than replace so that stays in effect.
  const settingsPath = path.join(workspaceRoot, '.vscode', 'settings.json');
  const settings = fs.existsSync(settingsPath) ? JSON.parse(fs.readFileSync(settingsPath, 'utf8')) : {};

  // A Gradle import that fetches wrapper distributions and toolchains on demand takes far longer
  // than the language server's default readiness window, and an unimported project reports every
  // Aspire import as unresolved.
  settings['java.import.gradle.wrapper.enabled'] = true;
  settings['java.configuration.updateBuildConfiguration'] = 'automatic';
  // The language server keeps its project metadata, and therefore its compiler output, in its own
  // workspace storage by default. That hides the directories the "does not copy build inputs" spec
  // asserts about: with nothing under the project's own bin/, the spec passes without ever
  // observing what the build produced. Putting the metadata back at the project root is what makes
  // that assertion able to fail.
  settings['java.import.generatesMetadataFilesAtProjectRoot'] = true;
  fs.writeFileSync(settingsPath, JSON.stringify(settings, undefined, 2));

  // The scaffolded C# fixture cannot coexist with a repository-internal workspace: it inherits the
  // repository's central package management and fails to restore with NU1507. The Java spec never
  // uses it, so remove it and unpin the pinned AppHost so discovery finds the Java AppHost.
  for (const scaffolded of ['AspireE2E.AppHost', 'AspireE2E.Worker']) {
    fs.rmSync(path.join(workspaceRoot, scaffolded), { recursive: true, force: true });
  }

  fs.rmSync(path.join(workspaceRoot, 'aspire.config.json'), { force: true });
}

function prepareJavaWorkspace(bundledCliPath, appHostSdkVersion) {
  if (!enableJavaE2E) {
    return;
  }

  if (!useJavaStarterWorkspace) {
    copyJavaPlaygroundIntoWorkspace(bundledCliPath);
    return;
  }

  assertWorkspaceRootIsNotGitIgnored();
  logStep('Generating the Java starter in the E2E workspace');

  for (const entry of fs.readdirSync(workspaceRoot)) {
    fs.rmSync(path.join(workspaceRoot, entry), { recursive: true, force: true });
  }

  const result = spawnSync(bundledCliPath, [
    'new',
    'aspire-java-starter',
    '--name',
    'JavaStarter',
    '--output',
    workspaceRoot,
    '--version',
    appHostSdkVersion,
    '--localhost-tld',
    'false',
    '--suppress-agent-init',
    '--non-interactive',
    '--nologo',
  ], {
    cwd: extensionRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: 600000,
  });
  fs.writeFileSync(workspaceMarkerFile, `${runId}\n`);

  if (result.error) {
    throw new Error(`Unable to generate the Java starter: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(`Generating the Java starter failed with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.
stdout:
${result.stdout}
stderr:
${result.stderr}`);
  }

  ensureJavaAppHostSdkGenerated(bundledCliPath, workspaceRoot);

  const settingsPath = path.join(workspaceRoot, '.vscode', 'settings.json');
  const settings = fs.existsSync(settingsPath) ? JSON.parse(fs.readFileSync(settingsPath, 'utf8')) : {};
  settings['aspire.aspireCliExecutablePath'] = bundledCliPath;
  settings['aspire.enableAutoRestore'] = false;
  settings['aspire.enableSettingsFileCreationPromptOnStartup'] = false;
  settings['aspire.appHostDiscoveryTimeoutMs'] = 120000;
  settings['java.configuration.updateBuildConfiguration'] = 'automatic';
  fs.writeFileSync(settingsPath, JSON.stringify(settings, undefined, 2));
}

/**
 * Fails when the Java workspace root is excluded by a .gitignore rule.
 *
 * The Java workspace has to live inside the repository (see the note on `javaScratchWorkspaceRoot`),
 * which puts it within reach of the repository's own ignore rules. That matters because `aspire ls`
 * discovers AppHosts from `git ls-files` when it is run inside a work tree: an ignored workspace
 * yields zero files, so `aspire ls` reports no candidates at all and every Java spec fails in its
 * `before all` hook with nothing pointing at the cause. Adding `extension/java-e2e-workspace/` to
 * .gitignore to keep `git status` tidy is exactly the well-intentioned change that breaks this, so
 * name the cause here instead of leaving it to be rediscovered from an empty candidate list.
 */
function assertWorkspaceRootIsNotGitIgnored() {
  const result = spawnSync('git', ['check-ignore', '-q', workspaceRoot], {
    cwd: repoRoot,
    shell: false,
    encoding: 'utf8',
  });

  // git check-ignore exits 0 when the path is ignored, 1 when it is not, and >1 on error. Treat an
  // error (no git, not a work tree) as "not ignored": the CLI falls back to a filesystem scan under
  // the same conditions, so there is nothing to warn about.
  if (result.status === 0) {
    throw new Error(
      `The Java E2E workspace root ${workspaceRoot} is excluded by a .gitignore rule. `
      + '`aspire ls` discovers AppHosts from `git ls-files`, so an ignored workspace reports zero '
      + 'AppHost candidates and every Java spec fails while waiting for one. Remove the ignore rule '
      + 'covering this directory.');
  }
}

/**
 * Makes sure the Java AppHost's generated Aspire SDK exists before VS Code opens the workspace.
 *
 * `aspire restore` runs in the repository-local AppHost because the generator assemblies resolve
 * relative to the repository's package feed; a temporary-directory copy cannot discover them.
 */
function ensureJavaAppHostSdkGenerated(bundledCliPath, appHostDirectory) {
  const generatedModules = path.join(appHostDirectory, '.aspire', 'modules');
  if (fs.existsSync(generatedModules) && fs.readdirSync(generatedModules).length > 0) {
    return;
  }

  logStep('Generating the Aspire Java SDK in the playground');
  const result = spawnSync(bundledCliPath, ['restore'], {
    cwd: appHostDirectory,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: 600000,
  });

  if (result.error) {
    throw new Error(`Unable to run 'aspire restore' in ${appHostDirectory}: ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(`'aspire restore' failed in ${appHostDirectory} with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
  }
}

/**
 * Puts the Java language server and debug adapter into the run's extensions directory.
 *
 * The extension advertises the `java` capability only when both are installed, and the CLI only
 * hands a Java AppHost back to the extension to launch when that capability is advertised, so
 * without these the AppHost still runs and no breakpoint in it can ever bind.
 */
function installJavaExtensions(extestEnv) {
  if (!enableJavaE2E) {
    return;
  }

  const configuredVsixPaths = resolveJavaVsixPaths();
  if (configuredVsixPaths.length > 0) {
    for (const vsixPath of configuredVsixPaths) {
      logStep(`Installing ${path.basename(vsixPath)}`);
      run(process.execPath, [extesterCli, 'install-vsix', '--storage', storageDir, '--extensions_dir', extensionsDir, '--vsix_file', vsixPath], extestEnv, { timeout: 600000 });
    }
  }
  else {
    copyLocallyInstalledJavaExtensions();
  }
}

/**
 * Resolves the Java extension VSIXes the run was given, if any.
 *
 * CI downloads them from the marketplace and passes them here, because a hosted runner has no VS
 * Code profile to borrow them from. The value is a list rather than one variable per extension
 * because the set is an implementation detail of what redhat.java and the debug adapter need from
 * each other, and that set has changed before.
 */
function resolveJavaVsixPaths() {
  const configured = process.env.ASPIRE_EXTENSION_E2E_JAVA_VSIX;
  if (!configured) {
    return [];
  }

  return configured
    .split(path.delimiter)
    .map(entry => entry.trim())
    .filter(entry => entry.length > 0)
    .map(entry => {
      const resolvedPath = path.resolve(entry);
      if (!fs.existsSync(resolvedPath)) {
        throw new Error(`ASPIRE_EXTENSION_E2E_JAVA_VSIX points to a missing file: ${resolvedPath}`);
      }

      validateVsix(resolvedPath);
      return resolvedPath;
    });
}

/**
 * Falls back to the Java extensions already installed in a developer's VS Code.
 *
 * They are installed as already-unpacked directories rather than VSIXes. VS Code treats every
 * immediate subdirectory of --extensions-dir that has a package.json as an installed extension, so
 * copying is equivalent to `install-vsix` and avoids a marketplace download in a run that is
 * otherwise offline. ASPIRE_EXTENSION_E2E_JAVA_EXTENSIONS_DIR names the directory holding them.
 *
 * Without that variable both the stable and Insiders extension directories are searched, and the
 * first one holding every required extension wins. A developer with the Extension Pack for Java in
 * Insiders only would otherwise be told the extensions are missing while they are plainly
 * installed, which is a confusing way to fail a run that has already spent minutes downloading.
 */
function copyLocallyInstalledJavaExtensions() {
  const configuredRoot = process.env.ASPIRE_EXTENSION_E2E_JAVA_EXTENSIONS_DIR;
  const candidateRoots = configuredRoot
    ? [path.resolve(configuredRoot)]
    : [path.join(os.homedir(), '.vscode', 'extensions'), path.join(os.homedir(), '.vscode-insiders', 'extensions')];

  const searched = [];
  let sourceRoot;
  let available = [];
  for (const candidateRoot of candidateRoots) {
    if (!fs.existsSync(candidateRoot)) {
      searched.push(`${candidateRoot} (not found)`);
      continue;
    }

    const entries = fs.readdirSync(candidateRoot, { withFileTypes: true })
      .filter(entry => entry.isDirectory())
      .map(entry => entry.name);
    // Directory names carry a version, and platform-specific builds add a target triple, so match
    // on the `<publisher>.<name>-` prefix rather than an exact name.
    if (REQUIRED_JAVA_EXTENSION_IDS.every(identifier => entries.some(name => name.startsWith(`${identifier}-`)))) {
      sourceRoot = candidateRoot;
      available = entries;
      break;
    }

    searched.push(`${candidateRoot} (missing ${REQUIRED_JAVA_EXTENSION_IDS.filter(identifier => !entries.some(name => name.startsWith(`${identifier}-`))).join(', ')})`);
  }

  if (!sourceRoot) {
    throw new Error(`The Java E2E specs require ${REQUIRED_JAVA_EXTENSION_IDS.join(', ')} to be installed. Searched: ${searched.join('; ')}. Install the Extension Pack for Java, set ASPIRE_EXTENSION_E2E_JAVA_EXTENSIONS_DIR to a directory that has them, or pass VSIXes through ASPIRE_EXTENSION_E2E_JAVA_VSIX.`);
  }

  for (const identifier of REQUIRED_JAVA_EXTENSION_IDS) {
    // Directory names carry a version, and platform-specific builds add a target triple, so match
    // on the `<publisher>.<name>-` prefix rather than an exact name.
    const directoryName = available.find(name => name.startsWith(`${identifier}-`));
    if (!directoryName) {
      throw new Error(`The Java E2E specs require ${identifier} to be installed under ${sourceRoot}. Found: ${available.join(', ') || '(none)'}`);
    }

    const source = path.join(sourceRoot, directoryName);
    assertExtensionSupportsVsCodeVersion(source, directoryName);

    logStep(`Installing ${directoryName}`);
    fs.cpSync(source, path.join(extensionsDir, directoryName), { recursive: true, verbatimSymlinks: true });
  }
}

/**
 * Fails the run when the extensions directory does not end up holding every Java extension.
 *
 * Both install paths can appear to succeed without producing what the specs need: a VSIX for the
 * wrong extension installs cleanly, and a copied directory is ignored outright unless VS Code scans
 * it before extensions.json exists. Either way the specs would launch, find no `java` capability,
 * and fail minutes later with a timeout that says nothing about the missing extension.
 *
 * extensions.json is what VS Code loads from, so it is the only check that distinguishes an
 * extension that is present on disk from one that will actually activate.
 */
function assertJavaExtensionsRegistered() {
  if (!enableJavaE2E) {
    return;
  }

  const manifestPath = path.join(extensionsDir, 'extensions.json');
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`VS Code did not write ${manifestPath}, so no extension is registered for the run.`);
  }

  let registered;
  try {
    registered = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  }
  catch (error) {
    throw new Error(`Unable to read the installed extension list at ${manifestPath}: ${error.message}`);
  }

  const registeredIds = registered
    .map(entry => entry?.identifier?.id)
    .filter(Boolean);

  for (const identifier of REQUIRED_JAVA_EXTENSION_IDS) {
    const entry = registered.find(candidate => candidate?.identifier?.id?.toLowerCase() === identifier.toLowerCase());
    if (!entry) {
      throw new Error(`${identifier} is not registered in ${manifestPath}, so VS Code will not load it. Registered: ${registeredIds.join(', ') || '(none)'}`);
    }

    assertExtensionSupportsVsCodeVersion(path.join(extensionsDir, entry.relativeLocation), entry.relativeLocation);
  }

  console.log(`Java extensions registered for the run: ${REQUIRED_JAVA_EXTENSION_IDS.join(', ')}.`);
}

/**
 * Fails fast when a copied extension declares an engine range the pinned VS Code cannot satisfy.
 *
 * VS Code silently refuses to activate an incompatible extension, which would surface much later as
 * an empty diagnostics list that looks like a passing test.
 */
function assertExtensionSupportsVsCodeVersion(extensionDirectory, directoryName) {
  const manifest = JSON.parse(fs.readFileSync(path.join(extensionDirectory, 'package.json'), 'utf8'));
  const engine = manifest.engines?.vscode;
  if (typeof engine !== 'string' || engine === '*') {
    return;
  }

  const minimum = engine.match(/^\^?(\d+)\.(\d+)\.(\d+)$/);
  if (!minimum) {
    return;
  }

  const required = [Number(minimum[1]), Number(minimum[2]), Number(minimum[3])];
  const actual = vscodeVersion.split('.').map(Number);
  for (let i = 0; i < 3; i++) {
    if ((actual[i] ?? 0) > required[i]) {
      return;
    }

    if ((actual[i] ?? 0) < required[i]) {
      throw new Error(`${directoryName} requires VS Code ${engine} but the E2E run is pinned to ${vscodeVersion}.`);
    }
  }
}

function resolveRequiredVsixPath(environmentVariable) {  const configuredPath = process.env[environmentVariable];
  if (!configuredPath) {
    throw new Error(`${environmentVariable} is required when ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS=true.`);
  }

  const resolvedPath = path.resolve(configuredPath);
  if (!fs.existsSync(resolvedPath)) {
    throw new Error(`${environmentVariable} points to a missing file: ${resolvedPath}`);
  }

  validateVsix(resolvedPath);
  return resolvedPath;
}

function validateAzureFunctionsCoreTools() {
  // Node cannot launch .cmd files directly on Windows, so invoke the trusted, constant
  // Core Tools command through ComSpec instead.
  // https://nodejs.org/api/child_process.html#spawning-bat-and-cmd-files-on-windows
  const displayName = isWindows ? 'func.cmd' : 'func';
  const executable = isWindows ? (process.env.ComSpec || 'cmd.exe') : displayName;
  const args = isWindows ? ['/d', '/s', '/c', 'func.cmd --version'] : ['--version'];
  const result = spawnSync(executable, args, {
    cwd: extensionRoot,
    env: getAspireCliEnvironment(),
    shell: false,
    encoding: 'utf8',
    timeout: 60000,
  });

  if (result.error) {
    throw new Error(`Unable to execute Azure Functions Core Tools (${displayName}): ${result.error.message}`);
  }

  if (result.status !== 0) {
    throw new Error(`Azure Functions Core Tools failed --version with code ${result.status ?? `signal ${result.signal ?? 'unknown'}`}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
  }
}

function packageVsix() {
  run('corepack', ['yarn@1.22.22', 'run', 'vsce', 'package', '--pre-release', '-o', defaultVsixPath], { ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE: 'true' }, { timeout: 300000 });
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

// ExTester does not expose a supported way to open VS Code with a workspace
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
    console.log('Patching ExTester VS Code launch arguments by exact argument match.');
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
  if (enableAzureFunctionsE2E) {
    writeAzureFunctionsProject('AspireE2E.Functions');
  }
  writeAppHostProject('AspireE2E.AppHost', resolvedAppHostSdkVersion, enableAzureFunctionsE2E);
  writeNuGetConfigIfLocalPackageSourcesExist();

  const vscodeDirectory = path.join(workspaceRoot, '.vscode');
  fs.mkdirSync(vscodeDirectory, { recursive: true });
  fs.writeFileSync(path.join(vscodeDirectory, 'settings.json'), JSON.stringify({
    'aspire.aspireCliExecutablePath': resolvedCliPath,
    'aspire.closeDashboardOnDebugEnd': true,
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

  // The Java run deletes the C# fixture projects, so there is nothing here to prewarm.
  if (enableJavaE2E) {
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

function writeAppHostProject(projectName, resolvedAppHostSdkVersion, includeAzureFunctions) {
  const projectDirectory = path.join(workspaceRoot, projectName);
  fs.mkdirSync(projectDirectory, { recursive: true });
  const azureFunctionsPackageReference = includeAzureFunctions
    ? `    <PackageReference Include="Aspire.Hosting.Azure.Functions" Version="${resolvedAppHostSdkVersion}" />\n`
    : '';
  fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Aspire.AppHost.Sdk/${resolvedAppHostSdkVersion}">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../AspireE2E.Worker/AspireE2E.Worker.csproj" />
${azureFunctionsPackageReference}  </ItemGroup>

</Project>
`);

  const azureFunctionsResource = includeAzureFunctions
    ? `builder.AddAzureFunctionsProject("e2e-functions", "../AspireE2E.Functions/AspireE2E.Functions.csproj");\n\n`
    : '';
  fs.writeFileSync(path.join(projectDirectory, 'AppHost.cs'), `${csharpFileHeader}#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRETERMINAL001

using Aspire.Hosting.Pipelines;

// The E2E fixture intentionally covers interaction command arguments and terminal metadata while those APIs are still experimental.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AspireE2E_Worker>("e2e-worker")
    .WithHttpEndpoint(name: "http")
    .WithCommand(
        "echo-arguments",
        "echo-arguments",
        static context => Task.FromResult(CommandResults.Success("Echo arguments completed.", context.Arguments.GetString("message")!)),
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

// e2e-terminal opts into WithTerminal so the real CLI surfaces terminal.enabled and
// terminal.replicaIndex over the backchannel. The extension's Open terminal action reads
// those properties, so this resource exercises that metadata flowing through a real CLI process.
builder.AddProject<Projects.AspireE2E_Worker>("e2e-terminal")
    .WithHttpEndpoint(name: "http")
    .WithTerminal();

${azureFunctionsResource}builder.Pipeline.AddStep("e2e-run-action-step", async context =>
{
    var task = await context.ReportingStep
        .CreateTaskAsync("Running E2E run action pipeline step", context.CancellationToken)
        .ConfigureAwait(false);

    await using (task.ConfigureAwait(false))
    {
        await task.CompleteAsync(
            "E2E run action pipeline step completed",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
});

builder.Pipeline.AddStep("e2e-debug-action-step", async context =>
{
    var task = await context.ReportingStep
        .CreateTaskAsync("Running E2E debug action pipeline step", context.CancellationToken)
        .ConfigureAwait(false);

    await using (task.ConfigureAwait(false))
    {
        await task.CompleteAsync(
            "E2E debug action pipeline step completed",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
});

builder.Build().Run();

sealed class NoCommandsResource(string name) : Aspire.Hosting.ApplicationModel.Resource(name);
`);
}

function writeAzureFunctionsProject(projectName) {
  const projectDirectory = path.join(workspaceRoot, projectName);
  const propertiesDirectory = path.join(projectDirectory, 'Properties');
  const certificatePath = path.join(projectDirectory, 'https-e2e.pfx');
  const certificatePassword = String.raw`Aspire E2E p@ss'\word`;
  fs.mkdirSync(propertiesDirectory, { recursive: true });
  fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.52.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" Version="2.1.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.7" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
`);

  fs.writeFileSync(path.join(projectDirectory, 'Program.cs'), `${csharpFileHeader}using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Build().Run();
`);

  fs.writeFileSync(path.join(projectDirectory, 'HttpsFunction.cs'), `${csharpFileHeader}using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class HttpsFunction
{
    [Function("HttpsFunction")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "https-proof")] HttpRequest request)
    {
        return new OkObjectResult("Aspire HTTPS Functions E2E");
    }
}
`);

  fs.writeFileSync(path.join(projectDirectory, 'host.json'), JSON.stringify({
    version: '2.0',
  }, undefined, 2));

  fs.writeFileSync(path.join(propertiesDirectory, 'launchSettings.json'), JSON.stringify({
    profiles: {
      [projectName]: {
        commandName: 'Project',
        commandLineArgs: `--useHttps --cert "${certificatePath}" --password "${certificatePassword}"`,
        launchBrowser: false,
      },
    },
  }, undefined, 2));

  // Core Tools otherwise depends on ambient development-certificate state, which
  // is intentionally absent on clean hosted runners.
  run('dotnet', ['dev-certs', 'https', '--export-path', certificatePath, '--password', certificatePassword], {}, { timeout: 120000 });
}

function writeWorkerProject(projectName) {
  const projectDirectory = path.join(workspaceRoot, projectName);
  fs.mkdirSync(projectDirectory, { recursive: true });
  fs.writeFileSync(path.join(projectDirectory, `${projectName}.csproj`), `<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
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

/**
 * Pins the VS Code version the cached runner keys on.
 *
 * ExTester accepts `latest`, which resolves to whatever Microsoft is shipping the moment it asks.
 * Keying on that literal would freeze the first release ever downloaded into `vscode-latest` and
 * quietly serve it forever, which is the opposite of what somebody asking for `latest` wants.
 * `min` and `max` are safe to key literally because ExTester resolves them from the version pinned
 * in package.json, and that version is already part of the cache key.
 */
function resolveCachedVsCodeVersion(requestedVersion) {
  const normalizedVersion = requestedVersion.trim().toLowerCase();
  if (normalizedVersion === 'min' || normalizedVersion === 'max' || /^\d+\.\d+(\.\d+)?$/.test(normalizedVersion)) {
    return normalizedVersion;
  }

  throw new Error(`ASPIRE_EXTENSION_E2E_VSCODE_VERSION must be a concrete version such as '1.130.0', or 'min'/'max', but was '${requestedVersion}'. Moving aliases cannot be cached because the cache key would never change when the alias does.`);
}

function assertVsCodeVersionCompatibleWithExtester(vscodeVersion, extesterVersion) {
  if (vscodeVersion === 'min' || vscodeVersion === 'max') {
    return;
  }

  // On macOS, ExTester 8.23 always launches Contents/MacOS/Electron, which VS Code removed in
  // 1.131. Reject the pair before creating a run root or publishing a cache entry. Linux and
  // Windows use different executable paths and remain compatible with the same concrete override.
  if (process.platform === 'darwin' && compareConcreteVersions(vscodeVersion, '1.131.0') >= 0 && compareConcreteVersions(extesterVersion, '8.24.0') < 0) {
    throw new Error(`VS Code ${vscodeVersion} cannot be used with ExTester ${extesterVersion} on macOS: this ExTester version launches only Contents/MacOS/Electron, which VS Code 1.131.0 and newer no longer provide.`);
  }
}

function compareConcreteVersions(left, right) {
  const leftParts = left.split('.').map(Number);
  const rightParts = right.split('.').map(Number);

  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index++) {
    const difference = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
    if (difference !== 0) {
      return difference;
    }
  }

  return 0;
}

function getAspireCliEnvironment(extraEnv = {}) {
  return {
    ...process.env,
    ASPIRE_HOME: process.env.ASPIRE_EXTENSION_E2E_ASPIRE_HOME || isolatedAspireHome,
    ASPIRE_CLI_START_TIMEOUT: process.env.ASPIRE_EXTENSION_E2E_CLI_START_TIMEOUT || '300',
    ASPIRE_CLI_TELEMETRY_OPTOUT: 'true',
    ASPIRE_VERSION_CHECK_DISABLED: 'true',
    NUGET_PACKAGES: e2eNuGetPackages,
    DOTNET_CLI_UI_LANGUAGE: 'en',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: '1',
    DOTNET_NOLOGO: '1',
    MSBUILDTERMINALLOGGER: 'false',
    features__updateNotificationsEnabled: 'false',
    // A single-file Java AppHost is started by the CLI's AppHost server, which the CLI locates
    // either from an installed bundle or, in dev mode, from the Aspire repository root. The E2E
    // workspace CLI copy has no bundle, so without this the CLI fails with "No Aspire AppHost
    // server is available". Java support is also still experimental, and `aspire ls` silently
    // omits Java AppHosts unless both feature flags are on - the isolated ASPIRE_HOME has neither.
    ...(enableJavaE2E ? {
      ASPIRE_REPO_ROOT: repoRoot,
      features__polyglotSupportEnabled: 'true',
      features__experimentalPolyglot__java: 'true',
    } : {}),
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
  const nugetConfig = `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
${sourceEntries}
${fallbackSourceEntries}
  </packageSources>
</configuration>
`;
  // External AppHost fixtures are siblings of the workspace, while an explicitly supplied
  // workspace may sit outside the run root. Keep both restore scopes deterministic.
  fs.writeFileSync(runRootNuGetConfigPath, nugetConfig);
  fs.writeFileSync(workspaceNuGetConfigPath, nugetConfig);
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

  if (result.error?.code === 'ETIMEDOUT' && options.terminateOrphansUnder) {
    try {
      terminateOrphanedDescendants(options.terminateOrphansUnder);
    } catch (cleanupError) {
      // Falling through to a retry here would let `beforeRetry` delete, and a later attempt
      // validate and publish, a directory an unpack process may still be writing into -- exactly
      // the corruption this cleanup exists to prevent. Nothing is known about the directory once
      // the orphans cannot be accounted for, so abandon the candidate instead of reusing it.
      throw markErrorNonRetryable(new Error(`${command} ${args.join(' ')} timed out and the unpack processes it left behind under ${options.terminateOrphansUnder} could not be confirmed dead: ${cleanupError instanceof Error ? cleanupError.message : String(cleanupError)}`));
    }
  }

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
  runWithRetries(() => run(command, args, extraEnv, options), {
    attempts: options.attempts,
    retryDelayMs: options.retryDelayMs,
    beforeRetry: options.beforeRetry,
    description: `${command} ${args.join(' ')}`,
  });
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

  // The Java run deliberately places its workspace inside the repository rather than the temp root,
  // so that the CLI resolves packages the way it does for the playground. The marker-file check
  // still applies, so this is not a weakening of the delete guard.
  if (isSamePath(resolvedWorkspaceRoot, resolveExistingPathForSafety(javaScratchWorkspaceRoot))) {
    assertWorkspaceRootCarriesMarker();
    return;
  }

  if (process.env.ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP !== 'true') {
    throw new Error(`ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT must be under the runner temp root unless ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP=true is set. Refusing to delete ${workspaceRoot}.`);
  }

  assertWorkspaceRootCarriesMarker();
}

/**
 * Refuses to delete a workspace root that holds content the runner did not create.
 *
 * An empty directory is accepted because it cannot hold anything worth protecting, and because
 * `prepareWorkspaceFixture` creates the directory before it writes the marker: a run interrupted in
 * that window, or one that failed before writing the marker, otherwise leaves a markerless
 * directory that blocks every later run with no way to recover but manual deletion.
 */
function assertWorkspaceRootCarriesMarker() {
  if (isWorkspaceRootOwnedByRunner()) {
    return;
  }

  throw new Error(
    `Refusing to delete external E2E workspace root without marker file ${workspaceMarkerFile}. `
    + `Delete ${workspaceRoot} manually if it is not yours, then re-run.`);
}

/**
 * Reports whether the workspace root is the runner's to delete: absent, marked by a previous run,
 * or empty. Both the pre-run fixture reset and the post-run cleanup consult this, so a developer
 * directory that happens to sit at the workspace path survives a run intact.
 */
function isWorkspaceRootOwnedByRunner() {
  if (!fs.existsSync(workspaceRoot) || fs.existsSync(workspaceMarkerFile)) {
    return true;
  }

  return fs.readdirSync(workspaceRoot).length === 0;
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

/**
 * Deletes what a failed ExTester download left behind, so the retry starts clean.
 *
 * Two kinds of debris land directly in the storage root. The first is the downloaded archive.
 * The second is `vscode-temp-<random>`, which ExTester unpacks VS Code into before moving it into
 * place and removes in a `finally` -- a `finally` that never runs when the process is killed for
 * exceeding its timeout, leaving a full unpacked copy behind. Nothing rejects that copy later, so
 * a retry that succeeds would publish several hundred extra megabytes per timed-out attempt into
 * an entry that is supposed to have been pruned.
 *
 * Only ordinary files and these known directories at the top level are touched. Archives nested
 * deeper belong to an application that has already been unpacked -- VS Code ships some of its own
 * -- and deleting those would publish a permanently damaged entry to the shared cache, because a
 * ChromeDriver retry runs after VS Code has been unpacked into the same directory. This mirrors
 * `pruneDownloadArchives` in the cache module for the same reason.
 */
function cleanPartialExtesterDownloads(storageDirectory) {
  let entries;
  try {
    entries = fs.readdirSync(storageDirectory, { withFileTypes: true });
  } catch (error) {
    if (error && error.code === 'ENOENT') {
      return;
    }

    throw error;
  }

  for (const entry of entries) {
    const entryPath = path.join(storageDirectory, entry.name);
    if (entry.isFile() && isPartialDownloadArchiveName(entry.name)) {
      fs.rmSync(entryPath, { force: true });
      continue;
    }

    if (entry.isDirectory() && entry.name.startsWith(EXTESTER_UNPACK_DIRECTORY_PREFIX)) {
      // Never recursive: an abandoned extraction holds the VS Code bundle's internal links, and
      // the shared cache sits on the other end of the projections in this tree.
      removePathWithoutFollowingLinks(entryPath);
    }
  }
}

function isPartialDownloadArchiveName(name) {
  const lowerCaseName = name.toLowerCase();
  return lowerCaseName.endsWith('.zip')
    || lowerCaseName.endsWith('.tar.gz')
    || lowerCaseName.endsWith('.tgz')
    || lowerCaseName.endsWith('.gz');
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

  // The Java workspace lives inside the repository and is deliberately not gitignored, so leaving it
  // behind would show up as untracked changes and could be committed by accident.
  //
  // Only remove it when the runner owns it. `prepareWorkspaceFixture` refuses to delete a workspace
  // root that holds content the runner did not create, and deleting it here regardless would defeat
  // that guard and destroy a developer's directory that happens to sit at this path.
  if (enableJavaE2E && workspaceRoot === javaScratchWorkspaceRoot && isWorkspaceRootOwnedByRunner()) {
    removePathWithoutFollowingLinks(javaScratchWorkspaceRoot, { recursive: true, force: true });
  }

  // The storage directory under this root holds the projected VS Code and ChromeDriver artifacts,
  // which are junctions on Windows, and recursive removal descends junctions there. Tearing this
  // tree down recursively would delete the shared download cache every other run depends on, so
  // remove it link by link and detach those projections instead of following them.
  try {
    removePathWithoutFollowingLinks(shortRunRoot, {
      maxRetries: process.platform === 'win32' ? 20 : 0,
      retryDelay: 250,
    });
  }
  catch (error) {
    if (process.platform === 'win32' && isRetryableWindowsFileLock(error)) {
      console.warn(`Warning: unable to remove locked E2E path '${shortRunRoot}': ${error.message}`);
      return;
    }

    throw error;
  }
}

function sanitizePathSegment(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '-');
}

function removePath(targetPath, options = {}) {
  fs.rmSync(targetPath, {
    maxRetries: process.platform === 'win32' ? 20 : 0,
    retryDelay: 250,
    ...options,
  });
}

function isRetryableWindowsFileLock(error) {
  return error && typeof error === 'object' && ['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code);
}
