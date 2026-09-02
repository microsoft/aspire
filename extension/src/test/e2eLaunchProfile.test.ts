import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { spawnSync } from 'child_process';
import * as ts from 'typescript';

function removeDirectorySafely(directory: string): void {
    try {
        fs.rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    }
    catch (error) {
        console.warn(`Failed to remove test directory '${directory}': ${error instanceof Error ? error.message : String(error)}`);
    }
}

function readSourcePattern(source: string, name: string): RegExp {
    const declaration = new RegExp(`const ${name} = /(.+)/;`).exec(source);
    assert.ok(declaration, `run-e2e.js must define ${name}`);
    return new RegExp(declaration[1]);
}

/**
 * Removes block and line comments so a statement-level assertion is not satisfied or defeated by
 * prose. The comments in `run-e2e.js` discuss `throw` and `fs.` precisely because the code around
 * them must not use either.
 */
function stripComments(source: string): string {
    return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
}

function getDirectCallExpression(statement: ts.Statement, name: string): ts.CallExpression | undefined {
    if (!ts.isExpressionStatement(statement) || !ts.isCallExpression(statement.expression)) {
        return undefined;
    }

    return ts.isIdentifier(statement.expression.expression) && statement.expression.expression.text === name
        ? statement.expression
        : undefined;
}

function getLiteralText(expression: ts.Expression | undefined): string | undefined {
    return expression !== undefined && ts.isStringLiteralLike(expression)
        ? expression.text
        : undefined;
}

function getSuiteStatements(sourceFile: ts.SourceFile): readonly ts.Statement[] {
    for (const statement of sourceFile.statements) {
        const suiteCall = getDirectCallExpression(statement, 'suite');
        if (suiteCall === undefined) {
            continue;
        }

        const callback = suiteCall.arguments[1];
        if (callback !== undefined && (ts.isFunctionExpression(callback) || ts.isArrowFunction(callback)) && ts.isBlock(callback.body)) {
            return callback.body.statements;
        }
    }

    return [];
}

function compareVersionStrings(left: string, right: string): number {
    const leftParts = left.split('.').map(Number);
    const rightParts = right.split('.').map(Number);

    for (let i = 0; i < Math.max(leftParts.length, rightParts.length); i++) {
        const difference = (leftParts[i] ?? 0) - (rightParts[i] ?? 0);
        if (difference !== 0) {
            return difference;
        }
    }

    return 0;
}

function runE2eRunnerAsPlatform(extensionRoot: string, platform: 'darwin' | 'linux' | 'win32', environment: NodeJS.ProcessEnv) {
    const runnerPath = path.join(extensionRoot, 'scripts', 'run-e2e.js');
    const bootstrap = `Object.defineProperty(process, 'platform', { value: ${JSON.stringify(platform)} }); require(${JSON.stringify(runnerPath)});`;
    return spawnSync(process.execPath, ['-e', bootstrap], {
        encoding: 'utf8',
        timeout: 120000,
        env: environment,
    });
}

function createE2eSpecFixtures(extensionRoot: string, fileNames: readonly string[]): string {
    const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
    fs.mkdirSync(testArtifactsRoot, { recursive: true });
    const fixtureRoot = fs.mkdtempSync(path.join(testArtifactsRoot, 'e2e-spec-fixtures-'));
    for (const fileName of fileNames) {
        fs.writeFileSync(path.join(fixtureRoot, fileName), '');
    }

    return fixtureRoot;
}

function toPosixRelativePath(from: string, to: string): string {
    return path.relative(from, to).split(path.sep).join('/');
}

