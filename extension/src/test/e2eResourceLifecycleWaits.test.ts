import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';

const lifecycleCommands = new Set(['startResource', 'stopResource', 'restartResource']);

interface LifecycleDispatch {
    file: string;
    line: number;
    command: string;
    waitFor: string | undefined;
    hasTimeoutBudget: boolean;
    followingSource: string;
}

function getStringProperty(object: ts.ObjectLiteralExpression, name: string): string | undefined {
    for (const property of object.properties) {
        if (ts.isPropertyAssignment(property) && property.name.getText() === name && ts.isStringLiteralLike(property.initializer)) {
            return property.initializer.text;
        }
    }

    return undefined;
}

function hasProperty(object: ts.ObjectLiteralExpression, name: string): boolean {
    return object.properties.some(property => property.name?.getText() === name);
}

function findLifecycleDispatches(file: string, source: string): LifecycleDispatch[] {
    const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const dispatches: LifecycleDispatch[] = [];

    const visit = (node: ts.Node): void => {
        if (ts.isCallExpression(node)
            && node.expression.getText(sourceFile) === 'executeE2eControlCommand'
            && node.arguments.length > 0
            && ts.isObjectLiteralExpression(node.arguments[0])) {
            const command = getStringProperty(node.arguments[0], 'name');
            if (command !== undefined && lifecycleCommands.has(command)) {
                const options = node.arguments[1];
                const optionsObject = options !== undefined && ts.isObjectLiteralExpression(options) ? options : undefined;
                dispatches.push({
                    file,
                    line: sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1,
                    command,
                    waitFor: optionsObject ? getStringProperty(optionsObject, 'waitFor') : undefined,
                    hasTimeoutBudget: optionsObject ? hasProperty(optionsObject, 'timeoutMs') : false,
                    followingSource: source.slice(node.getEnd(), node.getEnd() + 400),
                });
            }
        }

        ts.forEachChild(node, visit);
    };

    visit(sourceFile);
    return dispatches;
}

function findAllLifecycleDispatches(): LifecycleDispatch[] {
    const e2eRoot = path.resolve(__dirname, '..', '..', 'src', 'test-e2e');
    return fs.readdirSync(e2eRoot)
        .filter(file => file.endsWith('.e2e.test.ts'))
        .flatMap(file => findLifecycleDispatches(file, fs.readFileSync(path.join(e2eRoot, file), 'utf8')));
}

function getViolations(dispatches: LifecycleDispatch[]): string[] {
    const violations: string[] = [];
    for (const dispatch of dispatches) {
        const location = `${dispatch.file}:${dispatch.line} (${dispatch.command})`;
        if (dispatch.waitFor === 'started') {
            const outcomeWait = `await waitForCommandOutcome('aspire-vscode.${dispatch.command}', 'success',`;
            if (!dispatch.followingSource.replace(/^\s*;?\s*/, '').startsWith(outcomeWait)) {
                violations.push(`${location} waits for 'started' but is not immediately followed by a successful command outcome assertion.`);
            }
        } else if (!dispatch.hasTimeoutBudget) {
            violations.push(`${location} uses the default 10s control apply wait; dispatch with { waitFor: 'started' } and assert the command outcome.`);
        }
    }

    return violations;
}

suite('E2E resource lifecycle control waits', () => {
    // Stopping a `dotnet run` resource that is still building can take longer than the default 10s
    // E2E control apply wait on Windows CI, so lifecycle commands must rely on an outcome wait
    // (or an explicit budget) instead of the incidental control timeout.
    test('resource lifecycle commands do not use the default control apply timeout', () => {
        const dispatches = findAllLifecycleDispatches();
        const files = new Set(dispatches.map(dispatch => dispatch.file));
        for (const file of ['resourceDebugTools.e2e.test.ts', 'treeActions.e2e.test.ts', 'azureFunctions.e2e.test.ts']) {
            assert.ok(files.has(file), `Expected to find resource lifecycle dispatches in ${file}.`);
        }

        assert.deepStrictEqual(getViolations(dispatches), []);
    });

    test('flags lifecycle dispatches that rely on the default apply wait or skip the outcome assertion', () => {
        const source = [
            "suite('sample', () => {",
            "  test('a', async () => {",
            "    await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName });",
            "    await executeE2eControlCommand({ name: 'restartResource', appHostPath, resourceName }, { waitFor: 'started' });",
            "    await waitForResourceState(resourceName, ['Running'], 90000);",
            "    await executeE2eControlCommand({ name: 'startResource', appHostPath, resourceName }, { waitFor: 'started' });",
            "    await waitForCommandOutcome('aspire-vscode.startResource', 'success', 60000, before);",
            "    await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName }, { timeoutMs });",
            "    await executeE2eControlCommand({ name: 'runAppHost', appHostPath });",
            '  });',
            '});',
        ].join('\n');

        const dispatches = findLifecycleDispatches('sample.e2e.test.ts', source);
        assert.strictEqual(dispatches.length, 4);
        assert.deepStrictEqual(getViolations(dispatches), [
            "sample.e2e.test.ts:3 (stopResource) uses the default 10s control apply wait; dispatch with { waitFor: 'started' } and assert the command outcome.",
            "sample.e2e.test.ts:4 (restartResource) waits for 'started' but is not immediately followed by a successful command outcome assertion.",
        ]);
    });
});
