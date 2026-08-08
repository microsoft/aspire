import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { countDebugConsoleOccurrences, getCommandInvocationCount, waitForCommandOutcome, waitForDebugConsoleOutput, waitForDebugSessionStartup, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForSettledDebugConsoleOutput, waitForWorkspaceAppHost, type DebugConsoleOutput } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning, writeFileWithRetry } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

const probeCategory = 'AspireE2E.LogProbe';
const infoMarker = 'E2ELOGPROBEINFO';
const repeatedMarker = 'E2ELOGPROBEREPEAT';
const warningMarker = 'E2ELOGPROBEWARN';
const warningContinuationMarker = 'E2ELOGPROBEWARNSECOND';
const debugMarker = 'E2ELOGPROBEDEBUG';
const errorMarker = 'E2ELOGPROBEERROR';
const exceptionMarker = 'E2ELOGPROBEEXCEPTION';

const yellowAnsi = '\u001b[33m';
const dimAnsi = '\u001b[2m';

/**
 * The AppHost writes every log record twice: once to its own stdout, which reaches the
 * extension through the `coreclr` debug adapter, and once over the CLI backchannel. Only a
 * real AppHost reproduces the timing and stream chunking between those two transports, so
 * the deduplication and styling contract is asserted end to end here rather than against
 * mocked RPC and tracker calls.
 */
suite('Aspire AppHost debug console logs E2E', function () {
    this.timeout(360000);

    let appHostSourcePath: string | undefined;
    let originalAppHostSource: string | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => {
                if (appHostSourcePath && originalAppHostSource !== undefined) {
                    writeFileWithRetry(appHostSourcePath, originalAppHostSource);
                }
            },
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'AppHost debug console logs E2E teardown failed.');
    });

    test('renders each AppHost log record once with the expected category and styling', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        appHostSourcePath = path.join(path.dirname(appHostPath), 'AppHost.cs');
        originalAppHostSource = fs.readFileSync(appHostSourcePath, 'utf8');

        const instrumented = instrumentAppHostSource(originalAppHostSource);
        writeFileWithRetry(appHostSourcePath, instrumented);

        const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);
        await waitForDebugSessionStartup();

        // The error record is emitted last, so seeing it means every probe record has been
        // written by the AppHost. The settle wait then covers the backchannel copy, which
        // can trail the debug adapter copy by an arbitrary amount.
        await waitForDebugConsoleOutput(errorMarker, appHostPath, 240000);
        const outputs = await waitForSettledDebugConsoleOutput(appHostPath);
        const transcript = () => JSON.stringify(outputs.filter(event => event.output.includes('E2ELOGPROBE')), undefined, 2);

        assert.strictEqual(countDebugConsoleOccurrences(outputs, infoMarker), 1, `Expected one information record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, repeatedMarker), 2, `Expected both copies of the deliberately repeated record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, warningMarker), 1, `Expected one warning record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, warningContinuationMarker), 1, `Expected the warning continuation line to stay with its record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, debugMarker), 1, `Expected one debug record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, errorMarker), 1, `Expected one error record.\n${transcript()}`);
        assert.strictEqual(countDebugConsoleOccurrences(outputs, exceptionMarker), 1, `Expected the exception to stay with its error record.\n${transcript()}`);

        const warning = findSingleOutput(outputs, warningMarker, transcript);
        assert.ok(warning.output.includes(yellowAnsi), `Expected the warning record to be yellow: ${JSON.stringify(warning.output)}`);
        assert.ok(warning.output.includes(warningContinuationMarker), `Expected the warning continuation line in the same output event: ${JSON.stringify(warning.output)}`);
        assert.strictEqual(warning.category, 'stdout', `Expected the warning record on stdout: ${JSON.stringify(warning)}`);

        const debugRecord = findSingleOutput(outputs, debugMarker, transcript);
        assert.ok(debugRecord.output.includes(dimAnsi), `Expected the debug record to be dimmed: ${JSON.stringify(debugRecord.output)}`);
        assert.strictEqual(debugRecord.category, 'stdout', `Expected the debug record on stdout: ${JSON.stringify(debugRecord)}`);

        const error = findSingleOutput(outputs, errorMarker, transcript);
        assert.strictEqual(error.category, 'stderr', `Expected the error record on stderr: ${JSON.stringify(error)}`);
        assert.ok(error.output.includes(exceptionMarker), `Expected the exception in the same output event as its message: ${JSON.stringify(error.output)}`);

        const information = findSingleOutput(outputs, infoMarker, transcript);
        assert.strictEqual(information.category, 'stdout', `Expected the information record on stdout: ${JSON.stringify(information)}`);
        assert.ok(!information.output.includes(yellowAnsi) && !information.output.includes(dimAnsi), `Expected the information record to be unstyled: ${JSON.stringify(information.output)}`);
        assert.ok(information.output.includes(probeCategory), `Expected the record to keep its logger category: ${JSON.stringify(information.output)}`);

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();
    });
});

function findSingleOutput(outputs: readonly DebugConsoleOutput[], marker: string, transcript: () => string): DebugConsoleOutput {
    const matches = outputs.filter(event => event.output.includes(marker));
    assert.strictEqual(matches.length, 1, `Expected exactly one debug console event containing '${marker}'.\n${transcript()}`);

    return matches[0];
}

function instrumentAppHostSource(source: string): string {
    // `Microsoft.Extensions.Logging` and `Microsoft.Extensions.DependencyInjection` are not
    // among the AppHost SDK's implicit usings, and using directives have to precede the
    // first top-level statement, so they are inserted immediately above it.
    const withUsings = source.replace(
        'var builder = DistributedApplication.CreateBuilder(args);',
        `using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);`);
    assert.notStrictEqual(withUsings, source, 'Expected the AppHost fixture to create a DistributedApplication builder.');

    // The probe runs from AfterResourcesCreatedEvent so its records land at the tail of the
    // console transcript, after the noisy startup output the state bridge also records.
    // The exception is constructed rather than thrown so its ToString() has no stack trace
    // and the assertion does not depend on runtime frames.
    const instrumented = withUsings.replace(
        'builder.Build().Run();',
        `builder.Services.AddLogging(logging => logging.AddFilter("${probeCategory}", LogLevel.Trace));
builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((probeEvent, probeCancellationToken) =>
{
    var probeLogger = probeEvent.Services.GetRequiredService<ILoggerFactory>().CreateLogger("${probeCategory}");
    probeLogger.LogInformation("${infoMarker} single information record.");
    probeLogger.LogInformation("${repeatedMarker} identical record.");
    probeLogger.LogInformation("${repeatedMarker} identical record.");
    probeLogger.LogWarning("${warningMarker} first line.\\n${warningContinuationMarker} second line.");
    probeLogger.LogDebug("${debugMarker} dim record.");
    probeLogger.LogError(new InvalidOperationException("${exceptionMarker}"), "${errorMarker} failing record.");
    return Task.CompletedTask;
});

builder.Build().Run();`);
    assert.notStrictEqual(instrumented, withUsings, 'Expected the AppHost fixture to build and run the distributed application.');

    return instrumented;
}
