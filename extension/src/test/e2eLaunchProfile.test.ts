import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { spawnSync } from 'child_process';
import * as ts from 'typescript';

import { removeDirectorySafely } from './testHelpers';
function readSourcePattern(source: string, name: string): RegExp {
    const declaration = new RegExp(`const ${name} = /(.+)/;`).exec(source);
    assert.ok(declaration, `run-e2e.js must define ${name}`);
    return new RegExp(declaration[1]);
}

function normalizeLineEndings(source: string): string {
    return source.replace(/\r\n/g, '\n');
}

function readRunnerSource(extensionRoot: string): string {
    return normalizeLineEndings(
        fs.readFileSync(path.resolve(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8'));
}

function readResourceDebuggerSource(extensionRoot: string): string {
    return normalizeLineEndings(
        fs.readFileSync(path.resolve(extensionRoot, 'src', 'test-e2e', 'resourceDebugger.e2e.test.ts'), 'utf8'));
}

/**
 * Removes block and line comments so a statement-level assertion is not satisfied or defeated by
 * prose. The comments in `run-e2e.js` discuss `throw` and `fs.` precisely because the code around
 * them must not use either.
 */
function stripComments(source: string): string {
    return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
}

const resourceDebuggerDeadlineProtectedCalls = new Set([
    'clearBreakpoints',
    'executeE2eControlCommand',
    'openAspireView',
    'stopPrimaryAppHostIfRunning',
    'waitForNoDebugSessions',
    'waitForNoRunningAppHost',
    'waitForProcessExit',
    'waitForRepositoryIdle',
    'waitForWorkspaceAppHost',
]);

const resourceDebuggerCallsRequiringTimeoutArgument = new Set([
    'executeE2eControlCommand',
    'waitForNoDebugSessions',
    'waitForNoRunningAppHost',
    'waitForProcessExit',
    'waitForRepositoryIdle',
    'waitForWorkspaceAppHost',
]);

function assertResourceDebuggerUsesBoundedDeadlines(source: string): void {
    const sourceFile = ts.createSourceFile('resourceDebugger.e2e.test.ts', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const wrapperDeclaration = sourceFile.statements.find(statement =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'runResourceDebuggerPhase');
    assert.ok(wrapperDeclaration && ts.isFunctionDeclaration(wrapperDeclaration) && wrapperDeclaration.body,
        'Expected the resource debugger to define its shared-deadline phase wrapper.');
    const cleanupWrapperDeclaration = sourceFile.statements.find(statement =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'runResourceDebuggerCleanupPhase');
    assert.ok(cleanupWrapperDeclaration && ts.isFunctionDeclaration(cleanupWrapperDeclaration) && cleanupWrapperDeclaration.body,
        'Expected the resource debugger to define its independent cleanup phase wrapper.');

    const wrapperText = wrapperDeclaration.getText(sourceFile);
    assert.ok(wrapperText.includes('getRemainingE2eDeadlineMs('), 'The phase wrapper must derive its timeout from the shared deadline.');
    assert.ok(
        wrapperText.includes('const phaseDeadline = Math.min(deadline, Date.now() + timeoutMs);'),
        'The phase wrapper must derive a phase deadline without extending the shared deadline.');
    assert.ok(
        wrapperText.includes('runWithE2eDeadline(description, phaseDeadline,'),
        'The phase wrapper must bound operations that do not accept a timeout argument by the derived phase deadline.');
    const cleanupWrapperText = cleanupWrapperDeclaration.getText(sourceFile);
    assert.ok(
        cleanupWrapperText.includes('const deadline = Date.now() + phaseCeilingMs;'),
        'Each cleanup phase must receive a fresh deadline after the proof deadline expires.');
    assert.ok(
        cleanupWrapperText.includes('runResourceDebuggerPhase(description, deadline, phaseCeilingMs, operation)'),
        'Cleanup phases must retain the same per-operation timeout enforcement as proof phases.');
    let teardownCallback: ts.ArrowFunction | undefined;
    const findTeardownCallback = (node: ts.Node): void => {
        if (ts.isCallExpression(node)
            && ts.isIdentifier(node.expression)
            && node.expression.text === 'teardown'
            && ts.isArrowFunction(node.arguments[0])) {
            teardownCallback = node.arguments[0];
            return;
        }

        ts.forEachChild(node, findTeardownCallback);
    };
    findTeardownCallback(sourceFile);
    assert.ok(teardownCallback, 'Expected the resource debugger suite to register teardown cleanup.');
    const teardownText = teardownCallback.getText(sourceFile);
    assert.ok(teardownText.includes('runResourceDebuggerCleanupPhase('),
        'Teardown cleanup must run through the independent cleanup phase wrapper.');
    assert.ok(!teardownText.includes('resourceDebuggerDeadline'),
        'Teardown cleanup must not reuse the proof deadline.');
    let waitsOnAppHostStateMirror = false;
    const findAppHostStateWait = (node: ts.Node): void => {
        if (ts.isCallExpression(node)
            && ts.isIdentifier(node.expression)
            && node.expression.text === 'waitForNoRunningAppHost') {
            waitsOnAppHostStateMirror = true;
            return;
        }

        ts.forEachChild(node, findAppHostStateWait);
    };
    findAppHostStateWait(teardownCallback);
    assert.ok(!waitsOnAppHostStateMirror,
        'Teardown must not wait on the extension state AppHost mirror after process-aware stopping has completed.');

    const requiredPhaseCeilings = [
        'openAspireView',
        'repositoryIdle',
        'workspaceAppHost',
        'proof',
        'proofControl',
        'stopDebuggingControl',
        'processExit',
        'debugSessions',
        'appHostStop',
        'appHostExit',
    ];
    for (const phase of requiredPhaseCeilings) {
        assert.ok(source.includes(`${phase}:`), `Expected a centralized timeout ceiling for the ${phase} phase.`);
    }

    assert.ok(
        source.includes('this.timeout(resourceDebuggerDeadlineTimeoutMs + resourceDebuggerTeardownTimeoutMs);'),
        'The Mocha timeout must include the independently bounded teardown budget.');
    const deadlineTimeoutMatch = /const resourceDebuggerDeadlineTimeoutMs = (\d+);/.exec(source);
    const teardownTimeoutMatch = /const resourceDebuggerTeardownTimeoutMs = (\d+);/.exec(source);
    assert.ok(deadlineTimeoutMatch, 'Expected a numeric shared resource debugger deadline.');
    assert.ok(teardownTimeoutMatch, 'Expected a numeric resource debugger teardown timeout.');
    assert.ok(Number(teardownTimeoutMatch[1]) > 0, 'The Mocha timeout must exceed the shared resource debugger deadline.');
    assert.ok(Number(teardownTimeoutMatch[1]) <= 600000, 'The teardown timeout must remain bounded below the workflow timeout.');

    let protectedCallCount = 0;
    const visit = (node: ts.Node): void => {
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && resourceDebuggerDeadlineProtectedCalls.has(node.expression.text)) {
            protectedCallCount++;
            const callName = node.expression.text;
            let callback: ts.ArrowFunction | ts.FunctionExpression | undefined;
            let current: ts.Node | undefined = node.parent;
            while (current && !ts.isFunctionDeclaration(current)) {
                if (ts.isArrowFunction(current) || ts.isFunctionExpression(current)) {
                    const parent: ts.Node = current.parent;
                    if (ts.isCallExpression(parent)
                        && ts.isIdentifier(parent.expression)) {
                        const callbackIndex = parent.expression.text === 'runResourceDebuggerPhase'
                            ? 3
                            : parent.expression.text === 'runResourceDebuggerCleanupPhase'
                                ? 2
                                : -1;
                        if (parent.arguments[callbackIndex] === current) {
                            callback = current;
                            break;
                        }
                    }
                }
                current = current.parent;
            }

            assert.ok(callback, `Expected ${callName} to execute through runResourceDebuggerPhase.`);
            const timeoutParameter = callback.parameters[0];
            if (resourceDebuggerCallsRequiringTimeoutArgument.has(callName)) {
                assert.ok(timeoutParameter && ts.isIdentifier(timeoutParameter.name),
                    `Expected the ${callName} phase callback to receive its remaining timeout.`);
                const timeoutName = timeoutParameter.name.text;
                let usesTimeout = false;
                const findTimeoutUse = (candidate: ts.Node): void => {
                    if (candidate !== timeoutParameter.name && ts.isIdentifier(candidate) && candidate.text === timeoutName) {
                        usesTimeout = true;
                    }
                    ts.forEachChild(candidate, findTimeoutUse);
                };
                findTimeoutUse(node);
                assert.ok(usesTimeout, `Expected ${callName} to use the remaining timeout supplied by runResourceDebuggerPhase.`);
            }
        }

        ts.forEachChild(node, visit);
    };
    visit(sourceFile);

    assert.ok(protectedCallCount > 0, 'Expected to validate resource debugger deadline-bound calls.');
}

interface ExTesterAwait {
    runWithProcessTreeTimeout: ts.Identifier;
    process: ts.Identifier;
    runTestsArgs: ts.Identifier;
    extesterCli: ts.Identifier;
    testSpec: ts.Identifier;
    extestEnv: ts.Identifier;
    getRunTestsTimeoutMs: ts.Identifier;
}

interface MainInvocation {
    main: ts.Identifier;
    failureHandler: ts.Expression;
}

const protectedFunctionNames = [
    'main',
    'getRunTestsTimeoutMs',
    'readMochaResults',
    'readJsonIfExists',
    'sanitizePathSegment',
] as const;

const protectedTopLevelBindingNames = [
    'assertShardExecutedTests',
    'extesterCli',
    'runWithProcessTreeTimeout',
    'shardName',
    'testSpec',
] as const;

const protectedMainBindingNames = [
    'cleanupFailed',
    'extestEnv',
    'recording',
    'runTestsArgs',
    'testFailure',
] as const;

function assertShardResultGuardWiring(
    source: string,
    expectedSyntax = fs.readFileSync(
        path.resolve(__dirname, '..', '..', 'src', 'test', 'e2eLaunchProfile.runner-wiring.txt'),
        'utf8')): void {
    const { sourceFile, checker } = createRunnerProgram(source);
    assertUniqueProtectedDeclarations(sourceFile);
    assert.strictEqual(
        getProtectedRunnerSyntax(sourceFile).replace(/\r\n/g, '\n'),
        expectedSyntax.replace(/\r\n/g, '\n'),
        'The protected runner syntax allowlist must match the normalized production skeleton.');

    const guardBinding = findTopLevelShardResultGuardBinding(sourceFile);
    assert.ok(guardBinding, 'run-e2e.js must actively require assertShardExecutedTests from e2e-shard-results.');
    assert.ok(
        guardBinding.declarationList.flags & ts.NodeFlags.Const,
        'Required E2E wiring bindings must be immutable and never reassigned.');
    const requireSymbol = checker.getSymbolAtLocation(guardBinding.requireIdentifier);
    assert.ok(
        requireSymbol &&
        !requireSymbol.declarations?.length,
        'The shard result guard import must use the intrinsic CommonJS require binding.');
    const shardNameDeclaration = findTopLevelShardNameDeclaration(sourceFile);
    const shardNameInitializer = shardNameDeclaration && getExpectedShardNameInitializer(shardNameDeclaration);
    assert.ok(
        shardNameDeclaration && shardNameInitializer,
        'The top-level shardName binding must use the intended shardName initializer expression.');
    const testSpecDeclaration = findConstVariableDeclaration(sourceFile.statements, 'testSpec');
    assert.ok(testSpecDeclaration, 'run-e2e.js must define the protected testSpec binding.');

    const main = sourceFile.statements.find(statement =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'main');
    assert.ok(main && ts.isFunctionDeclaration(main) && main.body, 'run-e2e.js must define main.');
    const mainSymbol = main.name && checker.getSymbolAtLocation(main.name);
    const mainInvocations = sourceFile.statements
        .map(getTopLevelMainInvocation)
        .filter((invocation): invocation is MainInvocation => invocation !== undefined);
    assert.ok(
        mainSymbol &&
        mainInvocations.length === 1 &&
        checker.getSymbolAtLocation(mainInvocations[0].main) === mainSymbol,
        'run-e2e.js must invoke main unconditionally through the top-level production binding.');
    assert.ok(
        isExpectedMainFailureHandler(mainInvocations[0].failureHandler, sourceFile, checker),
        'run-e2e.js must report main failures with a nonzero exit code.');

    const mainTryStatements = main.body.statements.filter((statement): statement is ts.TryStatement =>
        ts.isTryStatement(statement));
    assert.strictEqual(
        mainTryStatements.length,
        1,
        'The expected reachable main try path must contain exactly one exact ExTester run-tests command.');
    const mainTry = mainTryStatements[0];
    const mainTryIndex = main.body.statements.indexOf(mainTry);
    assert.ok(
        main.body.statements.length === 6 &&
        mainTryIndex === 3 &&
        isSingleVariableStatement(main.body.statements[0], 'recording', ts.NodeFlags.Let) &&
        isSingleVariableStatement(main.body.statements[1], 'testFailure', ts.NodeFlags.Let) &&
        isSingleVariableStatement(main.body.statements[2], 'cleanupFailed', ts.NodeFlags.Let),
        'The production main function must preserve the expected top-level control-flow skeleton.');

    const testFailureDeclaration = getSingleVariableDeclaration(main.body.statements[1], 'testFailure');
    const testFailureSymbol = testFailureDeclaration && checker.getSymbolAtLocation(testFailureDeclaration.name);
    assert.ok(testFailureSymbol, 'The production testFailure binding must resolve to a symbol.');

    const mainTryBody = mainTry.tryBlock.statements;
    const runTestsTryIndex = mainTryBody.length - 1;
    const runTestsTry = mainTryBody[runTestsTryIndex];
    assert.ok(
        ts.isTryStatement(runTestsTry) &&
        isRecordingStartStatement(mainTryBody[runTestsTryIndex - 1]),
        'The expected reachable main try path must contain exactly one exact ExTester run-tests command.');

    const runTestsStatements = runTestsTry.tryBlock.statements;
    const exTesterAwaitCandidates = runTestsStatements
        .map((statement, index) => ({ exTesterAwait: getSuccessfulExTesterAwait(statement), index }))
        .filter((candidate): candidate is { exTesterAwait: ExTesterAwait; index: number } =>
            candidate.exTesterAwait !== undefined);
    assert.strictEqual(
        exTesterAwaitCandidates.length,
        1,
        'The expected reachable main try path must contain exactly one exact ExTester run-tests command.');
    const { exTesterAwait, index: awaitIndex } = exTesterAwaitCandidates[0];
    const guardStatement = runTestsTry.tryBlock.statements[awaitIndex + 1];
    assert.ok(
        guardStatement &&
        ts.isExpressionStatement(guardStatement) &&
        isCallTo(guardStatement.expression, 'assertShardExecutedTests'),
        'The shard result guard must be a direct statement immediately after the successful ExTester await.');
    const guardCall = guardStatement.expression;
    assert.ok(ts.isIdentifier(guardCall.expression));
    assert.ok(
        hasExpectedRunTestsFailurePropagation(runTestsTry, checker, testFailureSymbol),
        'The run-tests catch must preserve run-tests failures.');
    assert.ok(
        mainTry.catchClause === undefined &&
        mainTry.finallyBlock &&
        hasExpectedCleanupFailurePropagation(mainTry.finallyBlock, checker, testFailureSymbol),
        'The main finally block must preserve cleanup failures.');
    assert.ok(
        hasExpectedTestFailureRethrow(main.body.statements[mainTryIndex + 1], checker, testFailureSymbol),
        'The main function must rethrow recorded test failures after finally cleanup.');

    const guardBindingSymbol = checker.getSymbolAtLocation(guardBinding.binding.name);
    assert.ok(guardBindingSymbol, 'The top-level shard result guard binding must resolve to a symbol.');
    assert.ok(
        checker.getSymbolAtLocation(guardCall.expression) === guardBindingSymbol,
        'The shard result guard call must resolve to the top-level shard result guard binding.');

    const runWithProcessTreeTimeoutBinding = findTopLevelRequiredBinding(
        sourceFile,
        './e2e-process-runner.cjs',
        'runWithProcessTreeTimeout');
    const runWithProcessTreeTimeoutSymbol = runWithProcessTreeTimeoutBinding &&
        checker.getSymbolAtLocation(runWithProcessTreeTimeoutBinding.name);
    assert.ok(
        runWithProcessTreeTimeoutSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.runWithProcessTreeTimeout) === runWithProcessTreeTimeoutSymbol,
        'The ExTester await must resolve to the production runWithProcessTreeTimeout binding.');

    const extesterCliDeclaration = findConstVariableDeclaration(sourceFile.statements, 'extesterCli');
    const extestEnvDeclaration = findConstVariableDeclaration(mainTry.tryBlock.statements, 'extestEnv');
    const runTestsArgsDeclaration = findConstVariableDeclaration(runTestsStatements, 'runTestsArgs');
    const getRunTestsTimeoutMsDeclaration = findTopLevelFunctionDeclaration(sourceFile, 'getRunTestsTimeoutMs');
    const testSpecInitializer = testSpecDeclaration && getExpectedTestSpecInitializer(testSpecDeclaration);
    assert.ok(
        testSpecInitializer &&
        checker.getSymbolAtLocation(testSpecInitializer.process) === undefined,
        'The top-level testSpec binding must use the intended testSpec initializer expression.');
    const extesterCliSymbol = extesterCliDeclaration && checker.getSymbolAtLocation(extesterCliDeclaration.name);
    const testSpecSymbol = testSpecDeclaration && checker.getSymbolAtLocation(testSpecDeclaration.name);
    const extestEnvSymbol = extestEnvDeclaration && checker.getSymbolAtLocation(extestEnvDeclaration.name);
    const runTestsArgsSymbol = runTestsArgsDeclaration && checker.getSymbolAtLocation(runTestsArgsDeclaration.name);
    const getRunTestsTimeoutMsSymbol = getRunTestsTimeoutMsDeclaration?.name &&
        checker.getSymbolAtLocation(getRunTestsTimeoutMsDeclaration.name);
    assert.ok(
        extesterCliSymbol &&
        testSpecSymbol &&
        extestEnvSymbol &&
        runTestsArgsSymbol &&
        getRunTestsTimeoutMsSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.process) === undefined &&
        checker.getSymbolAtLocation(exTesterAwait.runTestsArgs) === runTestsArgsSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.extesterCli) === extesterCliSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.testSpec) === testSpecSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.extestEnv) === extestEnvSymbol &&
        checker.getSymbolAtLocation(exTesterAwait.getRunTestsTimeoutMs) === getRunTestsTimeoutMsSymbol,
        'The ExTester await arguments must resolve to the production ExTester await dependencies.');

    const guardArguments = getExactShardResultArguments(guardCall);
    assert.ok(guardArguments, 'The shard result guard argument must contain exactly the shardName and results properties.');

    const shardNameSymbol = shardNameDeclaration && checker.getSymbolAtLocation(shardNameDeclaration.name);
    assert.ok(
        shardNameSymbol && checker.getShorthandAssignmentValueSymbol(guardArguments.shardName) === shardNameSymbol,
        'The shard result guard argument must resolve to the top-level shardName binding.');

    const readMochaResultsDeclaration = sourceFile.statements.find((statement): statement is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'readMochaResults');
    const readMochaResultsSymbol = readMochaResultsDeclaration?.name &&
        checker.getSymbolAtLocation(readMochaResultsDeclaration.name);
    assert.ok(
        readMochaResultsSymbol && checker.getSymbolAtLocation(guardArguments.readMochaResults) === readMochaResultsSymbol,
        'The shard result guard argument must resolve to the top-level readMochaResults declaration.');

    const sanitizePathSegmentDeclaration = findTopLevelFunctionDeclaration(sourceFile, 'sanitizePathSegment');
    const sanitizePathSegmentSymbol = sanitizePathSegmentDeclaration?.name &&
        checker.getSymbolAtLocation(sanitizePathSegmentDeclaration.name);
    assert.ok(
        sanitizePathSegmentSymbol &&
        checker.getSymbolAtLocation(shardNameInitializer.sanitizePathSegment) === sanitizePathSegmentSymbol &&
        checker.getSymbolAtLocation(shardNameInitializer.process) === undefined,
        'The top-level shardName binding must use the intended shardName initializer expression.');

    assert.ok(
        runTestsStatements.length === 4 &&
        awaitIndex === 2 &&
        isCallStatement(runTestsStatements[0], 'logStep', 'Running VS Code extension E2E tests'),
        'The production run-tests try must preserve the expected direct control-flow skeleton.');
}

