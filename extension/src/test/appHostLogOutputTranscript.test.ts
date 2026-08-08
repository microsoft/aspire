import * as assert from 'assert';
import { AppHostLogEntry, AppHostLogOutputCoordinator, AppHostParentOutput } from '../debugger/appHostLogOutput';

// Captured from a real `dotnet run` of an Aspire AppHost whose BackgroundService emits one
// record per log level plus a repeated, a multi-line, and an exception-bearing record. These
// are the bytes the coreclr debug adapter reads from the AppHost's piped stdout, which is how
// the extension launches it (`console` is `internalConsole`). Absolute paths were replaced
// with placeholders; every record shape is untouched, including the framework records that
// surround the emitter's own output.
const realAppHostStdout = [
    'info: Aspire.Hosting.DistributedApplication[0]\n',
    '      Aspire AppHost version: 13.5.0-preview.1.26405.3\n',
    'info: Aspire.Hosting.DistributedApplication[0]\n',
    '      Distributed application starting.\n',
    'fail: Aspire.Hosting.Dashboard[0]\n',
    '      2026-08-07T11:13:18.2140000Z [sys] An attempt to start the Executable failed: Error = chdir <artifacts>/Aspire.Dashboard/Debug/net8.0: no such file or directory\n',
    'crit: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_CRITICAL\n',
    'fail: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_ERROR\n',
    '      System.InvalidOperationException: ISSUE18047_REALRUN_EXCEPTION\n',
    'warn: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_WARNING\n',
    'info: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_INFORMATION\n',
    'dbug: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_DEBUG\n',
    'trce: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_TRACE\n',
    'info: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_REPEATED\n',
    'info: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_REPEATED\n',
    'warn: Issue18047.ProofLogEmitter[0]\n',
    '      ISSUE18047_REALRUN_MULTILINE\n',
    '      details line\n',
    'warn: Aspire.Hosting.Backchannel.AuxiliaryBackchannelRpcTarget[0]\n',
    '      An error occurred while waiting for the Aspire Dashboard to become healthy.\n',
    "      Aspire.Hosting.DistributedApplicationException: Stopped waiting for resource 'aspire-dashboard' to become healthy because it failed to start.\n",
    '         at Aspire.Hosting.ApplicationModel.ResourceNotificationService.WaitForResourceHealthyAsync(String resourceName, CancellationToken cancellationToken) in /_/src/Aspire.Hosting/ApplicationModel/ResourceNotificationService.cs:line 250\n'
].join('');

// The same run also wrote one line straight to stderr, bypassing ILogger entirely.
const realAppHostStderr = 'ISSUE18047_REALRUN_NATIVE_EXCEPTION: native details\n';

