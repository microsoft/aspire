import * as assert from 'assert';
import { getCmdShimSpawnCommand, shouldWrapWithCmd } from '../../utils/cmdShimCommand';

export function commandLineArgumentEquals(actual: string, expected: string, platform = process.platform): boolean {
    return platform === 'win32'
        ? actual.toLowerCase() === expected.toLowerCase()
        : actual === expected;
}

export function assertLinkedAppHostCliLaunch(
    argumentsList: readonly string[],
    appHostPath: string,
    cliPath: string,
    platform = process.platform
): void {
    const formattedArguments = JSON.stringify(argumentsList);
    assert.ok(
        argumentsList.length > 0 && commandLineArgumentEquals(argumentsList[0], cliPath, platform),
        `Expected the current E2E CLI '${cliPath}' as argv[0] in: ${formattedArguments}`);

    const runIndex = argumentsList.indexOf('run', 1);
    assert.ok(runIndex > 0, `Expected exact 'run' after the CLI path in: ${formattedArguments}`);
    const separatorIndex = argumentsList.indexOf('--', runIndex + 1);
    const rootArgumentsEnd = separatorIndex >= 0 ? separatorIndex : argumentsList.length;

    const isolatedIndex = argumentsList.indexOf('--isolated', runIndex + 1);
    assert.ok(isolatedIndex > runIndex && isolatedIndex < rootArgumentsEnd, `Expected exact '--isolated' root option after 'run' in: ${formattedArguments}`);
    assert.strictEqual(
        argumentsList.slice(runIndex + 1, rootArgumentsEnd).some(argument => argument === '--isolated=false') ||
        argumentsList[isolatedIndex + 1]?.toLowerCase() === 'false',
        false,
        `Expected inferred isolation to use only the true-form --isolated switch: ${formattedArguments}`);

    const startDebugSessionIndex = argumentsList.indexOf('--start-debug-session', isolatedIndex + 1);
    assert.ok(startDebugSessionIndex > isolatedIndex && startDebugSessionIndex < rootArgumentsEnd, `Expected exact '--start-debug-session' root option after '--isolated' in: ${formattedArguments}`);

    const appHostIndex = argumentsList.indexOf('--apphost', startDebugSessionIndex + 1);
    assert.ok(appHostIndex > startDebugSessionIndex && appHostIndex < rootArgumentsEnd, `Expected exact '--apphost' root option after '--start-debug-session' in: ${formattedArguments}`);
    assert.ok(
        appHostIndex + 1 < argumentsList.length &&
        commandLineArgumentEquals(argumentsList[appHostIndex + 1], appHostPath, platform),
        `Expected exact --apphost path '${appHostPath}' immediately after '--apphost' in: ${formattedArguments}`);
}

export function assertExactLinkedAppHostCliLaunch(
    argumentsList: readonly string[],
    appHostPath: string,
    cliPath: string,
    appHostArguments: readonly string[],
    platform = process.platform
): void {
    const expectedArguments = getExpectedLinkedAppHostCliProcessArguments(cliPath, appHostPath, appHostArguments);
    const pathArgumentIndexes = shouldWrapWithCmd(cliPath)
        ? [0]
        : [0, 6];
    const argumentsMatch = argumentsList.length === expectedArguments.length &&
        argumentsList.every((argument, index) =>
            pathArgumentIndexes.includes(index)
                ? commandLineArgumentEquals(argument, expectedArguments[index], platform)
                : argument === expectedArguments[index]);

    assert.ok(
        argumentsMatch,
        `Expected exact Aspire CLI argv ${JSON.stringify(expectedArguments)}, got ${JSON.stringify(argumentsList)}.`);
}

export function getExpectedLinkedAppHostCliProcessArguments(
    cliPath: string,
    appHostPath: string,
    appHostArguments: readonly string[]
): string[] {
    const cliArguments = [
        'run',
        '--isolated',
        '--start-debug-session',
        '--nologo',
        '--apphost',
        appHostPath,
        '--',
        ...appHostArguments,
    ];

    if (shouldWrapWithCmd(cliPath)) {
        const spawnCommand = getCmdShimSpawnCommand(cliPath, cliArguments);
        return [spawnCommand.command, ...spawnCommand.args];
    }

    return [cliPath, ...cliArguments];
}