function createRunnerProgram(source: string): { sourceFile: ts.SourceFile; checker: ts.TypeChecker } {
    const fileName = 'run-e2e.js';
    const options: ts.CompilerOptions = {
        allowJs: true,
        module: ts.ModuleKind.CommonJS,
        noLib: true,
        noResolve: true,
        target: ts.ScriptTarget.Latest,
    };
    const host = ts.createCompilerHost(options, true);
    host.fileExists = candidate => candidate === fileName;
    host.readFile = candidate => candidate === fileName ? source : undefined;
    host.getSourceFile = (candidate, languageVersion) =>
        candidate === fileName
            ? ts.createSourceFile(candidate, source, languageVersion, true, ts.ScriptKind.JS)
            : undefined;

    const program = ts.createProgram([fileName], options, host);
    const sourceFile = program.getSourceFile(fileName);
    assert.ok(sourceFile, 'The TypeScript program must contain run-e2e.js.');
    return { sourceFile, checker: program.getTypeChecker() };
}

function assertUniqueProtectedDeclarations(sourceFile: ts.SourceFile): void {
    const countDeclarations = (root: ts.Node, names: ReadonlySet<string>): Map<string, number> => {
        const declarationCounts = new Map([...names].map(name => [name, 0]));
        const recordBindingName = (name: ts.BindingName): void => {
            if (ts.isIdentifier(name)) {
                if (names.has(name.text)) {
                    declarationCounts.set(name.text, declarationCounts.get(name.text)! + 1);
                }
                return;
            }

            for (const element of name.elements) {
                if (ts.isBindingElement(element)) {
                    recordBindingName(element.name);
                }
            }
        };
        const visit = (node: ts.Node): void => {
            if ((ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node)) && node.name) {
                recordBindingName(node.name);
            }
            else if (ts.isVariableDeclaration(node) || ts.isParameter(node)) {
                recordBindingName(node.name);
            }

            node.forEachChild(visit);
        };
        root.forEachChild(visit);

        return declarationCounts;
    };

    const globalNames = new Set<string>([
        ...protectedFunctionNames,
        ...protectedTopLevelBindingNames,
        'process',
        'require',
    ]);
    const globalDeclarationCounts = countDeclarations(sourceFile, globalNames);
    for (const name of [...protectedFunctionNames, ...protectedTopLevelBindingNames]) {
        assert.strictEqual(
            globalDeclarationCounts.get(name),
            1,
            `The protected runner syntax allowlist requires exactly one protected declaration for ${name}.`);
    }
    for (const name of ['process', 'require']) {
        assert.strictEqual(
            globalDeclarationCounts.get(name),
            0,
            `The protected runner syntax allowlist requires the intrinsic ${name} binding.`);
    }

    const main = sourceFile.statements.find((statement): statement is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'main');
    assert.ok(main, 'The protected runner syntax allowlist requires the top-level main declaration.');
    const mainDeclarationCounts = countDeclarations(main, new Set(protectedMainBindingNames));
    for (const name of protectedMainBindingNames) {
        assert.strictEqual(
            mainDeclarationCounts.get(name),
            1,
            `The protected runner syntax allowlist requires exactly one protected declaration for ${name}.`);
    }
}

