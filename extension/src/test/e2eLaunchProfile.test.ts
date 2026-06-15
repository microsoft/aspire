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
        const extension = fs.readFileSync(path.join(extensionRoot, 'src', 'extension.ts'), 'utf8');
        const openWorkspaceCase = extension.slice(extension.indexOf("case 'openWorkspaceFolder'"), extension.indexOf("case 'getWorkspaceFolders'"));
        const clearControlFileIndex = openWorkspaceCase.indexOf('clearPendingE2eControlFile();');
        const openFolderIndex = openWorkspaceCase.indexOf("vscode.commands.executeCommand('vscode.openFolder'");

        assert.ok(apiTypes.includes("{ name: 'openWorkspaceFolder'; folderPath: string }"));
        assert.ok(clearControlFileIndex >= 0);
        assert.ok(openFolderIndex > clearControlFileIndex);
    });

    test('validates explicit workspace folder before reporting bridge command start', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const extension = fs.readFileSync(path.join(extensionRoot, 'src', 'extension.ts'), 'utf8');
        const openWorkspaceCase = extension.slice(extension.indexOf("case 'openWorkspaceFolder'"), extension.indexOf("case 'getWorkspaceFolders'"));

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
        assert.ok(aspireCliEnvironment.includes("DOTNET_CLI_TELEMETRY_OPTOUT: '1'"));
        assert.ok(envConstruction.includes('const extestEnv = getAspireCliEnvironment({'));
        assert.ok(envConstruction.includes("ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE: 'true'"));
        assert.ok(runTests.includes('runWithProcessTreeTimeout(process.execPath'));
        assert.ok(runTests.includes('extestEnv'));
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
        assert.ok(zeroToRunning.includes('waitForEditorTitle(dashboardHost, 180000'));
        assert.ok(zeroToRunning.includes("process.platform === 'linux'"));
        assert.ok(zeroToRunning.includes("waitForWorkbenchTextAfterIntegratedBrowserNavigation(['Resources', dashboardHost], 180000)"));
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
        const extension = fs.readFileSync(path.join(extensionRoot, 'src', 'extension.ts'), 'utf8');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');

        assert.ok(apiTypes.includes('sequence: number;'));
        assert.ok(extension.includes('commandInvocationSequence'));
        assert.ok(extension.includes('terminalCommandSequence'));
        assert.ok(extension.includes('debugLaunchSequence'));
        assert.ok(assertions.includes('event.sequence > afterInvocationSequence'));
        assert.ok(!assertions.includes('.slice(afterInvocationCount)'));
        assert.ok(!assertions.includes('.slice(afterCommandCount)'));
        assert.ok(!assertions.includes('.slice(afterLaunchCount)'));
    });
});
