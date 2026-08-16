import * as assert from 'assert';

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

    const isolatedIndex = argumentsList.indexOf('--isolated', runIndex + 1);
    assert.ok(isolatedIndex > runIndex, `Expected exact '--isolated' after 'run' in: ${formattedArguments}`);
    assert.strictEqual(
        argumentsList.some(argument => argument === '--isolated=false') || argumentsList[isolatedIndex + 1]?.toLowerCase() === 'false',
        false,
        `Expected inferred isolation to use only the true-form --isolated switch: ${formattedArguments}`);

    const startDebugSessionIndex = argumentsList.indexOf('--start-debug-session', isolatedIndex + 1);
    assert.ok(startDebugSessionIndex > isolatedIndex, `Expected exact '--start-debug-session' after '--isolated' in: ${formattedArguments}`);

    const appHostIndex = argumentsList.indexOf('--apphost', startDebugSessionIndex + 1);
    assert.ok(appHostIndex > startDebugSessionIndex, `Expected exact '--apphost' after '--start-debug-session' in: ${formattedArguments}`);
    assert.ok(
        appHostIndex + 1 < argumentsList.length &&
        commandLineArgumentEquals(argumentsList[appHostIndex + 1], appHostPath, platform),
        `Expected exact --apphost path '${appHostPath}' immediately after '--apphost' in: ${formattedArguments}`);
}