function getProtectedRunnerSyntax(sourceFile: ts.SourceFile): string {
    const printer = ts.createPrinter({
        newLine: ts.NewLineKind.LineFeed,
        removeComments: true,
    });
    const print = (node: ts.Node): string =>
        printer.printNode(ts.EmitHint.Unspecified, node, sourceFile);
    const executableStatements = sourceFile.statements
        .filter(statement => !ts.isFunctionDeclaration(statement))
        .map(print)
        .join('\n');
    const sections = [`[top-level executable]\n${executableStatements}`];

    for (const functionName of protectedFunctionNames) {
        const declaration = sourceFile.statements.find((statement): statement is ts.FunctionDeclaration =>
            ts.isFunctionDeclaration(statement) && statement.name?.text === functionName);
        sections.push(`[function ${functionName}]\n${declaration ? print(declaration) : '<missing>'}`);
    }

    return `${sections.join('\n\n')}\n`;
}

function findTopLevelShardResultGuardBinding(sourceFile: ts.SourceFile): {
    binding: ts.BindingElement;
    declarationList: ts.VariableDeclarationList;
    requireIdentifier: ts.Identifier;
} | undefined {
    for (const statement of sourceFile.statements) {
        if (!ts.isVariableStatement(statement)) {
            continue;
        }

        for (const declaration of statement.declarationList.declarations) {
            if (!declaration.initializer ||
                !isCallTo(declaration.initializer, 'require') ||
                declaration.initializer.arguments.length !== 1 ||
                !ts.isStringLiteral(declaration.initializer.arguments[0]) ||
                declaration.initializer.arguments[0].text !== './e2e-shard-results' ||
                !ts.isObjectBindingPattern(declaration.name)) {
                continue;
            }

            const binding = declaration.name.elements.find(element =>
                element.propertyName === undefined &&
                ts.isIdentifier(element.name) &&
                element.name.text === 'assertShardExecutedTests');
            if (binding && ts.isIdentifier(declaration.initializer.expression)) {
                return {
                    binding,
                    declarationList: statement.declarationList,
                    requireIdentifier: declaration.initializer.expression,
                };
            }
        }
    }

    return undefined;
}

function findTopLevelRequiredBinding(
    sourceFile: ts.SourceFile,
    modulePath: string,
    bindingName: string): ts.BindingElement | undefined {
    for (const statement of sourceFile.statements) {
        if (!ts.isVariableStatement(statement)) {
            continue;
        }

        for (const declaration of statement.declarationList.declarations) {
            if (!declaration.initializer ||
                !isCallTo(declaration.initializer, 'require') ||
                declaration.initializer.arguments.length !== 1 ||
                !ts.isStringLiteral(declaration.initializer.arguments[0]) ||
                declaration.initializer.arguments[0].text !== modulePath ||
                !ts.isObjectBindingPattern(declaration.name)) {
                continue;
            }

            return declaration.name.elements.find(element =>
                element.propertyName === undefined &&
                ts.isIdentifier(element.name) &&
                element.name.text === bindingName);
        }
    }

    return undefined;
}