// BackchannelLoggerProvider is registered on the same ILoggerFactory as the console
// provider, so it observes the identical ILogger calls. These are those calls, written out
// rather than derived from the text above so the test does not assume the parser is correct.
const realBackchannelEntries: readonly Partial<AppHostLogEntry>[] = [
    { logLevel: 'Information', categoryName: 'Aspire.Hosting.DistributedApplication', message: 'Aspire AppHost version: 13.5.0-preview.1.26405.3' },
    { logLevel: 'Information', categoryName: 'Aspire.Hosting.DistributedApplication', message: 'Distributed application starting.' },
    { logLevel: 'Error', categoryName: 'Aspire.Hosting.Dashboard', message: '2026-08-07T11:13:18.2140000Z [sys] An attempt to start the Executable failed: Error = chdir <artifacts>/Aspire.Dashboard/Debug/net8.0: no such file or directory' },
    { logLevel: 'Critical', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_CRITICAL' },
    { logLevel: 'Error', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_ERROR', exception: 'System.InvalidOperationException: ISSUE18047_REALRUN_EXCEPTION' },
    { logLevel: 'Warning', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_WARNING' },
    { logLevel: 'Information', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_INFORMATION' },
    { logLevel: 'Debug', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_DEBUG' },
    { logLevel: 'Trace', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_TRACE' },
    { logLevel: 'Information', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_REPEATED' },
    { logLevel: 'Information', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_REPEATED' },
    { logLevel: 'Warning', categoryName: 'Issue18047.ProofLogEmitter', message: 'ISSUE18047_REALRUN_MULTILINE\ndetails line' },
    {
        logLevel: 'Warning',
        categoryName: 'Aspire.Hosting.Backchannel.AuxiliaryBackchannelRpcTarget',
        message: 'An error occurred while waiting for the Aspire Dashboard to become healthy.',
        exception: "Aspire.Hosting.DistributedApplicationException: Stopped waiting for resource 'aspire-dashboard' to become healthy because it failed to start.\n   at Aspire.Hosting.ApplicationModel.ResourceNotificationService.WaitForResourceHealthyAsync(String resourceName, CancellationToken cancellationToken) in /_/src/Aspire.Hosting/ApplicationModel/ResourceNotificationService.cs:line 250"
    }
];

suite('AppHost log output real transcript tests', () => {
    test('renders each logged record exactly once with severity styling', () => {
        const rendered = replayRealTranscript(256);
        const text = rendered.map(output => output.output).join('');

        // Every emitter record was logged once except REPEATED, which the AppHost really
        // did log twice. Before the fix each of these appeared twice: once from the child
        // console stream and once from the CLI's dim backchannel relay.
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_CRITICAL'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_ERROR'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_EXCEPTION'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_WARNING'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_INFORMATION'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_DEBUG'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_TRACE'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_MULTILINE'), 1);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_REPEATED'), 2);
        assert.strictEqual(countOccurrences(text, 'ISSUE18047_REALRUN_NATIVE_EXCEPTION'), 1);

        // Framework records travel the same two paths and must not double either.
        assert.strictEqual(countOccurrences(text, 'Distributed application starting.'), 1);
        assert.strictEqual(countOccurrences(text, 'An attempt to start the Executable failed'), 1);
        assert.strictEqual(countOccurrences(text, 'Stopped waiting for resource'), 1);

    });

    test('styles severities and routes error records to stderr', () => {
        const rendered = replayRealTranscript(256);

        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_WARNING'), {
            output: '\x1b[33mIssue18047.ProofLogEmitter: Warning: ISSUE18047_REALRUN_WARNING\x1b[0m\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_DEBUG'), {
            output: '\x1b[2mIssue18047.ProofLogEmitter: Debug: ISSUE18047_REALRUN_DEBUG\x1b[0m\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_TRACE'), {
            output: '\x1b[2mIssue18047.ProofLogEmitter: Trace: ISSUE18047_REALRUN_TRACE\x1b[0m\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_INFORMATION'), {
            output: 'Issue18047.ProofLogEmitter: Information: ISSUE18047_REALRUN_INFORMATION\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_CRITICAL'), {
            output: 'Issue18047.ProofLogEmitter: Critical: ISSUE18047_REALRUN_CRITICAL\n',
            category: 'stderr'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_EXCEPTION'), {
            output: 'Issue18047.ProofLogEmitter: Error: ISSUE18047_REALRUN_ERROR\nSystem.InvalidOperationException: ISSUE18047_REALRUN_EXCEPTION\n',
            category: 'stderr'
        });
        assert.deepStrictEqual(findRendered(rendered, 'ISSUE18047_REALRUN_MULTILINE'), {
            output: '\x1b[33mIssue18047.ProofLogEmitter: Warning: ISSUE18047_REALRUN_MULTILINE\ndetails line\x1b[0m\n',
            category: 'stdout'
        });
    });

    test('produces the same rendering at every stream chunk size', () => {
        // A redirected Console.Out flushes on a fixed buffer size, so the adapter sees a
        // record split at an arbitrary offset. The rendering must not depend on where.
        const baseline = replayRealTranscript(4096).map(output => output.output).join('');

        for (const chunkSize of [1, 7, 64, 137, 256, 1024]) {
            const actual = replayRealTranscript(chunkSize).map(output => output.output).join('');
            assert.strictEqual(actual, baseline, `chunk size ${chunkSize} changed the rendering`);
        }
    });
});

function replayRealTranscript(chunkSize: number): AppHostParentOutput[] {
    const coordinator = new AppHostLogOutputCoordinator();
    const rendered: AppHostParentOutput[] = [];

    for (const chunk of splitIntoChunks(realAppHostStdout, chunkSize)) {
        rendered.push(...coordinator.handleDebugAdapterOutput(chunk, 'stdout'));
    }

    for (const chunk of splitIntoChunks(realAppHostStderr, chunkSize)) {
        rendered.push(...coordinator.handleDebugAdapterOutput(chunk, 'stderr'));
    }

    realBackchannelEntries.forEach((entry, index) => {
        const output = coordinator.handleBackchannelEntry({
            sequenceNumber: index + 1,
            timestamp: '2026-08-07T11:13:18.2140000+00:00',
            eventId: 0,
            eventName: null,
            exception: null,
            ...entry
        } as AppHostLogEntry);

        if (output) {
            rendered.push(output);
        }
    });

    rendered.push(...coordinator.flush());

    return rendered;
}

function splitIntoChunks(value: string, chunkSize: number): string[] {
    const chunks: string[] = [];
    for (let index = 0; index < value.length; index += chunkSize) {
        chunks.push(value.slice(index, index + chunkSize));
    }

    return chunks;
}

function findRendered(rendered: AppHostParentOutput[], marker: string): AppHostParentOutput | undefined {
    return rendered.find(output => output.output.includes(marker));
}

function countOccurrences(value: string, marker: string): number {
    return value.split(marker).length - 1;
}
