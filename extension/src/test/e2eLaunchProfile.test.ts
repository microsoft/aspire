import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

suite('E2E launch profile', () => {
    test('uses in-memory secret storage so VS Code does not prompt for OS keychain access', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes("'--disable-keytar'"));
        assert.ok(runner.includes("'--use-inmemory-secretstorage'"));
        assert.ok(runner.includes("'--password-store=basic'"));
        assert.ok(runner.includes("'--disable-extension', 'vscode.github-authentication'"));
        assert.ok(runner.includes("'--disable-extension', 'vscode.microsoft-authentication'"));
    });

    test('opens the E2E workspace as a VS Code startup folder', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('JSON.stringify(workspaceRoot)'));
        assert.ok(!runner.includes("'--open_resource', workspaceRoot"));
    });

    test('clears the E2E control file before explicit workspace reloads', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const openWorkspaceCase = e2eStateFileBridge.slice(e2eStateFileBridge.indexOf("case 'openWorkspaceFolder'"), e2eStateFileBridge.indexOf("case 'getWorkspaceFolders'"));
        const clearControlFileIndex = openWorkspaceCase.indexOf('clearPendingE2eControlFile();');
        const openFolderIndex = openWorkspaceCase.indexOf("vscode.commands.executeCommand('vscode.openFolder'");

        assert.ok(apiTypes.includes("{ name: 'openWorkspaceFolder'; folderPath: string }"));
        assert.ok(clearControlFileIndex >= 0);
        assert.ok(openFolderIndex > clearControlFileIndex);
    });

    test('validates explicit workspace folder before reporting bridge command start', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const openWorkspaceCase = e2eStateFileBridge.slice(e2eStateFileBridge.indexOf("case 'openWorkspaceFolder'"), e2eStateFileBridge.indexOf("case 'getWorkspaceFolders'"));

        assert.ok(openWorkspaceCase.indexOf('getE2eWorkspaceFolderPath') < openWorkspaceCase.indexOf('markStarted();'));
    });

    test('uses a shared timeout budget for workspace recovery and AppHost discovery', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');

        assert.ok(assertions.includes('const deadline = createDeadline(timeoutMs);'));
        assert.ok(assertions.includes('getRemainingTimeout(deadline'));
        assert.ok(assertions.includes('throwIfControlFailed(openWorkspaceRevision);'));
    });

    test('bounds the ExTester process below the workflow timeout so diagnostics still run', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS'));
        assert.ok(runner.includes('await runWithProcessTreeTimeout(process.execPath'));
        assert.ok(runner.includes('getRunTestsTimeoutMs()'));
        assert.ok(runner.includes('2400000'));
        assert.ok(runner.includes('did not exit after process-tree termination'));
        assert.ok(runner.includes('child.unref()'));
        assert.ok(runner.includes("spawnSync('taskkill'"));
        assert.ok(runner.includes("terminateProcessTree(child.pid, 'SIGTERM')"));
        assert.ok(runner.includes("terminateProcessTree(child.pid, 'SIGKILL')"));
        assert.ok(runner.includes('process.kill(-pid, signal)'));
    });

    test('bounds retryable runner setup steps so setup failures still collect diagnostics', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes("'get-vscode'"));
        assert.ok(runner.includes('attempts: 2'));
        assert.ok(runner.includes('timeout: 240000'));
        assert.ok(runner.includes("'get-chromedriver'"));
        assert.ok(runner.includes('run(command, args, extraEnv, options);'));
    });

    test('guards destructive E2E workspace cleanup', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('assertWorkspaceRootSafeForDeletion();'));
        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP'));
        assert.ok(runner.includes('.aspire-extension-e2e-workspace'));
        assert.ok(runner.includes('Refusing to delete dangerous E2E workspace root'));
    });

    test('redacts sensitive dashboard URLs from runner failure diagnostics', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('debugSessions: state.state.debugSessions?.map(redactDebugSessionForDiagnostics)'));
        assert.ok(runner.includes('sanitizeDashboardUrlForDiagnostics'));
        assert.ok(runner.includes('redactTextFilesForArtifacts(resultsDir)'));
        assert.ok(runner.includes('redactTextFilesForArtifacts(storageDiagnosticsDir)'));
        assert.ok(runner.includes('skipAspireLeaseFiles'));
        assert.ok(runner.includes('/login?t=<redacted>'));
        assert.ok(runner.includes('new URL(stripResourceSuffix(url)).origin'));
    });

    test('installs the E2E runner dependencies from the internal npm feed', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const packageJson = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
        const lockfile = fs.readFileSync(path.join(extensionRoot, 'yarn.lock'), 'utf8');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');
        const internalFeed = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/';

        assert.strictEqual(packageJson.devDependencies['vscode-extension-tester'], '8.23.0');
        assert.strictEqual(packageJson.resolutions.undici, '7.27.0');
        assert.ok(lockfile.includes('vscode-extension-tester@8.23.0'));
        assert.ok(lockfile.includes('undici@7.27.0'));
        assert.ok(lockfile.split(/\r?\n/).filter(l => /^\s*resolved\s+"/.test(l)).every(l => l.includes(internalFeed)));
        assert.ok(workflow.includes('NPM_REGISTRY: https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/'));
        assert.ok(fs.existsSync(path.join(extensionRoot, 'scripts', 'validate-lockfile-registry.cjs')));
        assert.ok(workflow.includes('run: node scripts/validate-lockfile-registry.cjs'));
        assert.ok(workflow.includes('corepack yarn install --frozen-lockfile --non-interactive'));
        assert.ok(!workflow.includes('ASPIRE_EXTENSION_E2E_EXTESTER_NPM_REGISTRY'));
        assert.ok(!workflow.includes('registry=https://'));
    });

    test('preflights locked ExTester dependency graph before starting the E2E matrix', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');

        assert.ok(runner.includes('--verify-extester-feed'));
        assert.ok(runner.includes('Verifying vscode-extension-tester@'));
        assert.ok(runner.indexOf('const verifyExtesterFeedOnly = process.argv.includes') < runner.indexOf('fs.mkdtempSync'));
        assert.ok(runner.includes('if (!verifyExtesterFeedOnly)'));
        assert.ok(runner.includes('const matchedTestSpecs = verifyExtesterFeedOnly ? [] : findSpecMatches(testSpec);'));
        assert.ok(!runner.includes('ASPIRE_EXTENSION_E2E_EXTESTER_VERSION'));
        assert.ok(workflow.includes('Verify locked ExTester'));
        assert.ok(workflow.includes('verify_extester_feed:'));
        assert.ok(workflow.includes('run: node scripts/run-e2e.js --verify-extester-feed'));
        assert.ok(workflow.includes('needs: verify_extester_feed'));
        assert.ok(!workflow.includes('extester_feed_unavailable:'));
        assert.ok(!workflow.includes('VS Code extension E2E matrix skipped'));
    });

    test('keeps Linux E2E recordings for successful runs by default', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');

        assert.ok(workflow.includes("ASPIRE_EXTENSION_E2E_RECORDING_MODE: ${{ matrix.useXvfb && 'always' || 'off' }}"));
        assert.ok(workflow.includes('Linux CI keeps recordings by default; Windows shards upload screenshots and logs only.'));
    });

    test('waits for ffmpeg to flush before reporting E2E recordings as saved', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('ffmpeg.once(\'close\''));
        assert.ok(runner.includes("await runCleanupStep('stop recording', () => stopRecording(recording, testFailure), cleanupErrors);"));
        assert.ok(runner.includes("signalProcess(pid, 'SIGINT')"));
        assert.ok(runner.includes('waitForProcessClose(recording.closed, 15000)'));
        assert.ok(runner.includes('stoppedGracefully && fs.existsSync(recording.outputPath)'));
    });

    test('seeds Corepack from the internal npm feed before E2E workflow uses Yarn', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');
        const bashCorepackInstallIndex = workflow.indexOf('npm install --global --force --registry "$NPM_REGISTRY" "corepack@$CorepackVersion"');
        const pwshCorepackInstallIndex = workflow.indexOf('npm install --global --force --registry "$env:NPM_REGISTRY" "corepack@$CorepackVersion"');
        const yarnSeedIndex = workflow.indexOf('node ./scripts/prepareCorepackYarn.mjs');
        const yarnInstallIndex = workflow.indexOf('corepack yarn install --frozen-lockfile --non-interactive');
        const yarnCompileIndex = workflow.indexOf('corepack yarn compile');

        assert.ok(workflow.includes('NPM_REGISTRY: https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/'));
        assert.ok(workflow.includes('COREPACK_ENABLE_DOWNLOAD_PROMPT: 0'));
        assert.ok(bashCorepackInstallIndex >= 0);
        assert.ok(pwshCorepackInstallIndex >= 0);
        assert.ok(yarnSeedIndex > bashCorepackInstallIndex);
        assert.ok(yarnInstallIndex > yarnSeedIndex);
        assert.ok(yarnCompileIndex > yarnSeedIndex);
        assert.ok(!workflow.includes('cache: yarn'));
    });

    test('opts out of telemetry for all CLI processes spawned by E2E tests', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const envConstruction = runner.slice(runner.indexOf('const extestEnv = getAspireCliEnvironment({'), runner.indexOf("logStep('Downloading VS Code');"));
        const runTestsStart = runner.indexOf("logStep('Running VS Code extension E2E tests');");
        const runTests = runner.slice(runTestsStart, runner.indexOf('catch (error)', runTestsStart));
        const aspireCliEnvironmentStart = runner.indexOf('function getAspireCliEnvironment');
        const aspireCliEnvironmentEnd = runner.indexOf('function writeNuGetConfigIfLocalPackageSourcesExist');
        const aspireCliEnvironment = runner.slice(aspireCliEnvironmentStart, aspireCliEnvironmentEnd);

        assert.ok(aspireCliEnvironmentStart >= 0);
        assert.ok(aspireCliEnvironmentEnd > aspireCliEnvironmentStart);
        assert.ok(aspireCliEnvironment.includes("ASPIRE_CLI_TELEMETRY_OPTOUT: 'true'"));
        assert.ok(aspireCliEnvironment.includes("DOTNET_CLI_UI_LANGUAGE: 'en'"));
        assert.ok(aspireCliEnvironment.includes("DOTNET_CLI_TELEMETRY_OPTOUT: '1'"));
        assert.ok(envConstruction.includes('const extestEnv = getAspireCliEnvironment({'));
        assert.ok(envConstruction.includes("ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE: 'true'"));
        assert.ok(runTests.includes('runWithProcessTreeTimeout(process.execPath'));
        assert.ok(runTests.includes('extestEnv'));
    });

    test('suppresses evaluation diagnostics for intentional E2E AppHost interaction APIs', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('#pragma warning disable ASPIREINTERACTION001'));
        assert.ok(runner.includes('new InteractionInput'));
        assert.ok(runner.includes('InputType.SecretText'));
    });

    test('launches VS Code E2E tests with telemetry disabled before extension activation', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const settings = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'test-e2e', 'settings.json'), 'utf8'));

        assert.strictEqual(settings['telemetry.telemetryLevel'], 'off');
        assert.ok(runner.includes("'--disable-telemetry'"));
    });

    test('patches ExTester launch arguments without replacement-token expansion', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes('ExTester 8.23.0 does not expose a supported way to open VS Code with a workspace'));
        assert.ok(runner.includes('Patching ExTester VS Code launch arguments by exact 8.23.0 argument match.'));
        assert.ok(runner.includes('source.replace(target, () => replacement)'));
        assert.ok(runner.includes('source.replace(argsDeclarationPattern, () => replacement)'));
    });

    test('keeps the slow zero-to-running shard timeout above its composed wait budgets', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const zeroToRunning = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'zeroToRunning.e2e.test.ts'), 'utf8');

        assert.ok(zeroToRunning.includes('this.timeout(2100000);'));
        assert.ok(zeroToRunning.includes('waitForDebugSessionStartup(appHostPath, 300000)'));
        assert.ok(zeroToRunning.includes('waitForDebugDashboardUrl(appHostPath, 180000)'));
        assert.ok(zeroToRunning.includes("waitForHttpText(dashboardUrl, 'Aspire', 180000"));
        assert.ok(zeroToRunning.includes("process.platform === 'linux'"));
        assert.ok(zeroToRunning.includes("waitForWorkbenchTextAfterIntegratedBrowserNavigation(['Resources', dashboardHost], 180000)"));
        assert.ok(!zeroToRunning.includes("waitForEditorTitle(dashboardHost"));
        assert.ok(!zeroToRunning.includes("waitForEditorTitle(new URL(dashboardUrl).host"));
    });

    test('uses integrated-browser webview text instead of editor title waits', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const appHostTreeProvider = fs.readFileSync(path.join(extensionRoot, 'src', 'views', 'AspireAppHostTreeProvider.ts'), 'utf8');
        const treeActions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'treeActions.e2e.test.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');

        assert.ok(appHostTreeProvider.includes("await vscode.commands.executeCommand('simpleBrowser.show', element.url);"));
        assert.ok(treeActions.includes("assert.strictEqual((openedEndpoint.result as { url?: string }).url, endpointUrl);"));
        assert.ok(treeActions.includes('waitForWorkbenchTextAfterIntegratedBrowserNavigation(new URL(endpointUrl).host)'));
        assert.ok(treeActions.includes("waitForHttpText(endpointUrl, 'ok')"));
        assert.ok(!treeActions.includes('waitForEditorTitle(new URL(endpointUrl).host'));
        assert.ok(e2eStateFileBridge.includes('return { url: endpoint.url };'));
        assert.ok(e2eStateFileBridge.includes("case 'publishAppHost':"));
        assert.ok(e2eStateFileBridge.includes("appHostLaunchService.launch(command.appHostPath, 'publish', true)"));
    });

    test('hides AppHost outside the workspace for empty-discovery coverage', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const paths = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'paths.ts'), 'utf8');
        const discoveryConfiguration = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'discoveryConfiguration.e2e.test.ts'), 'utf8');

        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_RUN_ROOT: shortRunRoot'));
        assert.ok(paths.includes('export function getRunRoot()'));
        assert.ok(discoveryConfiguration.includes('const hiddenAppHostDirectory = getHiddenAppHostDirectory(appHostDirectory);'));
        assert.ok(discoveryConfiguration.includes("path.join(runRoot, '.e2e-hidden-apphost')"));
        assert.ok(!discoveryConfiguration.includes("path.join(getWorkspaceRoot(), '.e2e-hidden-apphost')"));
    });

    test('uses monotonic E2E event sequences instead of positional slices over capped buffers', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');

        assert.ok(apiTypes.includes('sequence: number;'));
        assert.ok(e2eStateFileBridge.includes('commandInvocationSequence'));
        assert.ok(e2eStateFileBridge.includes('terminalCommandSequence'));
        assert.ok(e2eStateFileBridge.includes('debugLaunchSequence'));
        assert.ok(assertions.includes('event.sequence > afterInvocationSequence'));
        assert.ok(!assertions.includes('.slice(afterInvocationCount)'));
        assert.ok(!assertions.includes('.slice(afterCommandCount)'));
        assert.ok(!assertions.includes('.slice(afterLaunchCount)'));
    });

    test('writes E2E control and mutable fixture files with Windows-safe retries', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const debugDashboard = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'debugDashboard.e2e.test.ts'), 'utf8');
        const extensionRenameRetryStart = e2eStateFileBridge.indexOf('function isRetryableRenameError');
        const extensionRenameRetryEnd = e2eStateFileBridge.indexOf('function sleepSynchronously');
        const renameRetryStart = assertions.indexOf('function isRetryableRenameError');
        const renameRetryEnd = assertions.indexOf('function isDebugSessionForAppHost');
        assert.ok(extensionRenameRetryStart >= 0);
        assert.ok(extensionRenameRetryEnd > extensionRenameRetryStart);
        assert.ok(renameRetryStart >= 0);
        assert.ok(renameRetryEnd > renameRetryStart);
        const extensionRenameRetry = e2eStateFileBridge.slice(extensionRenameRetryStart, extensionRenameRetryEnd);
        const renameRetry = assertions.slice(renameRetryStart, renameRetryEnd);

        assert.ok(assertions.includes('writeJsonFileAtomic(controlFilePath'));
        assert.ok(assertions.includes('renameFileWithRetry(temporaryPath, filePath)'));
        assert.ok(extensionRenameRetry.includes("error.code === 'EPERM'"));
        assert.ok(extensionRenameRetry.includes("error.code === 'EACCES'"));
        assert.ok(extensionRenameRetry.includes("error.code === 'EEXIST'"));
        assert.ok(renameRetry.includes("error.code === 'EBUSY'"));
        assert.ok(fixtures.includes('writeFileWithRetry(settingsPath'));
        assert.ok(fixtures.includes('removePath(getWorkspaceAppHostConfigPath(), { force: true });'));
        assert.ok(fixtures.includes("removePath(path.join(getWorkspaceRoot(), '.aspire'), { recursive: true, force: true });"));
        assert.ok(fixtures.includes("const maxAttempts = process.platform === 'win32' ? 40 : 1;"));
        assert.ok(fixtures.includes('fs.rmSync(targetPath, options);'));
        assert.ok(debugDashboard.includes('writeFileWithRetry(appHostSourcePath, brokenSource);'));
        assert.ok(debugDashboard.includes('writeFileWithRetry(appHostSourcePath, originalSource)'));
        assert.ok(debugDashboard.includes("__AspireE2EFlushRegressionMissingSymbol__' does not exist"));
        assert.ok(!debugDashboard.includes('waitForLogFileText'));
        assert.ok(fixtures.includes("code === 'EBUSY'"));
        assert.ok(fixtures.includes("code === 'EPERM'"));
        assert.ok(fixtures.includes("code === 'EACCES'"));
    });

    test('uses lightweight secondary AppHost candidates for discovery-only E2E coverage', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const commandPalette = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'commandPalette.e2e.test.ts'), 'utf8');
        const discoveryConfiguration = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'discoveryConfiguration.e2e.test.ts'), 'utf8');

        assert.ok(commandPalette.includes('this.timeout(420000);'));
        assert.ok(fixtures.includes("kind: 'project' | 'single-file' = 'project'"));
        assert.ok(fixtures.includes("path.join(projectDirectory, 'apphost.cs')"));
        assert.ok(fixtures.includes('#:sdk Aspire.AppHost.Sdk@${getAppHostSdkVersion()}'));
        assert.ok(commandPalette.includes("createAdditionalAppHostCandidate('AspireE2E.SecondAppHost', 'single-file')"));
        assert.ok(discoveryConfiguration.includes("createAdditionalAppHostCandidate('AspireE2E.SecondAppHost', 'single-file')"));
        assert.ok(discoveryConfiguration.includes('restored primary AppHost without stale secondary candidate'));
    });

    test('waits for running AppHost processes to exit before deleting E2E fixture directories', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const zeroToRunning = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'zeroToRunning.e2e.test.ts'), 'utf8');
        const commandPalette = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'commandPalette.e2e.test.ts'), 'utf8');
        const discoveryConfiguration = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'discoveryConfiguration.e2e.test.ts'), 'utf8');
        const stopAppHostStart = fixtures.indexOf('export async function stopAppHostIfRunning');
        const stopAppHostEnd = fixtures.indexOf('interface PsAppHost');
        const stopKnownProcessStart = fixtures.indexOf('async function waitForNoRunningAppHostPathOrStopKnownProcess');
        const stopKnownProcessEnd = fixtures.indexOf('function getRunningAppHostFromState');
        assert.ok(stopAppHostStart >= 0);
        assert.ok(stopAppHostEnd > stopAppHostStart);
        assert.ok(stopKnownProcessStart >= 0);
        assert.ok(stopKnownProcessEnd > stopKnownProcessStart);
        const stopAppHost = fixtures.slice(stopAppHostStart, stopAppHostEnd);
        const stopKnownProcess = fixtures.slice(stopKnownProcessStart, stopKnownProcessEnd);
        const waitForCapturedPidCalls = stopAppHost.match(/await waitForNoRunningAppHostPathOrStopKnownProcess\(appHostPath, 30000, runningAppHostBeforeStop\?\.appHostPid, 'after stopping'\);/g) ?? [];
        const stopErrorAssignmentStart = stopAppHost.indexOf('const stopError = await tryStopAppHost(appHostPath);');
        const successfulStopStart = stopAppHost.indexOf('if (!stopError)');
        const successfulStopEnd = stopAppHost.indexOf('if (/not running|No running AppHost|No AppHost/i.test(stopError.message))');
        const successfulStopWait = stopAppHost.indexOf("await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping');", successfulStopStart);
        const timedOutStopStart = stopAppHost.indexOf('if (/timed out|Failed to stop/i.test(stopError.message))');

        assert.ok(stopErrorAssignmentStart >= 0);
        assert.ok(successfulStopStart > stopErrorAssignmentStart);
        assert.ok(successfulStopEnd > successfulStopStart);
        assert.ok(successfulStopWait > successfulStopStart && successfulStopWait < successfulStopEnd);
        assert.ok(timedOutStopStart > successfulStopEnd);
        assert.ok(stopAppHost.includes('const runningAppHostBeforeStop = getRunningAppHostFromState(appHostPath);'));
        assert.ok(waitForCapturedPidCalls.length >= 3);
        assert.ok(stopAppHost.includes('const runningAppHost = await getRunningAppHostAccordingToCli(appHostPath);'));
        assert.ok(stopAppHost.includes('await waitForProcessExit(runningAppHost.appHostPid, 30000);'));
        assert.ok(stopAppHost.includes('if (!await getRunningAppHostAccordingToCli(appHostPath))'));
        assert.ok(stopAppHost.includes('if (isProcessRunning(runningAppHost.appHostPid))'));
        assert.ok(stopAppHost.includes('await stopProcess(runningAppHost.appHostPid, 30000);'));
        assert.ok(fixtures.includes('export function getRunningAppHostPid(appHostPath: string): number | undefined'));
        assert.ok(fixtures.includes('export async function waitForRunningAppHostPid(appHostPath: string, timeoutMs: number): Promise<number>'));
        assert.ok(fixtures.includes('removeGeneratedProject(projectName: string, knownAppHostPid?: number)'));
        assert.ok(zeroToRunning.includes('let appHostPidBeforeStop: number | undefined;'));
        assert.ok(zeroToRunning.includes('setup(() => {'));
        assert.ok(zeroToRunning.includes('appHostPidBeforeStop = undefined;'));
        assert.ok(zeroToRunning.includes('() => appHostPidBeforeStop ??= getRunningAppHostPid(appHostPath)'));
        assert.ok(zeroToRunning.indexOf('() => appHostPidBeforeStop ??= getRunningAppHostPid(appHostPath)') > zeroToRunning.indexOf('await runE2eTeardown(['));
        assert.ok(zeroToRunning.indexOf('appHostPidBeforeStop = await waitForRunningAppHostPid(appHostPath, 30000);') < zeroToRunning.lastIndexOf("executeE2eControlCommand({ name: 'stopDebugging' })"));
        assert.ok(zeroToRunning.includes('removeGeneratedProject(projectName, appHostPidBeforeStop)'));
        assert.ok(commandPalette.includes('runE2eTeardown'));
        assert.ok(discoveryConfiguration.includes('runE2eTeardown'));
        assert.ok(!commandPalette.includes('throw new AggregateError'));
        assert.ok(!discoveryConfiguration.includes('throw new AggregateError'));
        assert.ok(fixtures.includes("['ps', '--format', 'json']"));
        assert.ok(fixtures.includes('Number.isInteger(candidate.appHostPid)'));
        assert.ok(fixtures.includes('let lastKnownAppHostPid = knownAppHostPid;'));
        assert.ok(fixtures.includes('lastKnownAppHostPid = runningAppHost.appHostPid;'));
        assert.ok(!fixtures.includes('terminateProcessTree(runningAppHost.appHostPid'));
        assert.ok(fixtures.includes("await waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop?.appHostPid, 'after stopping')"));
        assert.ok(fixtures.includes("await waitForNoRunningAppHostPathOrStopKnownProcess(getGeneratedAppHostPath(projectName), 30000, knownAppHostPid, 'before deleting')"));
        assert.ok(fixtures.includes('async function waitForProcessExit(pid: number, timeoutMs: number): Promise<void>'));
        assert.ok(fixtures.includes('process.kill(pid, 0);'));
        assert.ok(fixtures.includes("process.kill(pid, 'SIGTERM');"));
        assert.ok(fixtures.includes('async function waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath: string, timeoutMs: number, knownAppHostPid: number | undefined, actionDescription: string): Promise<void>'));
        assert.ok(stopKnownProcess.indexOf('const runningAppHost = await getRunningAppHostAccordingToCli(appHostPath);') < stopKnownProcess.indexOf('await stopProcess(runningAppHost.appHostPid, 30000);'));
        assert.ok(stopKnownProcess.includes('stale/reused'));
        assert.ok(fixtures.includes('formatE2eTeardownFailureMessage(failureMessage, failures.map(redactE2eTeardownFailure))'));
        assert.ok(fixtures.includes('function redactE2eTeardownFailure(failure: unknown): string'));
        assert.ok(!fixtures.includes('error?.stack'));
        assert.ok(fixtures.includes("code === 'ENOTEMPTY'"));
        assert.ok(fixtures.includes("error.code === 'EPERM'"));
        assert.ok(fixtures.includes("const maxAttempts = process.platform === 'win32' ? 40 : 1;"));
    });

    test('keeps tree action resource lifecycle commands as terminal routing assertions', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const treeActions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'treeActions.e2e.test.ts'), 'utf8');
        const stopResourceStart = treeActions.indexOf("getCommandInvocationCount('aspire-vscode.stopResource')");
        const executeResourceCommandStart = treeActions.indexOf("getCommandInvocationCount('aspire-vscode.executeResourceCommandItem')");
        assert.ok(stopResourceStart >= 0);
        assert.ok(executeResourceCommandStart > stopResourceStart);
        const resourceLifecycleSuppressionStart = treeActions.lastIndexOf('await setTerminalCommandExecutionSuppressedForE2E(true);', stopResourceStart);
        assert.ok(resourceLifecycleSuppressionStart >= 0);
        const resourceLifecycleCommands = treeActions.slice(resourceLifecycleSuppressionStart, executeResourceCommandStart);

        assert.ok(resourceLifecycleCommands.includes('await setTerminalCommandExecutionSuppressedForE2E(true);'));
        assert.ok(resourceLifecycleCommands.includes('await setTerminalCommandExecutionSuppressedForE2E(false);'));
        assert.ok(!resourceLifecycleCommands.includes("['Stopped', 'Finished', 'Exited']"));
    });
});