function findTopLevelShardNameDeclaration(sourceFile: ts.SourceFile): ts.VariableDeclaration | undefined {
    return findConstVariableDeclaration(sourceFile.statements, 'shardName');
}

function findTopLevelFunctionDeclaration(sourceFile: ts.SourceFile, functionName: string): ts.FunctionDeclaration | undefined {
    return sourceFile.statements.find((statement): statement is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === functionName);
}

function findConstVariableDeclaration(statements: readonly ts.Statement[], variableName: string): ts.VariableDeclaration | undefined {
    for (const statement of statements) {
        if (!ts.isVariableStatement(statement) || !(statement.declarationList.flags & ts.NodeFlags.Const)) {
            continue;
        }

        const declaration = statement.declarationList.declarations.find(candidate =>
            ts.isIdentifier(candidate.name) && candidate.name.text === variableName);
        if (declaration) {
            return declaration;
        }
    }

    return undefined;
}

function getSuccessfulExTesterAwait(statement: ts.Statement): ExTesterAwait | undefined {
    if (!ts.isExpressionStatement(statement) ||
        !ts.isAwaitExpression(statement.expression) ||
        !isCallTo(statement.expression.expression, 'runWithProcessTreeTimeout')) {
        return undefined;
    }

    const call = statement.expression.expression;
    if (call.arguments.length !== 3 ||
        !ts.isPropertyAccessExpression(call.arguments[0]) ||
        !ts.isIdentifier(call.arguments[0].expression) ||
        call.arguments[0].expression.text !== 'process' ||
        call.arguments[0].name.text !== 'execPath' ||
        !ts.isIdentifier(call.arguments[1]) ||
        call.arguments[1].text !== 'runTestsArgs' ||
        !ts.isObjectLiteralExpression(call.arguments[2])) {
        return undefined;
    }

    const options = call.arguments[2];
    const spawnOptionsProperty = options.properties.find(property =>
        ts.isPropertyAssignment(property) &&
        ts.isIdentifier(property.name) &&
        property.name.text === 'spawnOptions' &&
        ts.isObjectLiteralExpression(property.initializer));
    const timeoutProperty = options.properties.find(property =>
        ts.isPropertyAssignment(property) &&
        ts.isIdentifier(property.name) &&
        property.name.text === 'timeout' &&
        isCallTo(property.initializer, 'getRunTestsTimeoutMs') &&
        property.initializer.arguments.length === 0);
    if (!spawnOptionsProperty ||
        !ts.isPropertyAssignment(spawnOptionsProperty) ||
        !ts.isObjectLiteralExpression(spawnOptionsProperty.initializer) ||
        !timeoutProperty ||
        !ts.isPropertyAssignment(timeoutProperty) ||
        !ts.isCallExpression(timeoutProperty.initializer) ||
        !ts.isIdentifier(timeoutProperty.initializer.expression)) {
        return undefined;
    }

    const environmentProperty = spawnOptionsProperty.initializer.properties.find(property =>
        ts.isPropertyAssignment(property) &&
        ts.isIdentifier(property.name) &&
        property.name.text === 'env' &&
        ts.isObjectLiteralExpression(property.initializer));
    if (!environmentProperty ||
        !ts.isPropertyAssignment(environmentProperty) ||
        !ts.isObjectLiteralExpression(environmentProperty.initializer)) {
        return undefined;
    }

    const extestEnvSpread = environmentProperty.initializer.properties.find(property =>
        ts.isSpreadAssignment(property) &&
        ts.isIdentifier(property.expression) &&
        property.expression.text === 'extestEnv');
    if (!extestEnvSpread ||
        !ts.isSpreadAssignment(extestEnvSpread) ||
        !ts.isIdentifier(extestEnvSpread.expression) ||
        !ts.isIdentifier(call.expression)) {
        return undefined;
    }

    const runTestsArgsDeclaration = findConstVariableDeclaration(
        statement.parent && ts.isBlock(statement.parent) ? statement.parent.statements : [],
        'runTestsArgs');
    if (!runTestsArgsDeclaration ||
        !runTestsArgsDeclaration.initializer ||
        !ts.isArrayLiteralExpression(runTestsArgsDeclaration.initializer)) {
        return undefined;
    }

    const commandArguments = runTestsArgsDeclaration.initializer.elements;
    if (commandArguments.length < 3 ||
        !ts.isIdentifier(commandArguments[0]) ||
        commandArguments[0].text !== 'extesterCli' ||
        !ts.isStringLiteral(commandArguments[1]) ||
        commandArguments[1].text !== 'run-tests' ||
        !ts.isIdentifier(commandArguments[2]) ||
        commandArguments[2].text !== 'testSpec') {
        return undefined;
    }

    return {
        runWithProcessTreeTimeout: call.expression,
        process: call.arguments[0].expression,
        runTestsArgs: call.arguments[1],
        extesterCli: commandArguments[0],
        testSpec: commandArguments[2],
        extestEnv: extestEnvSpread.expression,
        getRunTestsTimeoutMs: timeoutProperty.initializer.expression,
    };
}

function getExpectedShardNameInitializer(declaration: ts.VariableDeclaration): {
    sanitizePathSegment: ts.Identifier;
    process: ts.Identifier;
    environmentValue: ts.PropertyAccessExpression;
} | undefined {
    if (!declaration.initializer ||
        !isCallTo(declaration.initializer, 'sanitizePathSegment') ||
        declaration.initializer.arguments.length !== 1 ||
        !ts.isIdentifier(declaration.initializer.expression)) {
        return undefined;
    }

    const value = declaration.initializer.arguments[0];
    if (!ts.isBinaryExpression(value) ||
        value.operatorToken.kind !== ts.SyntaxKind.BarBarToken ||
        !ts.isStringLiteral(value.right) ||
        value.right.text !== 'all' ||
        !ts.isPropertyAccessExpression(value.left) ||
        value.left.name.text !== 'ASPIRE_EXTENSION_E2E_SHARD' ||
        !ts.isPropertyAccessExpression(value.left.expression) ||
        value.left.expression.name.text !== 'env' ||
        !ts.isIdentifier(value.left.expression.expression) ||
        value.left.expression.expression.text !== 'process') {
        return undefined;
    }

    return {
        sanitizePathSegment: declaration.initializer.expression,
        process: value.left.expression.expression,
        environmentValue: value.left,
    };
}

function getExpectedTestSpecInitializer(declaration: ts.VariableDeclaration): {
    process: ts.Identifier;
} | undefined {
    const value = declaration.initializer;
    if (!value ||
        !ts.isBinaryExpression(value) ||
        value.operatorToken.kind !== ts.SyntaxKind.BarBarToken ||
        !ts.isStringLiteral(value.right) ||
        value.right.text !== 'out/test-e2e/**/*.e2e.test.js' ||
        !ts.isPropertyAccessExpression(value.left) ||
        value.left.name.text !== 'ASPIRE_EXTENSION_E2E_SPEC' ||
        !ts.isPropertyAccessExpression(value.left.expression) ||
        value.left.expression.name.text !== 'env' ||
        !ts.isIdentifier(value.left.expression.expression) ||
        value.left.expression.expression.text !== 'process') {
        return undefined;
    }

    return { process: value.left.expression.expression };
}

function getExactShardResultArguments(call: ts.CallExpression): {
    shardName: ts.ShorthandPropertyAssignment;
    readMochaResults: ts.Identifier;
} | undefined {
    if (call.arguments.length !== 1 || !ts.isObjectLiteralExpression(call.arguments[0])) {
        return undefined;
    }

    const options = call.arguments[0];
    if (options.properties.length !== 2) {
        return undefined;
    }

    const shardNameProperty = options.properties.find(property =>
        ts.isShorthandPropertyAssignment(property) && property.name.text === 'shardName');
    const resultProperty = options.properties.find(property =>
        ts.isPropertyAssignment(property) &&
        ts.isIdentifier(property.name) &&
        property.name.text === 'results' &&
        isCallTo(property.initializer, 'readMochaResults') &&
        property.initializer.arguments.length === 0);
    if (!shardNameProperty ||
        !ts.isShorthandPropertyAssignment(shardNameProperty) ||
        !resultProperty ||
        !ts.isPropertyAssignment(resultProperty) ||
        !ts.isCallExpression(resultProperty.initializer) ||
        !ts.isIdentifier(resultProperty.initializer.expression)) {
        return undefined;
    }

    return {
        shardName: shardNameProperty,
        readMochaResults: resultProperty.initializer.expression,
    };
}

function isCallTo(node: ts.Node, functionName: string): node is ts.CallExpression {
    return ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === functionName;
}