function getTestBlock(source: string, testName: string): string {
    const sourceFile = ts.createSourceFile('e2e.test.ts', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const suiteStatements = getSuiteStatements(sourceFile);

    // Walk the parsed suite body instead of scanning raw text so comments, nested template
    // literals, and regular expressions like `/\)/` cannot masquerade as structure.
    for (const statement of suiteStatements) {
        const testCall = getDirectCallExpression(statement, 'test');
        if (testCall !== undefined && getLiteralText(testCall.arguments[0]) === testName) {
            return source.slice(statement.getStart(sourceFile), statement.getEnd());
        }
    }

    assert.fail(`Expected to find test '${testName}'.`);
}

suite('E2E launch profile', () => {
    test('finds test blocks when the test name uses double quotes', () => {
        const source = [
            "suite('Aspire debug dashboard E2E', function () {",
            '  test("keeps long-running CLI run status out of notifications", async () => {',
            '    await waitForAnyWorkbenchText(cliRunStatusTexts, 120000);',
            '    const notificationMessages = await getNotificationMessages();',
            '  });',
            "  test('another test', async () => {",
            "    throw new Error('should not be included');",
            '  });',
            '});',
        ].join('\n');

        const block = getTestBlock(source, 'keeps long-running CLI run status out of notifications');

        assert.ok(block.includes('waitForAnyWorkbenchText(cliRunStatusTexts, 120000);'));
        assert.ok(block.includes('const notificationMessages = await getNotificationMessages();'));
        assert.ok(!block.includes("test('another test'"));
    });

    test('finds test blocks when whitespace changes around the test declaration', () => {
        const source = [
            "suite('Aspire debug dashboard E2E', function () {",
            "\ttest ( 'keeps long-running CLI run status out of notifications' , async () => {",
            '        const observedStatuses = cliRunStatusTexts.map(status => `(${status})`);',
            "        assert.ok(observedStatuses.some(status => status.includes('Building AppHost...')));",
            '    });',
            "\ttest('another test', async () => {",
            '        assert.fail("should not be included");',
            '    });',
            '});',
        ].join('\n');

        const block = getTestBlock(source, 'keeps long-running CLI run status out of notifications');

        assert.ok(block.includes('const observedStatuses = cliRunStatusTexts.map(status => `(${status})`);'));
        assert.ok(block.includes("assert.ok(observedStatuses.some(status => status.includes('Building AppHost...')));"));
        assert.ok(!block.includes("assert.fail(\"should not be included\");"));
    });

    test('finds test blocks when the body contains a regex literal with a closing parenthesis', () => {
        const source = [
            "suite('Aspire debug dashboard E2E', function () {",
            "  test('keeps long-running CLI run status out of notifications', async () => {",
            "    assert.ok(/\\)/.test(')'));",
            '    const notificationMessages = await getNotificationMessages();',
            '  });',
            '});',
        ].join('\n');

        const block = getTestBlock(source, 'keeps long-running CLI run status out of notifications');

        assert.ok(block.includes("assert.ok(/\\)/.test(')'));"));
        assert.ok(block.includes('const notificationMessages = await getNotificationMessages();'));
    });

    test('finds test blocks when the body contains nested template literals with closing parentheses', () => {
        const source = [
            "suite('Aspire debug dashboard E2E', function () {",
            "  test('keeps long-running CLI run status out of notifications', async () => {",
            "    const shape = `outer ${`inner )`}`;",
            "    assert.ok(shape.includes('inner )'));",
            '  });',
            '});',
        ].join('\n');

        const block = getTestBlock(source, 'keeps long-running CLI run status out of notifications');

        assert.ok(block.includes("const shape = `outer ${`inner )`}`;"));
        assert.ok(block.includes("assert.ok(shape.includes('inner )'));"));
    });

    test('ignores block-commented target declarations', () => {
        const source = [
            "suite('Aspire debug dashboard E2E', function () {",
            '  /*',
            "  test('keeps long-running CLI run status out of notifications', async () => {",
            "    throw new Error('comment only');",
            '  });',
            '  */',
            "  test('another test', async () => {",
            '    assert.ok(true);',
            '  });',
            '});',
        ].join('\n');

        assert.throws(
            () => getTestBlock(source, 'keeps long-running CLI run status out of notifications'),
            /Expected to find test 'keeps long-running CLI run status out of notifications'\./);
    });

    test('creates nothing in the per-run root that a later module-scope throw could strand', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const runRootDeclaration = runner.indexOf('const shortRunRoot =');
        const moduleScopeAfterRunRoot = stripComments(runner.slice(runner.indexOf('\n', runRootDeclaration), runner.indexOf('\nfunction ')));

        // Module scope runs outside the cleanup `finally` that `main()` installs, so anything
        // between `mkdtempSync` and the first function declaration that can throw leaves an
        // `aev-*` directory behind with no owner. Everything here must be string joining.
        assert.ok(runRootDeclaration >= 0);
        assert.ok(!/\bfs\./.test(moduleScopeAfterRunRoot), 'module scope must not touch the filesystem after the run root exists');
        assert.ok(!/\bthrow\b/.test(moduleScopeAfterRunRoot), 'module scope must not throw after the run root exists');
        assert.ok(!moduleScopeAfterRunRoot.includes('removePath('), 'module scope must not remove paths after the run root exists');

        // The validations that reject the environment, and the spec walk, have to come first.
        assert.ok(runner.indexOf('const matchedTestSpecs =') < runRootDeclaration);
        assertTextOrder(runner, 'const javaStarterTestSpecs =', 'const shortRunRoot =');
        assertTextOrder(runner, 'Java E2E spec selection mixes workspace fixtures', 'const shortRunRoot =');
        assertTextOrder(runner, 'mixes Java and non-Java workspace fixtures', 'const shortRunRoot =');
        assert.ok(runner.indexOf("throw new Error('vscode-extension-tester must be pinned") < runRootDeclaration);
        assert.ok(runner.indexOf('const downloadCacheRoot =') < runRootDeclaration);
        assert.ok(runner.indexOf('const vscodeVersion = resolveCachedVsCodeVersion(') < runRootDeclaration);

        // The directory preparation that used to sit at module scope is now called from `main()`,
        // inside the `try` whose `finally` tears the run root down.
        const mainStart = runner.indexOf('async function main()');
        const mainBody = runner.slice(mainStart, runner.indexOf('\n  finally {', mainStart));
        assert.ok(mainBody.includes('prepareRunDirectories();'));
    });

    test('rejects mixed Java workspace fixtures before creating the per-run root even when Java E2E is explicitly disabled', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
        fs.mkdirSync(testArtifactsRoot, { recursive: true });
        const fixtureRoot = createE2eSpecFixtures(extensionRoot, [
            'javaAppHost.e2e.test.js',
            'javaStarterProjectModel.e2e.test.js',
        ]);
        const tempRoot = fs.mkdtempSync(path.join(testArtifactsRoot, 'aev-java-fixture-guard-'));
        try {
            const result = runE2eRunnerAsPlatform(extensionRoot, 'linux', {
                ...process.env,
                ASPIRE_EXTENSION_E2E_ENABLE_JAVA: 'false',
                ASPIRE_EXTENSION_E2E_SPEC: path.join(fixtureRoot, '{javaAppHost,javaStarterProjectModel}.e2e.test.js'),
                ASPIRE_EXTENSION_E2E_TEMP_ROOT: tempRoot,
                ASPIRE_EXTENSION_E2E_VSCODE_VERSION: '1.130.0',
            });

            const starterSpec = toPosixRelativePath(extensionRoot, path.join(fixtureRoot, 'javaStarterProjectModel.e2e.test.js'));
            const playgroundSpec = toPosixRelativePath(extensionRoot, path.join(fixtureRoot, 'javaAppHost.e2e.test.js'));
            assert.notStrictEqual(result.status, 0);
            assert.ok(
                result.stderr.includes(`Java E2E spec selection mixes workspace fixtures. Starter matches: ${starterSpec}. Playground matches: ${playgroundSpec}. Split these specs into separate runs.`),
                result.stderr);
            assert.deepStrictEqual(fs.readdirSync(tempRoot), []);
        }
        finally {
            removeDirectorySafely(tempRoot);
            removeDirectorySafely(fixtureRoot);
        }
    });

    test('rejects mixed Java and non-Java specs before creating the per-run root', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
        fs.mkdirSync(testArtifactsRoot, { recursive: true });
        const fixtureRoot = createE2eSpecFixtures(extensionRoot, [
            'javaAppHost.e2e.test.js',
            'packageSurface.e2e.test.js',
        ]);
        const tempRoot = fs.mkdtempSync(path.join(testArtifactsRoot, 'aev-java-non-java-guard-'));
        try {
            const result = runE2eRunnerAsPlatform(extensionRoot, 'linux', {
                ...process.env,
                ASPIRE_EXTENSION_E2E_CLI_PATH: path.join(tempRoot, 'missing-aspire'),
                ASPIRE_EXTENSION_E2E_ENABLE_JAVA: '',
                ASPIRE_EXTENSION_E2E_SPEC: path.join(fixtureRoot, '{javaAppHost,packageSurface}.e2e.test.js'),
                ASPIRE_EXTENSION_E2E_TEMP_ROOT: tempRoot,
                ASPIRE_EXTENSION_E2E_VSCODE_VERSION: '1.130.0',
            });

            const javaSpec = toPosixRelativePath(extensionRoot, path.join(fixtureRoot, 'javaAppHost.e2e.test.js'));
            const nonJavaSpec = toPosixRelativePath(extensionRoot, path.join(fixtureRoot, 'packageSurface.e2e.test.js'));
            assert.notStrictEqual(result.status, 0);
            assert.ok(
                result.stderr.includes(`Java E2E spec selection mixes Java and non-Java workspace fixtures. Java matches: ${javaSpec}. Non-Java matches: ${nonJavaSpec}. Split these specs into separate runs.`),
                result.stderr);
            assert.deepStrictEqual(fs.readdirSync(tempRoot), []);
        }
        finally {
            removeDirectorySafely(tempRoot);
            removeDirectorySafely(fixtureRoot);
        }
    });

    test('removes the per-run root when the environment is rejected before any download', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const tempRoot = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'aev-guard-'));
        try {
            // `latest` is rejected by resolveCachedVsCodeVersion because no cache key can
            // invalidate it. The rejection has to happen before `mkdtempSync`, otherwise this
            // temporary root is left holding an orphaned `aev-*` directory forever.
            const result = spawnSync(process.execPath, [path.join(extensionRoot, 'scripts', 'run-e2e.js')], {
                encoding: 'utf8',
                timeout: 120000,
                env: {
                    ...process.env,
                    ASPIRE_EXTENSION_E2E_SPEC: 'out/test/e2eLaunchProfile.test.js',
                    ASPIRE_EXTENSION_E2E_TEMP_ROOT: tempRoot,
                    ASPIRE_EXTENSION_E2E_VSCODE_VERSION: 'latest',
                },
            });

            assert.notStrictEqual(result.status, 0);
            assert.match(result.stderr, /latest/);
            assert.deepStrictEqual(fs.readdirSync(tempRoot), []);
        }
        finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('rejects VS Code overrides whose macOS executable the pinned ExTester cannot launch', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
        fs.mkdirSync(testArtifactsRoot, { recursive: true });
        const tempRoot = fs.mkdtempSync(path.join(testArtifactsRoot, 'aev-version-guard-'));
        try {
            const result = runE2eRunnerAsPlatform(extensionRoot, 'darwin', {
                ...process.env,
                ASPIRE_EXTENSION_E2E_CLI_PATH: path.join(tempRoot, 'missing-aspire'),
                ASPIRE_EXTENSION_E2E_SPEC: 'out/test/e2eLaunchProfile.test.js',
                ASPIRE_EXTENSION_E2E_TEMP_ROOT: tempRoot,
                ASPIRE_EXTENSION_E2E_VSCODE_VERSION: '1.131.0',
            });

            assert.notStrictEqual(result.status, 0);
            assert.match(result.stderr, /VS Code 1\.131\.0.*ExTester 8\.23\.0 on macOS.*Contents\/MacOS\/Electron/);
            assert.deepStrictEqual(fs.readdirSync(tempRoot), []);
        }
        finally {
            removeDirectorySafely(tempRoot);
        }
    });

    test('allows VS Code 1.131 overrides where ExTester uses the current executable path', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
        fs.mkdirSync(testArtifactsRoot, { recursive: true });

        for (const platform of ['linux', 'win32'] as const) {
            const tempRoot = fs.mkdtempSync(path.join(testArtifactsRoot, `aev-version-${platform}-`));
            try {
                const missingCliPath = path.join(tempRoot, 'missing-aspire');
                const result = runE2eRunnerAsPlatform(extensionRoot, platform, {
                    ...process.env,
                    ASPIRE_EXTENSION_E2E_CLI_PATH: missingCliPath,
                    ASPIRE_EXTENSION_E2E_SPEC: 'out/test/e2eLaunchProfile.test.js',
                    ASPIRE_EXTENSION_E2E_TEMP_ROOT: tempRoot,
                    ASPIRE_EXTENSION_E2E_VSCODE_VERSION: '1.131.0',
                });

                assert.notStrictEqual(result.status, 0);
                assert.ok(result.stderr.includes(`ASPIRE_EXTENSION_E2E_CLI_PATH points to a missing file: ${missingCliPath}`), result.stderr);
                assert.deepStrictEqual(fs.readdirSync(tempRoot), []);
            }
            finally {
                removeDirectorySafely(tempRoot);
            }
        }
    });

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

    test('reloads outside the extension host and waits for the fresh workbench', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const vscodeHelpers = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'vscode.ts'), 'utf8');
        const previousSessionIndex = fixtures.indexOf('const previousExtensionHostSessionId = readStateFile().extensionHostSessionId;');
        const clearControlFileIndex = fixtures.indexOf('fs.rmSync(controlFilePath, { force: true });');
        const reloadWindowIndex = fixtures.indexOf('await reloadWindow();');
        const waitForSessionIndex = fixtures.indexOf('file => file.extensionHostSessionId !== previousExtensionHostSessionId');
        const waitForWorkbenchIndex = fixtures.indexOf('VSBrowser.instance.waitForWorkbench');

        assert.ok(apiTypes.includes('extensionHostSessionId: string;'));
        assert.strictEqual(apiTypes.includes("{ name: 'reloadWindow' }"), false);
        assert.ok(e2eStateFileBridge.includes('const extensionHostSessionId = randomUUID();'));
        assert.ok(e2eStateFileBridge.includes('extensionHostSessionId,'));
        assert.strictEqual(e2eStateFileBridge.includes("case 'reloadWindow'"), false);
        assert.ok(vscodeHelpers.includes("new Workbench().executeCommand('Developer: Reload Window')"));
        assert.ok(previousSessionIndex >= 0);
        assert.ok(clearControlFileIndex > previousSessionIndex);
        assert.ok(reloadWindowIndex > clearControlFileIndex);
        assert.ok(waitForSessionIndex > reloadWindowIndex);
        assert.ok(waitForWorkbenchIndex > waitForSessionIndex);
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

        assert.ok(runner.includes("const { runWithProcessTreeTimeout } = require('./e2e-process-runner.cjs');"));
        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS'));
        assert.ok(runner.includes('await runWithProcessTreeTimeout(process.execPath'));
        assert.ok(runner.includes('getRunTestsTimeoutMs()'));
        assert.ok(runner.includes('2400000'));
        assert.ok(runner.includes("spawnSync('taskkill'"));
        assert.ok(runner.includes('process.kill(-pid, signal)'));
    });

    test('wires the ExTester process lifecycle into the extracted runner', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const invocationStart = runner.indexOf('await runWithProcessTreeTimeout(process.execPath');
        const invocationEnd = runner.indexOf('\n      });', invocationStart);
        const runnerOptions = runner.slice(invocationStart, invocationEnd);

        assert.ok(invocationStart >= 0);
        assert.ok(invocationEnd > invocationStart);
        assert.ok(runnerOptions.includes('quoteShellArgument: quoteWindowsShellArgument,'));
        assert.ok(runnerOptions.includes("stdio: 'inherit',"));
        assert.ok(runnerOptions.includes("detached: process.platform !== 'win32',"));
        assert.ok(runnerOptions.includes('terminateProcessTree,'));
    });

    test('bounds retryable runner setup steps so setup failures still collect diagnostics', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes("'get-vscode'"));
        assert.ok(runner.includes("ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_RETRY_ATTEMPTS', 5"));
        assert.ok(runner.includes("ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_RETRY_DELAY_MS', 15000"));
        assert.ok(runner.includes("ASPIRE_EXTENSION_E2E_SETUP_DOWNLOAD_TIMEOUT_MS', 240000"));
        assert.ok(runner.includes("'get-chromedriver'"));
        assert.ok(runner.includes('const setupDownloadRetryOptions = getSetupDownloadRetryOptions(stagingDirectory, downloadDirectory);'));
        assert.ok(runner.includes('runWithRetries(() => run(command, args, extraEnv, options), {'));
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
        assert.strictEqual(packageJson.resolutions.undici, '7.29.0');
        assert.ok(lockfile.includes('vscode-extension-tester@8.23.0'));
        assert.ok(lockfile.includes('undici@7.29.0'));
        assert.ok(lockfile.split(/\r?\n/).filter(l => /^\s*resolved\s+"/.test(l)).every(l => l.includes(internalFeed)));
        assert.ok(workflow.includes('NPM_REGISTRY: https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/'));
        assert.ok(fs.existsSync(path.join(extensionRoot, 'scripts', 'validate-lockfile-registry.cjs')));
        assert.ok(workflow.includes('run: node scripts/validate-lockfile-registry.cjs'));
        assert.ok(workflow.includes('corepack yarn install --frozen-lockfile --non-interactive'));
        assert.ok(!workflow.includes('ASPIRE_EXTENSION_E2E_EXTESTER_NPM_REGISTRY'));
        assert.ok(!workflow.includes('registry=https://'));
    });

    test('defaults to the newest VS Code with the legacy macOS executable path while the internal feed lacks newer ExTester', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const installedPackageJson = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'node_modules', 'vscode-extension-tester', 'package.json'), 'utf8'));
        const extester = require(path.join(extensionRoot, 'node_modules', 'vscode-extension-tester', 'out', 'extester.js')) as {
            loadCodeVersion(version: string): string;
        };
        const previousCodeVersion = process.env.CODE_VERSION;
        const defaultVsCodeVersion = '1.130.0';
        const macOsLegacyExecutableRemovalVersion = '1.131.0';
        const extesterMacOsExecutableFallbackVersion = '8.24.0';

        assert.ok(runner.includes(`process.env.ASPIRE_EXTENSION_E2E_VSCODE_VERSION || '${defaultVsCodeVersion}'`));
        assert.strictEqual(installedPackageJson.version, '8.23.0');
        assert.ok(compareVersionStrings(defaultVsCodeVersion, macOsLegacyExecutableRemovalVersion) < 0);
        assert.ok(compareVersionStrings(installedPackageJson.version, extesterMacOsExecutableFallbackVersion) < 0);

        try {
            delete process.env.CODE_VERSION;
            assert.strictEqual(extester.loadCodeVersion(defaultVsCodeVersion), defaultVsCodeVersion);
        }
        finally {
            if (previousCodeVersion === undefined) {
                delete process.env.CODE_VERSION;
            }
            else {
                process.env.CODE_VERSION = previousCodeVersion;
            }
        }
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

    test('pins the real Azure Functions toolchain for the offline E2E shard', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const appHostLifecycleTools = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'appHostLifecycleTools.e2e.test.ts'), 'utf8');
        const dotnetRuntimeInstallIndex = runner.indexOf("displayName: '.NET Install Tool'");
        const csharpInstallIndex = runner.indexOf("displayName: 'C#'");
        const resourceGroupsInstallIndex = runner.indexOf("displayName: 'Azure Resource Groups'");
        const functionsInstallIndex = runner.indexOf("displayName: 'Azure Functions'");
        const dotNetSetupIndex = workflow.indexOf('name: Setup .NET');
        const azureFunctionsPrerequisitesIndex = workflow.indexOf('name: Install Azure Functions E2E prerequisites');
        const runStepIndex = workflow.indexOf('- name: Run extension E2E tests');
        const uploadStepIndex = workflow.indexOf('- name: Upload E2E diagnostics');
        const runStep = workflow.slice(runStepIndex, uploadStepIndex);
        // Generated project files embed their target framework as:
        //   <TargetFramework>net10.0</TargetFramework>
        const runnerTargetFrameworks = [...runner.matchAll(/<TargetFramework>([^<]+)<\/TargetFramework>/g)].map(match => match[1]);
        const fixtureTargetFrameworks = [fixtures, appHostLifecycleTools]
            .flatMap(source => [...source.matchAll(/<TargetFramework>([^<]+)<\/TargetFramework>/g)].map(match => match[1]));

        assert.ok(workflow.includes('shardName: azure-functions'));
        assert.ok(workflow.includes('installAzureFunctions: true'));
        assert.ok(dotNetSetupIndex >= 0);
        assert.ok(dotNetSetupIndex < azureFunctionsPrerequisitesIndex);
        assert.ok(workflow.includes('global-json-file: global.json'));
        assert.deepStrictEqual(runnerTargetFrameworks, ['net10.0', 'net10.0', 'net10.0']);
        assert.deepStrictEqual(fixtureTargetFrameworks, ['net10.0', 'net10.0']);
        assert.ok(workflow.includes("core_tools_version='4.12.1'"));
        assert.ok(workflow.includes('faf8fb8d50b5293df338bec70594b12f45730e9fe251805298859b2238cf627e'));
        assert.ok(workflow.includes('vscode-dotnet-runtime/3.1.0/vspackage'));
        assert.ok(workflow.includes('8e675ffe5f3674430d63e28d2dc05ab40f36c8494e9549e79d3995d721b13f5a'));
        assert.ok(workflow.includes('csharp/2.148.23/vspackage?targetPlatform=linux-x64'));
        assert.ok(workflow.includes('18b503e614a979212762683b35a4fa1806688ba773d5fe93bf62c9f9346db23f'));
        assert.ok(workflow.includes('vscode-azureresourcegroups/0.12.7/vspackage'));
        assert.ok(workflow.includes('e4a2e7ab012de3777e1ac1781e2c25d65f150ad6f3770e8cfcc5a3d3658df35a'));
        assert.ok(workflow.includes('vscode-azurefunctions/1.22.0/vspackage'));
        assert.ok(workflow.includes('146aede06f941b07a55c5aebd28c5e3df684d57b07cf6f9ebf90d7bb8ecd41a2'));
        assert.ok(workflow.includes('ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS=true'));
        assert.ok(workflow.includes('ASPIRE_EXTENSION_E2E_DOTNET_RUNTIME_VSIX=$dotnet_runtime_vsix'));
        assert.ok(workflow.includes('ASPIRE_EXTENSION_E2E_CSHARP_VSIX=$csharp_vsix'));
        assert.ok(dotnetRuntimeInstallIndex >= 0);
        assert.ok(csharpInstallIndex > dotnetRuntimeInstallIndex);
        assert.ok(resourceGroupsInstallIndex > csharpInstallIndex);
        assert.ok(functionsInstallIndex > resourceGroupsInstallIndex);
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_DOTNET_RUNTIME_VSIX')"));
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_CSHARP_VSIX')"));
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_RESOURCE_GROUPS_VSIX')"));
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_FUNCTIONS_VSIX')"));
        assert.ok(runner.includes("const executable = isWindows ? (process.env.ComSpec || 'cmd.exe') : displayName;"));
        assert.ok(runner.includes("const args = isWindows ? ['/d', '/s', '/c', 'func.cmd --version'] : ['--version'];"));
        assert.ok(runner.includes("const certificatePassword = String.raw`Aspire E2E p@ss'\\word`;"));
        assert.ok(runner.includes('commandLineArgs: `--useHttps --cert "${certificatePath}" --password "${certificatePassword}"`'));
        assert.ok(runStep.includes('ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE: ${{ matrix.advisoryIssue }}'));
        assert.strictEqual(runStep.includes('continue-on-error:'), false);
    });

    test('wires structured E2E harness failures into advisory handling', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes("const { shouldAllowAdvisoryTestFailure } = require('./e2e-process-failure.cjs');"));
        assert.ok(runner.includes("const advisoryIssue = process.env.ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE || '';"));
        assert.ok(runner.includes('let cleanupFailed = false;'));
        assert.ok(runner.includes('cleanupFailed = true;'));
        assert.ok(runner.includes('shouldAllowAdvisoryTestFailure(testFailure, readMochaResults(), cleanupFailed)'));
        assert.ok(runner.includes('completed test failures tracked by ${advisoryIssue}. Diagnostics were uploaded for investigation.'));
        assert.strictEqual(runner.includes('ASPIRE_EXTENSION_E2E_ALLOW_TEST_FAILURE'), false);
        assert.strictEqual(runner.includes('completedTests'), false);
    });

    test('reloads the generated Azure Functions workspace before reopening the Aspire view', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const spec = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'azureFunctions.e2e.test.ts'), 'utf8');
        const firstOpenIndex = spec.indexOf('await openAspireView();');
        const initialWorkspaceLoadedIndex = spec.indexOf('await waitForWorkspaceAppHost();');
        const reloadWorkspaceIndex = spec.indexOf('await reloadWorkspaceForE2E();');
        const reloadedWorkspaceIndex = spec.indexOf('await waitForWorkspaceAppHost();', initialWorkspaceLoadedIndex + 1);
        const reopenedViewIndex = spec.indexOf('await openAspireView();', firstOpenIndex + 1);
        const runAppHostIndex = spec.indexOf("name: 'runAppHost'");

        assert.ok(firstOpenIndex >= 0);
        assert.ok(initialWorkspaceLoadedIndex > firstOpenIndex);
        assert.ok(reloadWorkspaceIndex > initialWorkspaceLoadedIndex);
        assert.ok(reloadedWorkspaceIndex > reloadWorkspaceIndex);
        assert.ok(reopenedViewIndex > reloadedWorkspaceIndex);
        assert.ok(runAppHostIndex > reopenedViewIndex);
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

    test('persists E2E NuGet packages while purging repo-built Aspire packages', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const nuGetPathDeclaration = "const e2eNuGetPackages = path.join(downloadCacheRoot, 'nuget-packages', shardName);";
        const nuGetPathIndex = runner.indexOf(nuGetPathDeclaration);
        const restoreWorkspaceIndex = runner.indexOf('restoreWorkspaceFixture();');
        const prepareCacheStart = runner.indexOf('function prepareNuGetPackageCache()');
        const prepareCacheEnd = runner.indexOf('\nfunction ', prepareCacheStart + 1);
        const prepareCache = runner.slice(prepareCacheStart, prepareCacheEnd);
        const mainStart = runner.indexOf('async function main()');
        const mainBody = runner.slice(mainStart, runner.indexOf('\n  finally {', mainStart));
        const cliEnvironmentStart = runner.indexOf('function getAspireCliEnvironment');
        const cliEnvironmentEnd = runner.indexOf('\nfunction ', cliEnvironmentStart + 1);
        const cliEnvironment = runner.slice(cliEnvironmentStart, cliEnvironmentEnd);
        const extestEnvironmentStart = runner.indexOf('const extestEnv = getAspireCliEnvironment({');
        const extestEnvironmentEnd = runner.indexOf('\n    });', extestEnvironmentStart);
        const extestEnvironment = runner.slice(extestEnvironmentStart, extestEnvironmentEnd);
        const javaSdkGenerationStart = runner.indexOf('function ensureJavaAppHostSdkGenerated');
        const javaSdkGenerationEnd = runner.indexOf('\nfunction ', javaSdkGenerationStart + 1);
        const javaSdkGeneration = runner.slice(javaSdkGenerationStart, javaSdkGenerationEnd);
        const javaRestoreStart = javaSdkGeneration.indexOf("spawnSync(bundledCliPath, ['restore'], {");
        const javaRestoreEnd = javaSdkGeneration.indexOf('\n  });', javaRestoreStart);
        const javaRestore = javaSdkGeneration.slice(javaRestoreStart, javaRestoreEnd);

        assert.ok(nuGetPathIndex >= 0);
        assert.ok(nuGetPathIndex < restoreWorkspaceIndex);
        assert.ok(prepareCacheStart >= 0);
        assert.ok(prepareCacheEnd > prepareCacheStart);
        assert.ok(prepareCache.includes("fs.mkdirSync(e2eNuGetPackages, { recursive: true });"));
        assert.ok(prepareCache.includes("fs.readdirSync(e2eNuGetPackages, { withFileTypes: true })"));
        assert.ok(prepareCache.includes("/^aspire(?:\\.|$)/i.test(entry.name)"));
        assert.ok(prepareCache.includes("removePathWithoutFollowingLinks(path.join(e2eNuGetPackages, entry.name), { recursive: true, force: true });"));
        assert.ok(mainBody.includes('prepareNuGetPackageCache();'));
        assert.ok(cliEnvironment.includes('NUGET_PACKAGES: e2eNuGetPackages,'));
        assert.ok(extestEnvironment.includes('NUGET_PACKAGES: e2eNuGetPackages,'));
        assert.ok(extestEnvironment.includes('ASPIRE_EXTENSION_E2E_NUGET_PACKAGES: e2eNuGetPackages,'));
        assert.ok(javaRestoreStart >= 0);
        assert.ok(javaRestoreEnd > javaRestoreStart);
        assert.ok(javaRestore.includes('env: getAspireCliEnvironment(),'));
    });

    test('makes local NuGet sources available to external AppHosts', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const writeConfigStart = runner.indexOf('function writeNuGetConfigIfLocalPackageSourcesExist');
        const writeConfigEnd = runner.indexOf('\nfunction ', writeConfigStart + 1);
        const writeConfig = runner.slice(writeConfigStart, writeConfigEnd);

        assert.ok(runner.includes("const runRootNuGetConfigPath = path.join(shortRunRoot, 'NuGet.config');"));
        assert.ok(writeConfig.includes('fs.writeFileSync(runRootNuGetConfigPath, nugetConfig);'));
        assert.ok(writeConfig.includes('fs.writeFileSync(workspaceNuGetConfigPath, nugetConfig);'));
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

    test('does not seed dashboard launch preferences in the E2E harness', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const settings = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'test-e2e', 'settings.json'), 'utf8'));

        assert.strictEqual(settings['aspire.dashboardBrowser'], undefined);
        assert.strictEqual(settings['aspire.enableAspireDashboardAutoLaunch'], undefined);
        assert.ok(!runner.includes("'aspire.dashboardBrowser':"));
        assert.ok(!runner.includes("'aspire.enableAspireDashboardAutoLaunch':"));
    });

    test('resets the dashboard default notification key for E2E dashboard launch coverage', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const debugDashboard = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'debugDashboard.e2e.test.ts'), 'utf8');

        assert.ok(apiTypes.includes('resetDashboardDefaultChangedNotification?: boolean;'));
        assert.ok(e2eStateFileBridge.includes("import { dashboardDefaultChangedNotificationKey } from '../utils/dashboardNotificationState';"));
        assert.ok(e2eStateFileBridge.includes("context.globalState.update(dashboardDefaultChangedNotificationKey, undefined)"));
        assert.ok(fixtures.includes('resetDashboardDefaultChangedNotificationForE2E'));
        assert.ok(debugDashboard.includes('await resetDashboardDefaultChangedNotificationForE2E();'));
    });

    test('scopes real debug browser cleanup to Windows while retaining deterministic nonblocking coverage', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const debugDashboard = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'debugDashboard.e2e.test.ts'), 'utf8');
        const vscodeHelpers = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'vscode.ts'), 'utf8');
        const aspireDebugSessionTests = fs.readFileSync(path.join(extensionRoot, 'src', 'test', 'aspireDebugSession.test.ts'), 'utf8');
        const debugBrowserTest = getTestBlock(debugDashboard, 'starts the AppHost without waiting for the dashboard debug browser and closes the browser on Windows');
        const normalizedDebugBrowserTest = debugBrowserTest.replace(/\r\n/g, '\n');
        const nonblockingUnitTest = getTestBlock(aspireDebugSessionTests, 'openDashboard does not wait for a dashboard debug session to start');
        const nonWindowsSkip = `if (process.platform !== 'win32') {
            this.skip();
        }`;

        assert.deepStrictEqual({
            nonWindowsSkipPrecedesDashboardBrowserSetting: normalizedDebugBrowserTest.indexOf(nonWindowsSkip) >= 0
                && normalizedDebugBrowserTest.indexOf(nonWindowsSkip) < normalizedDebugBrowserTest.indexOf("writeWorkspaceSetting('aspire.dashboardBrowser', 'debugChrome')"),
            waitsForBrowserDebugSession: debugBrowserTest.includes('await waitForBrowserDebugSession()'),
            waitsForNoBrowserDebugSessions: debugBrowserTest.includes('await waitForNoBrowserDebugSessions()'),
            hasNonblockingUnitTest: aspireDebugSessionTests.includes("test('openDashboard does not wait for a dashboard debug session to start'"),
            usesNeverSettlingBrowserLaunch: nonblockingUnitTest.includes('new Promise<boolean>(() => { })'),
            removedOptionalModalHelper: !vscodeHelpers.includes('export async function dismissModalDialogIfPresent('),
        }, {
            nonWindowsSkipPrecedesDashboardBrowserSetting: true,
            waitsForBrowserDebugSession: true,
            waitsForNoBrowserDebugSessions: true,
            hasNonblockingUnitTest: true,
            usesNeverSettlingBrowserLaunch: true,
            removedOptionalModalHelper: true,
        });
    });

    test('keeps CLI status surface coverage in the deterministic ProgressNotifier unit test', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const debugDashboard = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'debugDashboard.e2e.test.ts'), 'utf8');
        const progressNotifierTests = fs.readFileSync(path.join(extensionRoot, 'src', 'test', 'progressNotifier.test.ts'), 'utf8');
        const statusSurfaceTest = getTestBlock(progressNotifierTests, 'CLI status is reported as dismissible window progress rather than a notification');

        assert.ok(statusSurfaceTest.includes('vscode.ProgressLocation.Window'));
        assert.ok(statusSurfaceTest.includes('vscode.ProgressLocation.Notification'));
        assert.ok(
            !debugDashboard.includes("test('keeps long-running CLI run status out of notifications'"),
            'Workbench-wide text includes persistent Debug Console output, so it cannot prove status-bar progress is active.');
    });

    test('uses known AppHost PID when E2E teardown CLI status probes time out', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const stopTimeoutCase = fixtures.slice(fixtures.indexOf("if (/timed out|Failed to stop/i.test(stopError.message))"), fixtures.indexOf('const runningAppHost = await getRunningAppHostAccordingToCli(appHostPath);'));
        const waitFallbackStart = fixtures.indexOf('catch (cliError)');
        const waitFallback = fixtures.slice(waitFallbackStart, fixtures.indexOf('if (!runningAppHost)', waitFallbackStart));

        assert.ok(fixtures.includes("import { ProcessError, runProcess } from './process';"));
        assert.ok(stopTimeoutCase.includes('runningAppHostBeforeStop?.appHostPid !== undefined'));
        assert.ok(stopTimeoutCase.includes('waitForNoRunningAppHostPathOrStopKnownProcess(appHostPath, 30000, runningAppHostBeforeStop.appHostPid'));
        assert.ok(waitFallback.includes('isProcessTimeoutError(cliError)'));
        assert.ok(waitFallback.includes('knownAppHostPid === undefined'));
        assert.ok(waitFallback.includes('runningAppHostFromState?.appHostPid !== knownAppHostPid'));
        assert.ok(waitFallback.includes('isKnownAppHostProcess(knownAppHostPid, appHostPath)'));
        assert.ok(waitFallback.includes('await stopProcess(knownAppHostPid, 30000);'));
    });

    test('latches E2E control command start before command completion can overwrite the state file', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');

        assert.ok(apiTypes.includes('startedObserved?: boolean;'));
        assert.ok(e2eStateFileBridge.includes("controlStatus = { revision, status: 'started', startedObserved: true };"));
        assert.ok(e2eStateFileBridge.includes("controlStatus = { revision, status: 'applied', startedObserved: commandStarted, result };"));
        assert.ok(assertions.includes("waitFor === 'applied' ? file.control.status === 'applied' : file.control.startedObserved === true"));
    });

    test('keeps E2E clipboard snapshots out of diagnostic state and control files', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const appHostTreeE2E = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'appHostTree.e2e.test.ts'), 'utf8');
        const treeActionsE2E = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'treeActions.e2e.test.ts'), 'utf8');

        assert.ok(apiTypes.includes("{ name: 'snapshotClipboard' }"));
        assert.ok(apiTypes.includes("{ name: 'restoreClipboardSnapshot' }"));
        assert.ok(apiTypes.includes("{ name: 'captureWorkspaceAppHostPathClipboardExpectation' }"));
        assert.ok(apiTypes.includes("{ name: 'assertClipboardMatchesLastExpectation' }"));
        assert.ok(!apiTypes.includes("{ name: 'readClipboard' }"));
        assert.ok(!apiTypes.includes("{ name: 'writeClipboard'; text: string }"));

        assert.ok(e2eStateFileBridge.includes("case 'snapshotClipboard':"));
        assert.ok(e2eStateFileBridge.includes("case 'restoreClipboardSnapshot':"));
        assert.ok(e2eStateFileBridge.includes("case 'captureWorkspaceAppHostPathClipboardExpectation':"));
        assert.ok(e2eStateFileBridge.includes("case 'assertClipboardMatchesLastExpectation':"));
        assert.ok(!e2eStateFileBridge.includes('return await vscode.env.clipboard.readText();'));
        assert.ok(!e2eStateFileBridge.includes('await vscode.env.clipboard.writeText(command.text);'));

        assert.ok(fixtures.includes('snapshotClipboardForE2E'));
        assert.ok(fixtures.includes('restoreClipboardSnapshotForE2E'));
        assert.ok(fixtures.includes('captureWorkspaceAppHostPathClipboardExpectationForE2E'));
        assert.ok(fixtures.includes('assertClipboardMatchesLastExpectationForE2E'));
        assert.ok(!fixtures.includes('readClipboardForE2E'));
        assert.ok(!fixtures.includes('writeClipboardForE2E'));

        assert.ok(appHostTreeE2E.includes('snapshotClipboardForE2E'));
        assert.ok(appHostTreeE2E.includes('restoreClipboardSnapshotForE2E'));
        assert.ok(appHostTreeE2E.includes('await captureWorkspaceAppHostPathClipboardExpectationForE2E();'));
        assert.ok(appHostTreeE2E.includes('await assertClipboardMatchesLastExpectationForE2E();'));
        assert.ok(!appHostTreeE2E.includes('clipboardTextToRestore'));

        assert.ok(treeActionsE2E.includes('snapshotClipboardForE2E'));
        assert.ok(treeActionsE2E.includes('restoreClipboardSnapshotForE2E'));
        assertTextOrder(treeActionsE2E, '() => restoreClipboardSnapshotForE2E()', '() => setCliUnavailableForE2E(false)');
        assertTextOrder(treeActionsE2E, 'await snapshotClipboardForE2E();', "await executeE2eControlCommand({ name: 'copyAppHostPath'");
    });

    test('keeps copied values out of E2E control command results', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const treeActionsE2E = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'treeActions.e2e.test.ts'), 'utf8');

        const copyAppHostPathCase = getSwitchCase(e2eStateFileBridge, 'copyAppHostPath', 'viewAppHostLogFile');
        const copyLogFilePathCase = getSwitchCase(e2eStateFileBridge, 'copyLogFilePath', 'viewResourceLogs');
        const copyResourceNameCase = getSwitchCase(e2eStateFileBridge, 'copyResourceName', 'copyEndpointUrl');
        const copyEndpointUrlCase = getSwitchCase(e2eStateFileBridge, 'copyEndpointUrl', 'openInIntegratedBrowser');

        assert.ok(copyAppHostPathCase.includes("vscode.commands.executeCommand('aspire-vscode.copyAppHostPath'"));
        assert.ok(copyLogFilePathCase.includes("vscode.commands.executeCommand('aspire-vscode.copyLogFilePath'"));
        assert.ok(copyResourceNameCase.includes("vscode.commands.executeCommand('aspire-vscode.copyResourceName'"));
        assert.ok(copyEndpointUrlCase.includes("vscode.commands.executeCommand('aspire-vscode.copyEndpointUrl'"));

        assert.ok(!copyAppHostPathCase.includes('return copiedPath;'));
        assert.ok(!copyAppHostPathCase.includes("'appHostPath'"));
        assert.ok(!copyLogFilePathCase.includes('return logFilePath;'));
        assert.ok(!copyLogFilePathCase.includes("'logFilePath'"));
        assert.ok(!copyResourceNameCase.includes('return command.resourceName;'));
        assert.ok(!copyEndpointUrlCase.includes('return endpoint.url;'));
        assert.ok(!apiTypes.includes('expectedText: string'));
        assert.ok(!fixtures.includes('assertClipboardTextForE2E(expectedText'));
        assert.ok(!e2eStateFileBridge.includes('command.expectedText'));
        assert.ok(!treeActionsE2E.includes("name: 'copyEndpointUrl', appHostPath, resourceName: 'e2e-worker', url"));

        assert.ok(!treeActionsE2E.includes('copiedAppHost.result'));
        assert.ok(!treeActionsE2E.includes('copiedResourceName.result'));
        assert.ok(!treeActionsE2E.includes('copiedEndpointUrl.result'));
        assert.ok(!treeActionsE2E.includes('copiedLogPath.result'));
    });

    test('keeps E2E clipboard assertions tied to captured in-memory expectations', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');

        const copyAppHostPathCase = getSwitchCase(e2eStateFileBridge, 'copyAppHostPath', 'viewAppHostLogFile');
        const copyLogFilePathCase = getSwitchCase(e2eStateFileBridge, 'copyLogFilePath', 'viewResourceLogs');
        const copyResourceNameCase = getSwitchCase(e2eStateFileBridge, 'copyResourceName', 'copyEndpointUrl');
        const copyEndpointUrlCase = getSwitchCase(e2eStateFileBridge, 'copyEndpointUrl', 'openInIntegratedBrowser');
        const assertClipboardCase = getSwitchCase(e2eStateFileBridge, 'assertClipboardMatchesLastExpectation', 'openWorkspaceFolder');

        assert.ok(e2eStateFileBridge.includes('const clipboardExpectation: E2eClipboardExpectation = {};'));
        assert.ok(copyAppHostPathCase.includes("setClipboardExpectation(clipboardExpectation, expectedClipboardText, 'path');"));
        assert.ok(copyLogFilePathCase.includes("setClipboardExpectation(clipboardExpectation, expectedClipboardText, 'path');"));
        assert.ok(copyResourceNameCase.includes('setClipboardExpectation(clipboardExpectation, expectedClipboardText);'));
        assert.ok(copyEndpointUrlCase.includes('setClipboardExpectation(clipboardExpectation, endpoint.url);'));
        assert.ok(assertClipboardCase.includes('await assertExpectedClipboardText(clipboardExpectation);'));
        assert.ok(!assertClipboardCase.includes('createStateSnapshot'));
        assert.ok(!assertClipboardCase.includes('getEndpointElement'));
        assert.ok(!assertClipboardCase.includes('getLogFileElement'));
        assert.ok(!assertClipboardCase.includes('getResourceElement'));
    });

    test('keeps raw clipboard values out of E2E mismatch errors', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const functionStart = e2eStateFileBridge.indexOf('async function assertExpectedClipboardText');
        const functionEnd = e2eStateFileBridge.indexOf('function getE2eLaunchConfiguration', functionStart);

        assert.ok(functionStart >= 0);
        assert.ok(functionEnd > functionStart);

        const assertExpectedClipboardTextFunction = e2eStateFileBridge.slice(functionStart, functionEnd);

        assert.ok(assertExpectedClipboardTextFunction.includes('formatClipboardMismatchError(comparison, expectedText.length, clipboardText.length)'));
        assert.ok(!assertExpectedClipboardTextFunction.includes("Expected: '${expectedText}'"));
        assert.ok(!assertExpectedClipboardTextFunction.includes("actual: '${clipboardText}'"));
    });

    test('latches E2E AppHost stopping path transitions before snapshots can clear', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const apiTypes = fs.readFileSync(path.join(extensionRoot, 'src', 'types', 'extensionApi.ts'), 'utf8');
        const e2eStateFileBridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');
        const assertions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'assertions.ts'), 'utf8');
        const debugDashboard = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'debugDashboard.e2e.test.ts'), 'utf8');

        assert.ok(apiTypes.includes('stoppingPathEvents: readonly AspireExtensionE2EStoppingPathEvent[];'));
        assert.ok(apiTypes.includes("state: 'entered' | 'left';"));
        assert.ok(e2eStateFileBridge.includes('recordStoppingPathEvents(state.stoppingPaths);'));
        assert.ok(e2eStateFileBridge.includes("stoppingPathEvents.push({ sequence: ++stoppingPathSequence, appHostPath, state: 'entered' });"));
        assert.ok(assertions.includes('waitForStoppingPathEvent'));
        assert.ok(debugDashboard.includes('const beforeStoppingPathEvent = getStoppingPathEventCount();'));
        assert.ok(debugDashboard.includes("await waitForStoppingPathEvent(appHostPath, 'entered', beforeStoppingPathEvent, 120000);"));
        assert.ok(!debugDashboard.includes("file => file.state.stoppingPaths.some(stoppingPath => isSamePath(stoppingPath, appHostPath))"));
    });

    test('waits for durable AppHost discovery gates before asserting running state', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const vscodeHelpers = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'vscode.ts'), 'utf8');
        const appHostTree = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'appHostTree.e2e.test.ts'), 'utf8');
        const runningBeforeDiscoveryTest = getTestBlock(appHostTree, 'running AppHosts appear before slow discovery results');

        assert.ok(fixtures.includes('writeGatedStreamingDiscoveryCliWrapper'));
        assert.ok(fixtures.includes('function waitForReleaseFile'));
        assert.ok(fixtures.includes('waitForPsSnapshotRequest: () => waitForPath(psSnapshotRequestFilePath, 30_000)'));
        assert.ok(fixtures.includes('waitForLsCandidateRequest: () => waitForPath(lsCandidateRequestFilePath, 30_000)'));
        assert.ok(fixtures.includes('psSnapshotAppHostPid'));
        assert.ok(fixtures.includes("args.includes('--follow')"));
        assert.ok(fixtures.includes('Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 2_147_483_647);'));
        assert.ok(fixtures.includes('fs.writeSync(process.stdout.fd, JSON.stringify(payload)'));
        assert.ok(appHostTree.includes('writeGatedStreamingDiscoveryCliWrapper'));
        assert.ok(appHostTree.includes('discoveryGate.releasePsSnapshot();'));
        assert.ok(appHostTree.includes('discoveryGate.releaseLsCandidate();'));
        assert.ok(!runningBeforeDiscoveryTest.includes('waitForWorkspaceRediscoveryLoading'));
        assert.ok(vscodeHelpers.includes('export async function startAppHostsSectionTextTransition'));
        assert.ok(vscodeHelpers.includes('export async function waitForAppHostsSectionTextAfterTransition'));
        assert.ok(vscodeHelpers.includes('export async function cancelAppHostsSectionTextTransition'));
        assert.ok(runningBeforeDiscoveryTest.includes('const runningAppHost = findRunningAppHost(running.state);'));
        assert.ok(runningBeforeDiscoveryTest.includes('const authoritativeSnapshotAppHostPid = runningAppHost.appHostPid + 1_000_000;'));
        assert.ok(runningBeforeDiscoveryTest.includes('writeGatedStreamingDiscoveryCliWrapper(runningAppHost.appHostPath, authoritativeSnapshotAppHostPid)'));
        assert.ok(runningBeforeDiscoveryTest.includes('await startAppHostsSectionTextTransition([appHostLabel], /\\(\\d+ resources\\)/);'));
        assert.ok(runningBeforeDiscoveryTest.includes('const snapshotReleasedAt = Date.now();'));
        assert.ok(runningBeforeDiscoveryTest.includes('await waitForAppHostsSectionTextAfterTransition([appHostLabel], /\\(\\d+ resources\\)/, snapshotReleasedAt);'));
        assert.ok(runningBeforeDiscoveryTest.includes('file.state.isRepositoryLoading === false'));
        assert.ok(runningBeforeDiscoveryTest.includes('findRunningAppHost(file.state)?.appHostPid === authoritativeSnapshotAppHostPid'));

        const cleanupIndex = runningBeforeDiscoveryTest.indexOf('finally {');
        assert.ok(cleanupIndex >= 0, 'Expected the E2E to keep cleanup releases in a finally block.');
        const testBeforeCleanup = runningBeforeDiscoveryTest.slice(0, cleanupIndex);
        const cleanup = runningBeforeDiscoveryTest.slice(cleanupIndex);
        const startTransitionIndex = testBeforeCleanup.indexOf('await startAppHostsSectionTextTransition([appHostLabel], /\\(\\d+ resources\\)/);');
        const refreshIndex = testBeforeCleanup.indexOf("await executeE2eControlCommand({ name: 'refreshAppHosts' }");
        const waitForPsRequestIndex = testBeforeCleanup.indexOf('await discoveryGate.waitForPsSnapshotRequest();');
        const waitForLsRequestIndex = testBeforeCleanup.indexOf('await discoveryGate.waitForLsCandidateRequest();');
        const snapshotReleasedAtIndex = testBeforeCleanup.indexOf('const snapshotReleasedAt = Date.now();');
        const releasePsIndex = testBeforeCleanup.indexOf('discoveryGate.releasePsSnapshot();');
        const runningStateIndex = testBeforeCleanup.indexOf('const runningBeforeDiscovery = await waitForExtensionState');
        const runningTreeTextIndex = testBeforeCleanup.indexOf('await waitForAppHostsSectionTextAfterTransition([appHostLabel], /\\(\\d+ resources\\)/, snapshotReleasedAt);');
        const releaseLsIndex = testBeforeCleanup.indexOf('discoveryGate.releaseLsCandidate();');

        assert.ok(startTransitionIndex >= 0, 'The E2E must track the currently rendered running row before refresh.');
        assert.ok(refreshIndex > startTransitionIndex, 'The AppHosts pane transition must be tracked before refresh starts.');
        assert.ok(waitForPsRequestIndex >= 0, 'The E2E must wait until the running AppHost snapshot reaches its gate.');
        assert.ok(waitForLsRequestIndex > waitForPsRequestIndex, 'The E2E must wait until workspace discovery reaches its gate.');
        assert.ok(snapshotReleasedAtIndex > waitForLsRequestIndex, 'The fresh running AppHost snapshot release time must be captured only after both refresh paths are gated.');
        assert.ok(releasePsIndex > snapshotReleasedAtIndex, 'The fresh running AppHost snapshot must be released after its transition threshold is captured.');
        assert.ok(runningStateIndex > releasePsIndex, 'The running AppHost must be asserted after the fresh snapshot is released.');
        assert.ok(runningTreeTextIndex > runningStateIndex, 'The fresh running AppHost snapshot must be visibly rendered before discovery is released.');
        assert.ok(releaseLsIndex > runningTreeTextIndex, 'The slow workspace candidate must remain gated until the running AppHost is rendered.');
        assert.ok(cleanup.includes('await runE2eTeardown(['), 'Every cleanup must run even when another cleanup fails.');
        assert.ok(cleanup.includes('() => cancelAppHostsSectionTextTransition()'), 'The transition tracker must be disposed even when the E2E fails before observing the rendered row.');
    });

    test('derives AppHost tree cold-start budgets from the effective CLI startup timeout', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const appHostTree = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'appHostTree.e2e.test.ts'), 'utf8');
        const runningBeforeDiscoveryTest = getTestBlock(appHostTree, 'running AppHosts appear before slow discovery results');

        assert.ok(runner.includes("ASPIRE_CLI_START_TIMEOUT: process.env.ASPIRE_EXTENSION_E2E_CLI_START_TIMEOUT || '300'"));
        assert.ok(appHostTree.includes('const cliStartupTimeoutMs = getCliStartupTimeoutMs();'));
        assert.ok(appHostTree.includes('const configuredSeconds = Number(process.env.ASPIRE_CLI_START_TIMEOUT);'));
        assert.ok(appHostTree.includes('this.timeout(cliStartupTimeoutMs * 2);'));
        assert.ok(runningBeforeDiscoveryTest.includes('waitForRunningAppHost(cliStartupTimeoutMs)'));
        assert.ok(!appHostTree.includes('this.timeout(600000);'));
        assert.ok(!runningBeforeDiscoveryTest.includes('waitForRunningAppHost(300000)'));
    });

    test('starts the running-before-discovery scenario through the deterministic control bridge', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const appHostTree = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'appHostTree.e2e.test.ts'), 'utf8');
        const runningBeforeDiscoveryTest = getTestBlock(appHostTree, 'running AppHosts appear before slow discovery results');

        assert.ok(runningBeforeDiscoveryTest.includes('const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();'));
        assert.ok(runningBeforeDiscoveryTest.includes("const runInvocationBefore = getCommandInvocationCount('aspire-vscode.runAppHost');"));
        assert.ok(runningBeforeDiscoveryTest.includes("await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });"));
        assert.ok(runningBeforeDiscoveryTest.includes("await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 60000, runInvocationBefore);"));
        assert.ok(!runningBeforeDiscoveryTest.includes('waitForTreeItem('));
        assert.ok(!runningBeforeDiscoveryTest.includes('.expand()'));
        assert.ok(!runningBeforeDiscoveryTest.includes('clickTreeItem('));
    });

    test('patches ExTester launch arguments without version-specific assumptions or replacement-token expansion', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const browser = fs.readFileSync(path.join(extensionRoot, 'node_modules', 'vscode-extension-tester', 'out', 'browser.js'), 'utf8');
        const argsDeclaration = /const args = \[[^\n]*`--user-data-dir=\$\{path\.join\(this\.storagePath, 'settings'\)\}`(?:, [^\n]+?)?\];/.exec(browser);
        const cleanArgsDeclaration = "const args = ['--no-sandbox', '--disable-dev-shm-usage', `--user-data-dir=${path.join(this.storagePath, 'settings')}`];";

        assert.ok(argsDeclaration);
        assert.ok(runner.includes(cleanArgsDeclaration));
        assert.ok(runner.includes('ExTester does not expose a supported way to open VS Code with a workspace'));
        assert.ok(runner.includes('Patching ExTester VS Code launch arguments by exact argument match.'));
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

    test('keeps the ambiguous dynamic debug launch timeout above its process-state wait', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const dynamicDebugConfiguration = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'dynamicDebugConfiguration.e2e.test.ts'), 'utf8');
        const ambiguousLaunchTest = getTestBlock(dynamicDebugConfiguration, 'launches the selected AppHost from an ambiguous single-folder workspace');

        assert.ok(dynamicDebugConfiguration.includes('const appHostProcessStateTimeoutMs = 120000;'));
        assert.ok(ambiguousLaunchTest.includes('this.timeout(300000);'));
        assert.ok(ambiguousLaunchTest.includes('appHostProcessStateTimeoutMs'));
    });

    test('uses state-specific assertions for debugger guidance and project creation readiness', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const edgeCasesSource = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'edgeCases.e2e.test.ts'), 'utf8');
        const workspaceTargetProofSource = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'workspaceTargetProof.e2e.test.ts'), 'utf8');

        assert.ok(workspaceTargetProofSource.includes("const wrapperDirectory = path.join(fixtureRoot, '.workspace-target-cli-wrappers');"));
        assert.ok(workspaceTargetProofSource.includes('const wrapperPath = path.join(wrapperDirectory, `aspire-${folderName}`);'));
        assert.ok(!workspaceTargetProofSource.includes('const wrapperPath = path.join(folderPath, `aspire-${folderName}`);'));
        const workspaceRouting = getTestBlock(workspaceTargetProofSource, 'isolates folder terminals and keeps global commands window scoped');
        assert.ok(workspaceRouting.includes("const folderC = createFolderFixture(runRoot, 'folder-c');"));
        assert.ok(workspaceRouting.includes("const workspaceFolderLabels = ['folder-a', 'folder-b', 'folder-c'] as const;"));
        assert.ok(workspaceRouting.includes("await invokeNewForFolder('folder-c', folderC);"));
        assert.ok(!workspaceRouting.includes("await invokeNewForFolder('folder-b', folderB);"));
        assert.ok(workspaceRouting.includes('assert.ok(!updateCommand.commandLine.includes(folderC.wrapperPath), updateCommand.commandLine);'));

        const edgeCases = getTestBlock(edgeCasesSource, 'shows debugger install guidance while the Aspire panel and AppHost source are closed');
        assert.ok(edgeCases.includes("waitForNotificationMessage("));
        assert.ok(edgeCases.includes("'Set up Python debugging support to debug resources in this app.'"));
        assert.ok(!edgeCases.includes("name: 'getCodeLenses'"));
        assert.ok(!edgeCases.includes("waitForCodeLensText("));
        assert.ok(!edgeCases.includes("name: 'openFile'"));
        assert.ok(!edgeCases.includes("waitForWorkbenchText('Set up Python debugger'"));

        const workspaceCollision = getTestBlock(workspaceTargetProofSource, 'reopens the project folder picker after a colliding selection');
        assert.ok(!workspaceCollision.includes('await openAspireView();'));
        assert.ok(workspaceCollision.includes('await waitForRepositoryIdle();'));
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
        const dynamicDebugConfiguration = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'dynamicDebugConfiguration.e2e.test.ts'), 'utf8');
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
        assert.ok(dynamicDebugConfiguration.includes('let appHostPidsBeforeStop: number[];'));
        assert.ok(dynamicDebugConfiguration.includes('appHostPidsBeforeStop = [];'));
        const captureFixtureAppHostPids = dynamicDebugConfiguration.indexOf('appHostPidsBeforeStop = fixtureAppHostPaths');
        const stopFixtureAppHosts = dynamicDebugConfiguration.indexOf('...fixtureAppHostPaths.map(appHostPath => () => fs.existsSync(appHostPath) ? stopAppHostIfRunning(appHostPath) : undefined)');
        const waitForFixtureAppHostPids = dynamicDebugConfiguration.indexOf('Promise.all(appHostPidsBeforeStop.map(appHostPid =>');
        const removeFixtureRoot = dynamicDebugConfiguration.indexOf('removePath(fixtureRoot, { recursive: true, force: true })');
        assert.ok(captureFixtureAppHostPids >= 0);
        assert.ok(stopFixtureAppHosts > captureFixtureAppHostPids);
        assert.ok(dynamicDebugConfiguration.includes("waitForKnownProcessExit(appHostPid, 'a dynamic debug configuration AppHost process', 30000)"));
        assert.ok(waitForFixtureAppHostPids > stopFixtureAppHosts);
        assert.ok(removeFixtureRoot > waitForFixtureAppHostPids);
        assert.ok(commandPalette.includes('runE2eTeardown'));
        assert.ok(discoveryConfiguration.includes('runE2eTeardown'));
        assert.ok(!commandPalette.includes('throw new AggregateError'));
        assert.ok(!discoveryConfiguration.includes('throw new AggregateError'));
        assert.ok(fixtures.includes("['ps', '--format', 'json', '--nologo']"));
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

    test('keeps the gated deploy test-controlled and cleans its fixture in finally', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const fixtures = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'fixtures.ts'), 'utf8');
        const treeActions = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'treeActions.e2e.test.ts'), 'utf8');
        const gatedDeployFixture = fixtures.slice(
            fixtures.indexOf('export function writeGatedDeployActionCliWrapper'),
            fixtures.indexOf('export function writeLegacyPipelineActionCliWrapper'));
        const durableOperationTest = getTestBlock(treeActions, 'keeps one durable operation per AppHost while a deploy session is in flight');

        assert.ok(gatedDeployFixture.includes('cleanup: () => void;'));
        assert.ok(gatedDeployFixture.includes('removePath(gateDirectory, { recursive: true, force: true });'));
        assert.ok(gatedDeployFixture.includes('removePath(cliPath, { force: true });'));
        assert.ok(gatedDeployFixture.includes('removePath(scriptPath, { force: true });'));
        assert.ok(fixtures.includes("waitForReleaseFile(${JSON.stringify(options.deployReleaseFilePath)}, 'gated deploy', 900000, ${JSON.stringify(options.deployGateDirectory)});"));
        assert.ok(fixtures.includes('if (releaseDirectory !== undefined && !fs.existsSync(releaseDirectory)) {'));
        assert.ok(durableOperationTest.replace(/\r\n/g, '\n').includes(`finally {
            try {
                gatedCli.releaseDeploy();
            }
            finally {
                gatedCli.cleanup();
            }
            await waitForNoDebugSessions(120000);
        }`));
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
    test('reuses immutable VS Code downloads while keeping ExTester state per run', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(runner.includes("require('./e2e-download-cache')"));
        assert.ok(runner.includes('resolveDownloadCacheRoot(repoRoot)'));
        assert.ok(runner.includes('ensureDownloadCache({'));
        assert.ok(runner.includes('projectDownloadCache(downloadCache, storageDir);'));
        assert.ok(runner.includes('cleanPartialExtesterDownloads(stagingDirectory)'));
        assert.ok(runner.includes("'--offline'"));
        assert.ok(runner.includes("const storageDir = path.join(shortRunRoot, 'storage');"));
        assert.ok(runner.includes("const extensionsDir = path.join(shortRunRoot, 'extensions');"));
    });

    test('downloads into the cache staging directory rather than the per-run storage directory', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const populateStart = runner.indexOf('populate(stagingDirectory) {');
        const populateEnd = runner.indexOf('projectDownloadCache(downloadCache, storageDir);');
        const populateBody = runner.slice(populateStart, populateEnd);

        assert.ok(populateStart >= 0);
        assert.ok(populateEnd > populateStart);
        // The storage path handed to ExTester is derived from the staging directory rather than
        // being it verbatim, because ExTester interpolates it unquoted into shell commands.
        assert.ok(populateBody.includes('projectCommandSafeStagingDirectory(stagingDirectory)'));
        assert.ok(populateBody.includes("'get-vscode', '--storage', downloadDirectory"));
        assert.ok(populateBody.includes("'get-chromedriver', '--storage', downloadDirectory"));
        assert.ok(!populateBody.includes('--storage\', storageDir'));
    });

    test('tears down the per-run root without following projections into the shared cache', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const cleanupStart = runner.indexOf('function cleanupTemporaryRunRoot()');
        const cleanupBody = runner.slice(cleanupStart, runner.indexOf('\n}', cleanupStart));

        // The run root holds junctions into the shared download cache, and recursive deletion
        // descends junctions on Windows, so this teardown has to detach links instead.
        assert.ok(cleanupStart >= 0);
        assert.ok(cleanupBody.includes('removePathWithoutFollowingLinks(shortRunRoot, {'));
        assert.ok(!cleanupBody.includes('removePath(shortRunRoot'));
        assert.ok(!cleanupBody.includes('fs.rmSync('));
    });

    test('pins the VS Code version the download cache is keyed on', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        // ExTester's loadCodeVersion prefers CODE_VERSION over --code_version, so an inherited
        // value would download a version the cache key does not describe and leave a later run
        // reusing the wrong install offline.
        assert.ok(runner.includes('CODE_VERSION: vscodeVersion,'));
        assert.ok(runner.includes('const vscodeVersion = resolveCachedVsCodeVersion('));
        assert.match(runner, /vscodeVersion,\r?\n\s+extesterVersion,/);

        // ExTester's codeStream falls back to CODE_TYPE when --type is absent, and an Insiders
        // build unpacks into directory names this cache does not discover.
        assert.ok(runner.includes("CODE_TYPE: 'stable',"));
    });

    test('pins unit-test VS Code download to avoid moving latest resolution', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const unitTestConfig = fs.readFileSync(path.join(extensionRoot, '.vscode-test.mjs'), 'utf8');

        assert.ok(unitTestConfig.includes("version: '1.131.0'"));
    });

    test('cleans only ExTester download archives between setup retries', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const cleanupStart = runner.indexOf('function cleanPartialExtesterDownloads(');
        const cleanupBody = runner.slice(cleanupStart, runner.indexOf('\n}', cleanupStart));

        // A ChromeDriver retry runs after VS Code has been unpacked into the same staging
        // directory, so a recursive sweep would strip archives out of the application tree and
        // publish a damaged entry to the shared cache.
        assert.ok(cleanupStart >= 0);
        assert.ok(!cleanupBody.includes('getFilesRecursive('));
        assert.ok(cleanupBody.includes("readdirSync(storageDirectory, { withFileTypes: true })"));
        assert.ok(cleanupBody.includes('entry.isFile() && isPartialDownloadArchiveName(entry.name)'));
    });

    test('rejects moving VS Code aliases that a cache key could never invalidate', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const resolverStart = runner.indexOf('function resolveCachedVsCodeVersion(');
        const resolverBody = runner.slice(resolverStart, runner.indexOf('\n}', resolverStart));

        // `latest` would freeze the first release ever downloaded into `vscode-latest`. `min` and
        // `max` resolve from the pinned ExTester version, which is already part of the key.
        assert.ok(resolverStart >= 0);
        assert.ok(resolverBody.includes("normalizedVersion === 'min' || normalizedVersion === 'max'"));
        assert.ok(resolverBody.includes('/^\\d+\\.\\d+(\\.\\d+)?$/.test(normalizedVersion)'));
        assert.ok(resolverBody.includes("a concrete version such as '1.130.0'"));
        assert.ok(resolverBody.includes('throw new Error('));
    });

    test('hands ExTester a storage path the command interpreter cannot reinterpret', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const projectionStart = runner.indexOf('function projectCommandSafeStagingDirectory(');
        const projectionBody = runner.slice(projectionStart, runner.indexOf('\n}', projectionStart));

        // ExTester interpolates this path unquoted into `unzip -qo` on macOS and Linux and into
        // `<chromedriver> -v` on every platform, and the cache now lives wherever the repository
        // was cloned, so anything the interpreter acts on has to be projected away -- on Windows
        // too, and not just whitespace.
        assert.ok(projectionStart >= 0);
        assert.ok(projectionBody.includes('COMMAND_INERT_PATH_PATTERN.test(stagingDirectory)'));
        assert.ok(!projectionBody.includes("process.platform === 'win32' ||"));
        assert.ok(projectionBody.includes('!COMMAND_INERT_PATH_PATTERN.test(linkPath)'));
        assert.ok(projectionBody.includes("fs.symlinkSync(stagingDirectory, linkPath, isWindows ? 'junction' : 'dir')"));
        assert.ok(projectionBody.includes('removePathWithoutFollowingLinks(linkPath)'));

        const posixPattern = readSourcePattern(runner, 'POSIX_SHELL_INERT_PATH_PATTERN');
        const windowsPattern = readSourcePattern(runner, 'WINDOWS_COMMAND_INERT_PATH_PATTERN');

        // None of these contain whitespace, so a whitespace-only guard would hand every one of
        // them straight to `/bin/sh -c`.
        for (const shellActivePath of [
            '/home/dev/repo;touch-marker/cache',
            '/home/dev/repo$(id)/cache',
            '/home/dev/repo`id`/cache',
            '/home/dev/repo(1)/cache',
            '/home/dev/R&D/cache',
            '/home/dev/repo|tee/cache',
            '/home/dev/repo>out/cache',
            '/home/dev/repo*/cache',
            '/home/dev/repo?/cache',
            "/home/dev/it's/cache",
            '/home/dev/repo"x/cache',
            '/home/dev/repo\\x/cache',
            '/home/dev/~repo/cache',
            '/home/dev/repo#1/cache',
            '/home/dev/repo!1/cache',
            '/home/dev/my repo/cache',
        ]) {
            assert.ok(!posixPattern.test(shellActivePath), `${shellActivePath} must be projected`);
        }

        for (const inertPath of [
            '/home/dev/aspire/extension/.e2e-download-cache',
            '/var/folders/f9/T/aspire-e2e-Xa1B2c',
            '/home/dev/repo-1.2.3_x86+64@host/cache',
        ]) {
            assert.ok(posixPattern.test(inertPath), `${inertPath} must not be projected`);
        }

        // `cmd.exe /d /s /c` strips the quotes Node wraps the command in, so a space, a `&`, or
        // any of the token separators `,`, `;` and `=` breaks or redirects `<chromedriver> -v`.
        for (const commandActivePath of [
            'C:\\src\\my repo\\.cache',
            'C:\\src\\R&D\\.cache',
            'C:\\src\\repo(1)\\.cache',
            'C:\\src\\repo%PATH%\\.cache',
            'C:\\src\\repo!x!\\.cache',
            'C:\\src\\repo^x\\.cache',
            'C:\\src\\repo|tee\\.cache',
            'C:\\src\\repo>out\\.cache',
            'C:\\src\\repo,x\\.cache',
            'C:\\src\\repo;x\\.cache',
            'C:\\src\\repo=x\\.cache',
            'C:\\src\\repo"x\\.cache',
        ]) {
            assert.ok(!windowsPattern.test(commandActivePath), `${commandActivePath} must be projected`);
        }

        // `~` has to stay legal on Windows: hosted runners put TEMP under an 8.3 short name, and
        // rejecting it would push every run onto a projection whose own path is equally rejected,
        // turning a warm cache into a hard failure.
        for (const inertPath of [
            'C:\\Users\\RUNNER~1\\AppData\\Local\\Temp\\aev-Xa1B2c',
            'C:\\src\\aspire\\.git\\aspire-extension-e2e-cache',
            'D:\\a\\aspire\\aspire\\extension\\.cache-1.2.3_x86+64@host',
        ]) {
            assert.ok(windowsPattern.test(inertPath), `${inertPath} must not be projected`);
        }
    });

    test('cleans up orphaned unpack processes before a setup download can be retried', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const runStart = runner.indexOf('function run(command, args, extraEnv = {}, options = {}) {');
        const runBody = runner.slice(runStart, runner.indexOf('\n}\n', runStart));

        // spawnSync's timeout signals only the process it started, so ExTester's shelled-out
        // `unzip` survives and keeps writing into a staging directory that is about to be
        // published as an immutable cache entry. The behaviour of the cleanup itself is covered
        // functionally in e2eDownloadRetry.test.ts; this pins the wiring that reaches it.
        assert.ok(runStart >= 0);
        assert.ok(runner.includes("} = require('./e2e-download-retry');"));
        assert.ok(runner.includes('terminateOrphansUnder: downloadDirectory,'));
        assert.ok(runBody.includes("result.error?.code === 'ETIMEDOUT' && options.terminateOrphansUnder"));
        assert.ok(runBody.includes('terminateOrphanedDescendants(options.terminateOrphansUnder);'));

        // A cleanup that cannot account for the orphans must not fall through to another attempt,
        // because `beforeRetry` would then wipe a directory something may still be writing into.
        assert.ok(runBody.includes('throw markErrorNonRetryable(new Error('));
        assert.ok(runner.includes('beforeRetry: options.beforeRetry,'));
    });

    test('keeps setup downloads in the terminal foreground process group', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const runStart = runner.indexOf('function run(command, args, extraEnv = {}, options = {}) {');
        const runBody = runner.slice(runStart, runner.indexOf('\n}', runStart));

        // Detaching would take the child out of the foreground group and stop Ctrl-C from
        // reaching a download, which is why timed-out unpack processes are matched by path
        // instead of by process group.
        assert.ok(runStart >= 0);
        assert.ok(!runBody.includes('detached'));
    });

    test('removes ExTester unpack directories abandoned by a killed setup attempt', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const cleanupStart = runner.indexOf('function cleanPartialExtesterDownloads(');
        const cleanupBody = runner.slice(cleanupStart, runner.indexOf('\n}', cleanupStart));

        // ExTester removes `vscode-temp-*` in a `finally` that a killed process never reaches, so
        // a later successful retry would publish a whole abandoned VS Code copy alongside the
        // real one.
        assert.ok(cleanupStart >= 0);
        assert.ok(cleanupBody.includes('EXTESTER_UNPACK_DIRECTORY_PREFIX'));
        assert.ok(cleanupBody.includes('removePathWithoutFollowingLinks(entryPath)'));
        assert.ok(runner.includes("const EXTESTER_UNPACK_DIRECTORY_PREFIX = 'vscode-temp-';"));
    });

    test('resolves the download cache root before creating the per-run temporary root', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const runRootIndex = runner.indexOf('const shortRunRoot =');

        // These run at module scope, outside the cleanup scope `main()` installs, so anything that
        // can reject the environment has to run before the run root exists or a throw strands it.
        assert.ok(runRootIndex > 0);
        assert.ok(runner.indexOf('const downloadCacheRoot =') < runRootIndex);
        assert.ok(runner.indexOf('const vscodeVersion = resolveCachedVsCodeVersion(') < runRootIndex);
    });
});

function getSwitchCase(source: string, startCase: string, nextCase: string): string {
    const start = source.indexOf(`case '${startCase}':`);
    const end = source.indexOf(`case '${nextCase}':`, start);

    assert.ok(start >= 0, `Expected to find ${startCase} case.`);
    assert.ok(end > start, `Expected to find ${nextCase} case after ${startCase}.`);

    return source.slice(start, end);
}

function assertTextOrder(source: string, before: string, after: string): void {
    const beforeIndex = source.indexOf(before);
    const afterIndex = source.indexOf(after);

    assert.ok(beforeIndex >= 0, `Expected to find "${before}".`);
    assert.ok(afterIndex >= 0, `Expected to find "${after}".`);
    assert.ok(beforeIndex < afterIndex, `Expected "${before}" to appear before "${after}".`);
}