function getTopLevelMainInvocation(statement: ts.Statement): MainInvocation | undefined {
    if (!ts.isExpressionStatement(statement) ||
        !ts.isCallExpression(statement.expression) ||
        !ts.isPropertyAccessExpression(statement.expression.expression) ||
        statement.expression.expression.name.text !== 'catch' ||
        !ts.isCallExpression(statement.expression.expression.expression) ||
        statement.expression.expression.expression.arguments.length !== 0 ||
        !ts.isIdentifier(statement.expression.expression.expression.expression) ||
        statement.expression.expression.expression.expression.text !== 'main' ||
        statement.expression.arguments.length !== 1) {
        return undefined;
    }

    return {
        main: statement.expression.expression.expression.expression,
        failureHandler: statement.expression.arguments[0],
    };
}

function isExpectedMainFailureHandler(
    handler: ts.Expression,
    sourceFile: ts.SourceFile,
    checker: ts.TypeChecker): boolean {
    if (!ts.isArrowFunction(handler) ||
        handler.parameters.length !== 1 ||
        !ts.isIdentifier(handler.parameters[0].name) ||
        !ts.isBlock(handler.body) ||
        handler.body.statements.length !== 2 ||
        handler.body.statements[0].getText(sourceFile) !==
            'console.error(error instanceof Error ? error.stack ?? error.message : String(error));') {
        return false;
    }

    const exitCodeStatement = handler.body.statements[1];
    return ts.isExpressionStatement(exitCodeStatement) &&
        ts.isBinaryExpression(exitCodeStatement.expression) &&
        exitCodeStatement.expression.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isPropertyAccessExpression(exitCodeStatement.expression.left) &&
        ts.isIdentifier(exitCodeStatement.expression.left.expression) &&
        exitCodeStatement.expression.left.expression.text === 'process' &&
        checker.getSymbolAtLocation(exitCodeStatement.expression.left.expression) === undefined &&
        exitCodeStatement.expression.left.name.text === 'exitCode' &&
        ts.isNumericLiteral(exitCodeStatement.expression.right) &&
        exitCodeStatement.expression.right.text === '1';
}

function getSingleVariableDeclaration(
    statement: ts.Statement,
    variableName: string): ts.VariableDeclaration | undefined {
    if (!ts.isVariableStatement(statement) ||
        statement.declarationList.declarations.length !== 1) {
        return undefined;
    }

    const declaration = statement.declarationList.declarations[0];
    return ts.isIdentifier(declaration.name) && declaration.name.text === variableName
        ? declaration
        : undefined;
}

function isSingleVariableStatement(
    statement: ts.Statement,
    variableName: string,
    declarationFlag: ts.NodeFlags): boolean {
    return ts.isVariableStatement(statement) &&
        getSingleVariableDeclaration(statement, variableName) !== undefined &&
        (statement.declarationList.flags & declarationFlag) !== 0;
}

function isRecordingStartStatement(statement: ts.Statement | undefined): boolean {
    return !!statement &&
        ts.isExpressionStatement(statement) &&
        ts.isBinaryExpression(statement.expression) &&
        statement.expression.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isIdentifier(statement.expression.left) &&
        statement.expression.left.text === 'recording' &&
        isCallTo(statement.expression.right, 'startRecording') &&
        statement.expression.right.arguments.length === 0;
}

function isCallStatement(statement: ts.Statement, functionName: string, stringArgument: string): boolean {
    return ts.isExpressionStatement(statement) &&
        isCallTo(statement.expression, functionName) &&
        statement.expression.arguments.length === 1 &&
        ts.isStringLiteral(statement.expression.arguments[0]) &&
        statement.expression.arguments[0].text === stringArgument;
}

function hasExpectedRunTestsFailurePropagation(
    runTestsTry: ts.TryStatement,
    checker: ts.TypeChecker,
    testFailureSymbol: ts.Symbol): boolean {
    const catchClause = runTestsTry.catchClause;
    if (!catchClause ||
        runTestsTry.finallyBlock ||
        !catchClause.variableDeclaration ||
        !ts.isIdentifier(catchClause.variableDeclaration.name) ||
        catchClause.block.statements.length !== 1) {
        return false;
    }

    const errorSymbol = checker.getSymbolAtLocation(catchClause.variableDeclaration.name);
    return !!errorSymbol &&
        isSymbolAssignment(catchClause.block.statements[0], checker, testFailureSymbol, errorSymbol);
}

function hasExpectedCleanupFailurePropagation(
    finallyBlock: ts.Block,
    checker: ts.TypeChecker,
    testFailureSymbol: ts.Symbol): boolean {
    const cleanupFailureIf = finallyBlock.statements.at(-1);
    if (!cleanupFailureIf ||
        !ts.isIfStatement(cleanupFailureIf) ||
        cleanupFailureIf.elseStatement ||
        !ts.isBlock(cleanupFailureIf.thenStatement) ||
        cleanupFailureIf.thenStatement.statements.length !== 3) {
        return false;
    }

    const cleanupFailureDeclaration = getSingleVariableDeclaration(
        cleanupFailureIf.thenStatement.statements[1],
        'cleanupFailure');
    const cleanupFailureSymbol = cleanupFailureDeclaration &&
        checker.getSymbolAtLocation(cleanupFailureDeclaration.name);
    const preserveExistingFailure = cleanupFailureIf.thenStatement.statements[2];
    if (!cleanupFailureSymbol ||
        !ts.isIfStatement(preserveExistingFailure) ||
        !ts.isIdentifier(preserveExistingFailure.expression) ||
        checker.getSymbolAtLocation(preserveExistingFailure.expression) !== testFailureSymbol ||
        !ts.isBlock(preserveExistingFailure.thenStatement) ||
        preserveExistingFailure.thenStatement.statements.length !== 1 ||
        !isConsoleErrorOfSymbol(
            preserveExistingFailure.thenStatement.statements[0],
            checker,
            cleanupFailureSymbol) ||
        !preserveExistingFailure.elseStatement ||
        !ts.isBlock(preserveExistingFailure.elseStatement) ||
        preserveExistingFailure.elseStatement.statements.length !== 1) {
        return false;
    }

    return isSymbolAssignment(
        preserveExistingFailure.elseStatement.statements[0],
        checker,
        testFailureSymbol,
        cleanupFailureSymbol);
}

function hasExpectedTestFailureRethrow(
    statement: ts.Statement,
    checker: ts.TypeChecker,
    testFailureSymbol: ts.Symbol): boolean {
    if (!ts.isIfStatement(statement) ||
        statement.elseStatement ||
        !ts.isIdentifier(statement.expression) ||
        checker.getSymbolAtLocation(statement.expression) !== testFailureSymbol ||
        !ts.isBlock(statement.thenStatement) ||
        statement.thenStatement.statements.length < 2) {
        return false;
    }

    const printDiagnostics = statement.thenStatement.statements[0];
    const rethrow = statement.thenStatement.statements.at(-1);
    return ts.isExpressionStatement(printDiagnostics) &&
        isCallTo(printDiagnostics.expression, 'printFailureDiagnosticsSummary') &&
        printDiagnostics.expression.arguments.length === 0 &&
        !!rethrow &&
        ts.isThrowStatement(rethrow) &&
        !!rethrow.expression &&
        ts.isIdentifier(rethrow.expression) &&
        checker.getSymbolAtLocation(rethrow.expression) === testFailureSymbol;
}

function isSymbolAssignment(
    statement: ts.Statement,
    checker: ts.TypeChecker,
    leftSymbol: ts.Symbol,
    rightSymbol: ts.Symbol): boolean {
    return ts.isExpressionStatement(statement) &&
        ts.isBinaryExpression(statement.expression) &&
        statement.expression.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isIdentifier(statement.expression.left) &&
        checker.getSymbolAtLocation(statement.expression.left) === leftSymbol &&
        ts.isIdentifier(statement.expression.right) &&
        checker.getSymbolAtLocation(statement.expression.right) === rightSymbol;
}

function isConsoleErrorOfSymbol(
    statement: ts.Statement,
    checker: ts.TypeChecker,
    argumentSymbol: ts.Symbol): boolean {
    if (!ts.isExpressionStatement(statement) ||
        !ts.isCallExpression(statement.expression) ||
        !ts.isPropertyAccessExpression(statement.expression.expression) ||
        !ts.isIdentifier(statement.expression.expression.expression) ||
        statement.expression.expression.expression.text !== 'console' ||
        statement.expression.expression.name.text !== 'error' ||
        statement.expression.arguments.length !== 1 ||
        !ts.isIdentifier(statement.expression.arguments[0])) {
        return false;
    }

    return checker.getSymbolAtLocation(statement.expression.arguments[0]) === argumentSymbol;
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
        const runner = readRunnerSource(extensionRoot);
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
        assert.ok(runner.indexOf("throw new Error('vscode-extension-tester must be pinned") < runRootDeclaration);
        assert.ok(runner.indexOf('const downloadCacheRoot =') < runRootDeclaration);
        assert.ok(runner.indexOf('const vscodeVersion = resolveCachedVsCodeVersion(') < runRootDeclaration);

        // The directory preparation that used to sit at module scope is now called from `main()`,
        // inside the `try` whose `finally` tears the run root down.
        const mainStart = runner.indexOf('async function main()');
        const mainBody = runner.slice(mainStart, runner.indexOf('\n  finally {', mainStart));
        assert.ok(mainBody.includes('prepareRunDirectories();'));
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
        const runner = readRunnerSource(extensionRoot);

        assert.ok(runner.includes("'--disable-keytar'"));
        assert.ok(runner.includes("'--use-inmemory-secretstorage'"));
        assert.ok(runner.includes("'--password-store=basic'"));
        assert.ok(runner.includes("'--disable-extension', 'vscode.github-authentication'"));
        assert.ok(runner.includes("'--disable-extension', 'vscode.microsoft-authentication'"));
    });

    test('opens the E2E workspace as a VS Code startup folder', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);

        assert.ok(runner.includes("const { runWithProcessTreeTimeout } = require('./e2e-process-runner.cjs');"));
        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS'));
        assert.ok(runner.includes('await runWithProcessTreeTimeout(process.execPath'));
        assert.ok(runner.includes('getRunTestsTimeoutMs()'));
        assert.ok(runner.includes('2400000'));
        assert.ok(runner.includes("spawnSync('taskkill'"));
        assert.ok(runner.includes('process.kill(-pid, signal)'));
    });

    test('checks opted-in shard results after ExTester exits successfully', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

        assertShardResultGuardWiring(runner);
    });

    test('accepts the runner and wiring snapshot with Windows line endings', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(/\r?\n/g, '\r\n');
        const expectedSyntax = fs.readFileSync(
            path.join(extensionRoot, 'src', 'test', 'e2eLaunchProfile.runner-wiring.txt'),
            'utf8')
            .replace(/\r?\n/g, '\r\n');

        assertShardResultGuardWiring(runner, expectedSyntax);
    });

    test('applies protected runner mutations with Windows line endings', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = normalizeLineEndings(
            readRunnerSource(extensionRoot)
                .replace(/\r?\n/g, '\r\n'))
            .replace(
                "main().catch(error => {\n  console.error(error instanceof Error ? error.stack ?? error.message : String(error));\n  process.exitCode = 1;\n});",
                'main().catch(() => {});');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a commented-out shard result guard import', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const { assertShardExecutedTests } = require('./e2e-shard-results');",
                "// const { assertShardExecutedTests } = require('./e2e-shard-results');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a commented-out shard result guard invocation', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      assertShardExecutedTests({ shardName, results: readMochaResults() });',
                '      // assertShardExecutedTests({ shardName, results: readMochaResults() });');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard hidden in an unreachable branch', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      assertShardExecutedTests({ shardName, results: readMochaResults() });',
                '      if (false) {\n        assertShardExecutedTests({ shardName, results: readMochaResults() });\n      }');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects the complete run-tests path when it is unreachable', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "    recording = startRecording();\n    try {\n      logStep('Running VS Code extension E2E tests');",
                "    recording = startRecording();\n    if (false) {\n    try {\n      logStep('Running VS Code extension E2E tests');")
            .replace(
                '    }\n    completedTests = true;',
                '    }\n    }\n    completedTests = true;');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a disabled main invocation', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "main().catch(error => {\n  console.error(error instanceof Error ? error.stack ?? error.message : String(error));\n  process.exitCode = 1;\n});",
                "if (false) {\n  main().catch(error => {\n    console.error(error instanceof Error ? error.stack ?? error.message : String(error));\n    process.exitCode = 1;\n  });\n}");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects an unconditional return before the guarded run-tests path', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '  let cleanupFailed = false;',
                '  let cleanupFailed = false;\n  return;');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a conditional return that always terminates before the guarded run-tests path', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    assertSpecMatches(testSpec);',
                '    assertSpecMatches(testSpec);\n    if (true) return;');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a successful process exit before the guarded run-tests path', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    assertSpecMatches(testSpec);',
                '    assertSpecMatches(testSpec);\n    process.exit(0);');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a successful process exit before the top-level main invocation', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                'main().catch(error => {',
                'process.exit(0);\nmain().catch(error => {');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects process termination through element access in a protected initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const artifactsDir = path.join(extensionRoot, '.test-artifacts');",
                "const artifactsDir = (process['exit'](0), path.join(extensionRoot, '.test-artifacts'));");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects aliased process termination in a protected initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const artifactsDir = path.join(extensionRoot, '.test-artifacts');",
                "const terminateProcess = process.exit.bind(process);\nconst artifactsDir = (terminateProcess(0), path.join(extensionRoot, '.test-artifacts'));");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard after a different awaited command', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      await runWithProcessTreeTimeout(process.execPath, runTestsArgs, {',
                '      await Promise.resolve(process.execPath, runTestsArgs, {');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects an ExTester await through a shadowed fake runner', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      const runWithProcessTreeTimeout = async () => fs.writeFileSync(path.join(resultsDir, 'mocha.json'), JSON.stringify({ stats: { tests: 1, passes: 1, pending: 0 } }));");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects shadowed ExTester await dependencies', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      const extesterCli = 'fake';\n      const testSpec = 'fake';\n      const extestEnv = {};\n      const getRunTestsTimeoutMs = () => 1;");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard call through a shadowed no-op binding', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      const assertShardExecutedTests = () => undefined;");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a reassigned shard result guard binding', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const { assertShardExecutedTests } = require('./e2e-shard-results');",
                "let { assertShardExecutedTests } = require('./e2e-shard-results');")
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      assertShardExecutedTests = () => undefined;");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard call with a shadowed shard name', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      const shardName = 'not-resource-debugger';");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a neutralized hardcoded shard name initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "const shardName = sanitizePathSegment('not-resource-debugger');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard call with a shadowed Mocha result reader', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      const readMochaResults = () => ({ stats: { tests: 1, pending: 0 } });");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a duplicate protected function declaration', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                'function sanitizePathSegment(value) {',
                "function readMochaResults() {\n  return { stats: { tests: 1, passes: 1, pending: 0 } };\n}\n\nfunction sanitizePathSegment(value) {");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects replacing the protected Mocha JSON reader', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "function readJsonIfExists(filePath) {\n  if (!fs.existsSync(filePath)) {\n    return undefined;\n  }\n\n  try {\n    return JSON.parse(fs.readFileSync(filePath, 'utf8'));\n  }\n  catch (error) {\n    console.warn(`Failed to parse ${filePath}: ${error instanceof Error ? error.message : String(error)}`);\n    return undefined;\n  }\n}",
                "function readJsonIfExists() {\n  return { stats: { tests: 1, passes: 1, pending: 0 } };\n}");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a duplicate protected Mocha JSON reader declaration', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                'function findLatestExtensionLogPath() {',
                "function readJsonIfExists() {\n  return { stats: { tests: 1, passes: 1, pending: 0 } };\n}\n\nfunction findLatestExtensionLogPath() {");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a reassigned Mocha result reader', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      readMochaResults = () => ({ stats: { tests: 1, passes: 1, pending: 0 } });");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects an inner catch that swallows the shard result guard failure', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    catch (error) {\n      testFailure = error;\n    }',
                '    catch (error) {\n    }');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects disabling the post-finally test failure throw', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    throw testFailure;',
                '    return;');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects swallowing cleanup failures in the main finally block', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      else {\n        testFailure = cleanupFailure;\n      }',
                '      else {\n        console.error(cleanupFailure);\n      }');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects clearing the recorded failure in the main finally block', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    if (cleanupErrors.length > 0) {',
                '    testFailure = undefined;\n    if (cleanupErrors.length > 0) {');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects returning from the main finally block', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '    if (cleanupErrors.length > 0) {',
                '    return;\n    if (cleanupErrors.length > 0) {');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a top-level main handler that keeps a zero exit code', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "main().catch(error => {\n  console.error(error instanceof Error ? error.stack ?? error.message : String(error));\n  process.exitCode = 1;\n});",
                'main().catch(() => {});');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a Mocha result reader reassigned through object destructuring', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      ({ readMochaResults } = { readMochaResults: () => ({ stats: { tests: 1, passes: 1, pending: 0 } }) });");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a Mocha result reader reassigned through array destructuring', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      [readMochaResults] = [() => ({ stats: { tests: 1, passes: 1, pending: 0 } })];");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a process runner reassigned through a for-of initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      for (runWithProcessTreeTimeout of [async () => undefined]) {}");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a Mocha result reader reassigned through a for-in initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "      logStep('Running VS Code extension E2E tests');",
                "      logStep('Running VS Code extension E2E tests');\n      for (readMochaResults in { replacement: true }) {}");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects CommonJS require reassignment before the guard import', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const { assertShardExecutedTests } = require('./e2e-shard-results');",
                "require = () => ({ assertShardExecutedTests: () => undefined });\nconst { assertShardExecutedTests } = require('./e2e-shard-results');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a CommonJS require declaration that replaces the intrinsic binding', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const { assertShardExecutedTests } = require('./e2e-shard-results');",
                "var require = () => ({ assertShardExecutedTests: () => undefined });\nconst { assertShardExecutedTests } = require('./e2e-shard-results');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a hardcoded test spec initializer', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const testSpec = process.env.ASPIRE_EXTENSION_E2E_SPEC || 'out/test-e2e/**/*.e2e.test.js';",
                "const testSpec = 'out/test-e2e/resourceDebugger.e2e.test.js';");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects direct shard environment reassignment before shard initialization', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "process.env.ASPIRE_EXTENSION_E2E_SHARD = 'all';\nconst shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects Object.assign shard environment mutation before shard initialization', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "Object.assign(process.env, { ASPIRE_EXTENSION_E2E_SHARD: 'all' });\nconst shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard environment alias before shard initialization', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "const shardEnvironment = process.env;\nshardEnvironment.ASPIRE_EXTENSION_E2E_SHARD = 'all';\nconst shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a destructured shard environment alias before shard initialization', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "const { env: shardEnvironment } = process;\nshardEnvironment.ASPIRE_EXTENSION_E2E_SHARD = 'all';\nconst shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a reflected shard environment alias before shard initialization', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                "const shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');",
                "const shardEnvironment = Reflect.get(process, 'env');\nshardEnvironment.ASPIRE_EXTENSION_E2E_SHARD = 'all';\nconst shardName = sanitizePathSegment(process.env.ASPIRE_EXTENSION_E2E_SHARD || 'all');");

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects a shard result guard argument with a trailing override spread', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      assertShardExecutedTests({ shardName, results: readMochaResults() });',
                '      assertShardExecutedTests({ shardName, results: readMochaResults(), ...{ shardName: null } });');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('rejects duplicate shard result guard properties', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot)
            .replace(
                '      assertShardExecutedTests({ shardName, results: readMochaResults() });',
                '      assertShardExecutedTests({ shardName, results: readMochaResults(), shardName: null });');

        assert.throws(
            () => assertShardResultGuardWiring(runner),
            /protected runner syntax allowlist/);
    });

    test('wires the ExTester process lifecycle into the extracted runner', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);

        assert.ok(runner.includes('assertWorkspaceRootSafeForDeletion();'));
        assert.ok(runner.includes('ASPIRE_EXTENSION_E2E_ALLOW_EXTERNAL_WORKSPACE_ROOT_CLEANUP'));
        assert.ok(runner.includes('.aspire-extension-e2e-workspace'));
        assert.ok(runner.includes('Refusing to delete dangerous E2E workspace root'));
    });

    test('redacts sensitive dashboard URLs from runner failure diagnostics', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');
        const resourceGroupsInstallIndex = runner.indexOf("displayName: 'Azure Resource Groups'");
        const functionsInstallIndex = runner.indexOf("displayName: 'Azure Functions'");
        const runStepIndex = workflow.indexOf('- name: Run extension E2E tests');
        const uploadStepIndex = workflow.indexOf('- name: Upload E2E diagnostics');
        const runStep = workflow.slice(runStepIndex, uploadStepIndex);

        assert.ok(workflow.includes('shardName: azure-functions'));
        assert.ok(workflow.includes('installAzureFunctions: true'));
        assert.ok(workflow.includes("core_tools_version='4.12.1'"));
        assert.ok(workflow.includes('faf8fb8d50b5293df338bec70594b12f45730e9fe251805298859b2238cf627e'));
        assert.ok(workflow.includes('vscode-azureresourcegroups/0.12.7/vspackage'));
        assert.ok(workflow.includes('e4a2e7ab012de3777e1ac1781e2c25d65f150ad6f3770e8cfcc5a3d3658df35a'));
        assert.ok(workflow.includes('vscode-azurefunctions/1.22.0/vspackage'));
        assert.ok(workflow.includes('146aede06f941b07a55c5aebd28c5e3df684d57b07cf6f9ebf90d7bb8ecd41a2'));
        assert.ok(workflow.includes('ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS=true'));
        assert.ok(resourceGroupsInstallIndex >= 0);
        assert.ok(functionsInstallIndex > resourceGroupsInstallIndex);
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_RESOURCE_GROUPS_VSIX')"));
        assert.ok(runner.includes("path: resolveRequiredVsixPath('ASPIRE_EXTENSION_E2E_AZURE_FUNCTIONS_VSIX')"));
        assert.ok(runStep.includes('ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE: ${{ matrix.advisoryIssue }}'));
        assert.strictEqual(runStep.includes('continue-on-error:'), false);
    });

    test('wires structured E2E harness failures into advisory handling', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

        assert.ok(runner.includes("const { shouldAllowAdvisoryTestFailure } = require('./e2e-process-failure.cjs');"));
        assert.ok(runner.includes("const advisoryIssue = process.env.ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE || '';"));
        assert.ok(runner.includes('let cleanupFailed = false;'));
        assert.ok(runner.includes('cleanupFailed = true;'));
        assert.ok(runner.includes('shouldAllowAdvisoryTestFailure(testFailure, readMochaResults(), cleanupFailed)'));
        assert.ok(runner.includes('completed test failures tracked by ${advisoryIssue}. Diagnostics were uploaded for investigation.'));
        assert.strictEqual(runner.includes('ASPIRE_EXTENSION_E2E_ALLOW_TEST_FAILURE'), false);
        assert.strictEqual(runner.includes('completedTests'), false);
    });

    test('keeps Linux E2E recordings for successful runs by default', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8');

        assert.ok(workflow.includes("ASPIRE_EXTENSION_E2E_RECORDING_MODE: ${{ matrix.useXvfb && 'always' || 'off' }}"));
        assert.ok(workflow.includes('Linux CI keeps recordings by default; Windows shards upload screenshots and logs only.'));
    });

    test('waits for ffmpeg to flush before reporting E2E recordings as saved', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);

        assert.ok(runner.includes('#pragma warning disable ASPIREINTERACTION001'));
        assert.ok(runner.includes('new InteractionInput'));
        assert.ok(runner.includes('InputType.SecretText'));
    });

    test('launches VS Code E2E tests with telemetry disabled before extension activation', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
        const settings = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'test-e2e', 'settings.json'), 'utf8'));

        assert.strictEqual(settings['telemetry.telemetryLevel'], 'off');
        assert.ok(runner.includes("'--disable-telemetry'"));
    });

    test('does not seed dashboard launch preferences in the E2E harness', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
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

    test('waits for resource debugger child processes in parallel', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const nodeProofTest = getTestBlock(resourceDebugger, 'stopping debugging tears down the Node resource process tree');

        assert.ok(nodeProofTest.includes('await Promise.all(['), 'The two process-exit waits must share the same wall-clock budget.');
        assert.ok(nodeProofTest.indexOf('waitForProcessExit(debuggeePid') > nodeProofTest.indexOf('await Promise.all(['));
        assert.ok(nodeProofTest.indexOf('waitForProcessExit(childPid') > nodeProofTest.indexOf('waitForProcessExit(debuggeePid'));
        assert.ok(nodeProofTest.indexOf(']);') > nodeProofTest.indexOf('waitForProcessExit(childPid'));
    });

    test('uses bounded operation and cleanup deadlines for every resource debugger phase', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);

        assertResourceDebuggerUsesBoundedDeadlines(resourceDebugger);
    });

    test('gives resource debugger shards enough runner time for their bounded suite budget', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const workflow = normalizeLineEndings(
            fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml'), 'utf8'));
        const contributing = normalizeLineEndings(
            fs.readFileSync(path.join(extensionRoot, 'CONTRIBUTING.md'), 'utf8'));
        const proofTimeoutMatch = /const resourceDebuggerDeadlineTimeoutMs = (\d+);/.exec(resourceDebugger);
        const teardownTimeoutMatch = /const resourceDebuggerTeardownTimeoutMs = (\d+);/.exec(resourceDebugger);
        assert.ok(proofTimeoutMatch);
        assert.ok(teardownTimeoutMatch);

        // The first two tests share one proof, the third test runs another proof, and every test
        // executes teardown. Keep additional process-runner time for Mocha and browser shutdown.
        const worstCaseSuiteTimeoutMs = (2 * Number(proofTimeoutMatch[1])) + (3 * Number(teardownTimeoutMatch[1]));
        const resourceAwareDefaultTimeoutMatch = /const defaultRunTestsTimeoutMs = includeNodeResourceFixture \? (\d+) : 2400000;/.exec(runner);
        assert.ok(resourceAwareDefaultTimeoutMatch, 'Expected the local runner default to account for the resource debugger spec.');
        assert.ok(
            Number(resourceAwareDefaultTimeoutMatch[1]) >= worstCaseSuiteTimeoutMs + 2400000 + 300000,
            'The full-suite runner timeout must preserve the previous suite budget plus the resource debugger budget and cleanup slack.');
        const resourceShardBlocks = workflow
            .split('\n          - name: ')
            .filter(block => block.includes('\n            shardName: resource-debugger\n'));
        assert.strictEqual(resourceShardBlocks.length, 2, 'Expected Linux and Windows resource debugger matrix entries.');
        for (const block of resourceShardBlocks) {
            const runnerTimeoutMatch = /\n            runTestsTimeoutMs: (\d+)(?:\n|$)/.exec(block);
            assert.ok(runnerTimeoutMatch, 'Expected the resource debugger matrix entry to override the process-runner timeout.');
            assert.ok(
                Number(runnerTimeoutMatch[1]) >= worstCaseSuiteTimeoutMs + 300000,
                'The resource debugger process-runner timeout must exceed its worst-case Mocha budget by at least five minutes.');
            const jobTimeoutMatch = /\n            timeoutMinutes: (\d+)(?:\n|$)/.exec(block);
            assert.ok(jobTimeoutMatch, 'Expected the resource debugger matrix entry to override the job timeout.');
            assert.ok(
                Number(jobTimeoutMatch[1]) >= (Number(runnerTimeoutMatch[1]) / 60000) + 30,
                'The resource debugger job timeout must leave at least 30 minutes for setup and diagnostics.');
        }
        assert.ok(
            workflow.includes('ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS: ${{ matrix.runTestsTimeoutMs || 2400000 }}'),
            'The workflow must pass the per-shard process-runner timeout override.');
        assert.ok(
            workflow.includes('timeout-minutes: ${{ matrix.timeoutMinutes || 75 }}'),
            'The workflow job must honor the per-shard timeout override.');
        assert.ok(
            contributing.includes('ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS=3600000 ASPIRE_EXTENSION_E2E_SHARD=resource-debugger'),
            'The documented resource debugger command must use the same process-runner timeout as CI.');
        assert.ok(
            contributing.includes(`ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS=${resourceAwareDefaultTimeoutMatch[1]} ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e`),
            'The documented full-suite command must expose its larger resource-aware timeout.');
    });

    test('applies resource debugger mutations with Windows line endings', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = normalizeLineEndings(
            readResourceDebuggerSource(extensionRoot)
                .replace(/\r?\n/g, '\r\n'));
        const mutation = resourceDebugger.replace(
            "() => runResourceDebuggerCleanupPhase(\n                'resource debugger teardown stop control',\n                resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,",
            "() => runResourceDebuggerPhase(\n                'resource debugger teardown stop control',\n                resourceDebuggerDeadline,\n                resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,");

        assert.notStrictEqual(mutation, resourceDebugger, 'Expected to apply the shared teardown deadline mutation.');
    });

    test('rejects a resource debugger setup wait omitted from the shared deadline', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const mutation = resourceDebugger.replace(
            "await runResourceDebuggerPhase('repository idle setup', deadline, resourceDebuggerPhaseTimeoutMs.repositoryIdle, timeoutMs => waitForRepositoryIdle(timeoutMs));",
            'await waitForRepositoryIdle();');

        assert.notStrictEqual(mutation, resourceDebugger, 'Expected to apply the omitted setup deadline mutation.');
        assert.throws(() => assertResourceDebuggerUsesBoundedDeadlines(mutation), /waitForRepositoryIdle to execute through runResourceDebuggerPhase/);
    });

    test('rejects a resource debugger control wait omitted from the shared deadline', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const mutation = resourceDebugger.replace(
            "{ waitFor: 'started', timeoutMs }",
            "{ waitFor: 'started' }");

        assert.notStrictEqual(mutation, resourceDebugger, 'Expected to apply the omitted control deadline mutation.');
        assert.throws(() => assertResourceDebuggerUsesBoundedDeadlines(mutation), /executeE2eControlCommand to use the remaining timeout/);
    });

    test('rejects resource debugger teardown that reuses the proof deadline', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const mutation = resourceDebugger.replace(
            "() => runResourceDebuggerCleanupPhase(\n                'resource debugger teardown stop control',\n                resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,",
            "() => runResourceDebuggerPhase(\n                'resource debugger teardown stop control',\n                resourceDebuggerDeadline,\n                resourceDebuggerPhaseTimeoutMs.stopDebuggingControl,");

        assert.notStrictEqual(mutation, resourceDebugger, 'Expected to apply the shared teardown deadline mutation.');
        assert.throws(() => assertResourceDebuggerUsesBoundedDeadlines(mutation), /Teardown cleanup must not reuse the proof deadline/);
    });

    test('rejects resource debugger teardown that waits on the AppHost state mirror', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const mutation = resourceDebugger.replace(
            '                timeoutMs => waitForNoDebugSessions(timeoutMs)),',
            "                timeoutMs => waitForNoDebugSessions(timeoutMs)),\n            () => runResourceDebuggerCleanupPhase(\n                'renamed lagging state wait',\n                resourceDebuggerPhaseTimeoutMs.appHostExit,\n                timeoutMs => waitForNoRunningAppHost(timeoutMs)),");

        assert.notStrictEqual(mutation, resourceDebugger, 'Expected to add the lagging AppHost state wait.');
        assert.throws(() => assertResourceDebuggerUsesBoundedDeadlines(mutation), /Teardown must not wait on the extension state AppHost mirror/);
    });

    test('requires the captured AppHost process to be running before stopping debugging', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const resourceDebugger = readResourceDebuggerSource(extensionRoot);
        const nodeProofTest = getTestBlock(resourceDebugger, 'stopping debugging tears down the Node resource process tree');
        const capturedPidAssertion = "assert.ok(appHostPid !== undefined, 'Expected the extension state to report an AppHost pid while the AppHost is running.');";
        const livePidAssertion = 'assert.ok(isProcessRunning(appHostPid)';
        const stopDebugging = "timeoutMs => executeE2eControlCommand({ name: 'stopDebugging' }, { waitFor: 'started', timeoutMs }))";

        assert.ok(nodeProofTest.includes(capturedPidAssertion));
        assert.ok(nodeProofTest.includes(livePidAssertion));
        assert.ok(nodeProofTest.indexOf(livePidAssertion) > nodeProofTest.indexOf(capturedPidAssertion));
        assert.ok(nodeProofTest.indexOf(stopDebugging) > nodeProofTest.indexOf(livePidAssertion));
    });

    test('includes the Node fixture when the full E2E spec glob matches resource debugger tests', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
        const paths = fs.readFileSync(path.join(extensionRoot, 'src', 'test-e2e', 'helpers', 'paths.ts'), 'utf8');

        const matchedSpecsIndex = runner.indexOf('const matchedTestSpecs =');
        const includeFixtureIndex = runner.indexOf("const includeNodeResourceFixture = matchedTestSpecs.some(file => path.basename(file) === 'resourceDebugger.e2e.test.js');");

        assert.ok(matchedSpecsIndex >= 0, 'The runner must resolve the effective spec glob before fixture selection.');
        assert.ok(includeFixtureIndex > matchedSpecsIndex, 'Fixture selection must inspect the actual matched spec files, not just the shard name.');
        assert.ok(paths.includes('Any E2E run that includes the resource debugger spec gets this fixture'));
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

    test('patches ExTester launch arguments without version-specific assumptions or replacement-token expansion', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        assert.ok(stopAppHost.includes('await waitForProcessExit(runningAppHost.appHostPid, `AppHost ${appHostPath}`, 30000);'));
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
        assert.ok(dynamicDebugConfiguration.includes('let appHostPidBeforeStop: number | undefined;'));
        assert.ok(dynamicDebugConfiguration.includes('() => appHostPidBeforeStop ??= getRunningAppHostPid(appHostPath)'));
        assert.ok(dynamicDebugConfiguration.includes('() => appHostPidBeforeStop ??= getRunningAppHostPid(firstAppHostPath)'));
        assert.ok(dynamicDebugConfiguration.includes('() => stopAppHostIfRunning(appHostPath)'));
        assert.ok(dynamicDebugConfiguration.includes('() => stopAppHostIfRunning(firstAppHostPath)'));
        assert.ok(dynamicDebugConfiguration.includes("waitForKnownProcessExit(appHostPidBeforeStop, 'the dynamic debug configuration AppHost process', 30000)"));
        assert.ok(dynamicDebugConfiguration.indexOf("waitForKnownProcessExit(appHostPidBeforeStop, 'the dynamic debug configuration AppHost process', 30000)") < dynamicDebugConfiguration.indexOf('removePath(fixtureRoot, { recursive: true, force: true })'));
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
        assert.ok(fixtures.includes('export async function waitForProcessExit(pid: number, description: string, timeoutMs: number): Promise<void>'));
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
    test('reuses immutable VS Code downloads while keeping ExTester state per run', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);

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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
        const runner = readRunnerSource(extensionRoot);
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
