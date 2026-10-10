import * as assert from 'assert';
import * as sinon from 'sinon';
import {
    AppHostLogEntry,
    AppHostLogOutputCoordinator,
    AppHostParentOutput,
    AppHostParentOutputFilter
} from '../debugger/appHostLogOutput';

suite('AppHost log output coordinator', () => {
    test('deduplicates correlated records without dropping repeated messages', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ sequenceNumber: 1 })), {
            output: 'Example.Category: Information: Repeated message.\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ sequenceNumber: 2 })), {
            output: 'Example.Category: Information: Repeated message.\n',
            category: 'stdout'
        });

        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7]\n      Repeated message.\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7]\n      Repeated message.\n', 'stdout'),
            []);
    });

    test('deduplicates the backchannel, ConsoleLogger, and DebugLogger copies', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ logLevel: 'Warning', message: 'Port is busy.' })),
            {
                output: '\x1b[33mExample.Category: Warning: Port is busy.\x1b[0m\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(coordinator, 'warn: Example.Category[7]\n      Port is busy.\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(coordinator, 'Example.Category: Warning: Port is busy.\n', 'console'),
            []);
    });

    test('renders parsed records consistently regardless of which source arrives first', () => {
        const entry = createEntry({ logLevel: 'Warning', message: 'Port is busy.' });
        const expected = {
            output: '\x1b[33mExample.Category: Warning: Port is busy.\x1b[0m\n',
            category: 'stdout'
        };

        const backchannelFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(entry), expected);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(
            renderConsole(consoleFirst, '2026-08-10 17:40:09 warn: Example.Category[7]\n      Port is busy.\n', 'stdout'),
            [expected]);

        const debugLoggerFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(
            renderConsole(debugLoggerFirst, 'Example.Category: Warning: Port is busy.\n', 'console'),
            [expected]);
        assert.strictEqual(debugLoggerFirst.handleBackchannelEntry(entry), undefined);
    });

    test('preserves structured trailing whitespace while correlating normalized adapter output', () => {
        const entry = createEntry({ message: 'value \t\n\n' });
        const raw = 'info: Example.Category[7]\n      value \t\n      \n      \n';
        const expected = {
            output: 'Example.Category: Information: value \t\n\n\n',
            category: 'stdout'
        };

        const structuredFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(structuredFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(renderConsole(structuredFirst, raw, 'stdout'), []);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(consoleFirst.handleDebugAdapterOutput(raw, 'stdout'), []);
        assert.deepStrictEqual(consoleFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(consoleFirst.flush(), []);

        const continuation = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(continuation.handleBackchannelEntry(createEntry({
            categoryName: 'Worker[42] shard',
            message: 'first\nsecond'
        })), {
            output: 'Worker[42] shard: Information: first\nsecond\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(continuation, 'info: Worker[42] shard[7] first\nsecond\n', 'stdout'),
            []);
    });

    test('escapes category control characters consistently across log sources', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const category = 'Unsafe\x1b[31m\tCategory\u0085';
        const escapedCategory = 'Unsafe\\u001b[31m\\u0009Category\\u0085';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ categoryName: category })),
            {
                output: `${escapedCategory}: Information: Repeated message.\n`,
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(coordinator, `info: ${category}[7]\n      Repeated message.\n`, 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(coordinator, `${category}: Information: Repeated message.\n`, 'console'),
            []);

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: 2,
                categoryName: 'Forged\r\nline\x7f'
            })),
            {
                output: 'Forged\\u000d\\u000aline\\u007f: Information: Repeated message.\n',
                category: 'stdout'
            });
    });

    test('deduplicates a control-bearing category split across DAP callbacks', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const category = '\x1b[31mForged\r\nNext';

        assert.ok(coordinator.handleBackchannelEntry(createEntry({ categoryName: category })));
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('info: \x1b[31mForged\r\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('Next[7]\n      Repeated message.\n', 'stdout'),
            []);
        assert.deepStrictEqual(coordinator.flush(), []);

        const incomplete = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(
            incomplete.handleDebugAdapterOutput('info: ordinary output\n', 'stdout'),
            []);
        assert.deepStrictEqual(incomplete.flush(), [{
            output: 'info: ordinary output\n',
            category: 'stdout'
        }]);
    });

    test('does not merge an unmatched ConsoleLogger prefix with the next record', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: 'Real message.' })));

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'info: ordinary output\n'
                    + 'info: Example.Category[7] Real message.\n',
                'stdout'),
            [{
                output: 'info: ordinary output\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(coordinator.flush(), []);

        const lowLevel = new AppHostLogOutputCoordinator();
        assert.ok(lowLevel.handleBackchannelEntry(createEntry({ message: 'Real message.' })));
        assert.deepStrictEqual(
            lowLevel.handleDebugAdapterOutput(
                'dbug: ordinary output\n'
                    + 'info: Example.Category[7] Real message.\n',
                'stdout'),
            []);
        assert.deepStrictEqual(lowLevel.flush(), []);
        assert.deepStrictEqual(
            lowLevel.handleDebugAdapterOutput('      later stdout\n', 'stdout'),
            [{
                output: '      later stdout\n',
                category: 'stdout'
            }]);
    });

    test('idle flush releases incomplete ConsoleLogger header fragments', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput('info: ordinary output\n', 'stdout'),
                []);
            await clock.tickAsync(250);
            assert.deepStrictEqual(emitted, [{
                output: 'info: ordinary output\n',
                category: 'stdout'
            }]);
        } finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('bounds and resets incomplete ConsoleLogger header fragments', () => {
        const overflow = `${'x'.repeat(64 * 1024)}\n`;
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('info: ordinary output\n', 'stdout'),
            []);
        const outputs = coordinator.handleDebugAdapterOutput(overflow, 'stdout');
        assert.strictEqual(outputs.map(output => output.output).join(''), `info: ordinary output\n${overflow}`);
        assert.ok(outputs.every(output => output.category === 'stdout'));

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('info: discarded by reset\n', 'stdout'),
            []);
        coordinator.reset();
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('bounds ConsoleLogger records without losing raw output', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const header = 'fail: Example.Category[7]\n';
        const continuation = `      dbug: ${'x'.repeat(4084)}\n`;
        const raw = header + continuation.repeat(17);

        const outputs = coordinator.handleDebugAdapterOutput(raw, 'stdout');
        assert.strictEqual(outputs.map(output => output.output).join(''), raw);
        assert.ok(outputs.every(output => output.category === 'stderr'));
        assert.deepStrictEqual(coordinator.flush(), []);

        const oversizedLine = `dbug: Example.Category[7] ${'x'.repeat(64 * 1024)}\n`;
        const singleLine = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(
            singleLine.handleDebugAdapterOutput(oversizedLine, 'stdout'),
            [{
                output: oversizedLine,
                category: 'stdout'
            }]);
        assert.deepStrictEqual(singleLine.flush(), []);

        const windows = new AppHostLogOutputCoordinator();
        const windowsHeader = 'fail: Example.Category[7]\r\n';
        const windowsContinuation = `      ${'x'.repeat(4090)}\r\n`;
        const windowsOverflow = `      ${'x'.repeat(4090)}\n`;
        const windowsRaw = windowsHeader
            + windowsContinuation.repeat(15)
            + windowsOverflow
            + 'dbug: unindented tail\r\n';
        const windowsOutputs = windows.handleDebugAdapterOutput(windowsRaw, 'stdout');
        assert.strictEqual(windowsOutputs.map(output => output.output).join(''), windowsRaw);
        assert.ok(windowsOutputs.every(output => output.category === 'stderr'));
        assert.deepStrictEqual(windows.flush(), []);
    });

    test('keeps low-level adapter traffic from evicting pending Information records', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: 'Still pending.' })));

        const lowLevelOutput = Array.from(
            { length: 1025 },
            (_, index) => `Low.Level.Category: Debug: Detail ${index}.\n`).join('');
        assert.strictEqual(
            [...coordinator.handleDebugAdapterOutput(lowLevelOutput, 'console'), ...coordinator.flush()].length,
            1025);

        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7]\n      Still pending.\n', 'stdout'),
            []);
    });

    test('completed low-level pairs do not evict a pending record', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(coordinator, 'Pending.Category: Debug: Still pending.\n', 'console'),
            [{
                output: '\x1b[2mPending.Category: Debug: Still pending.\x1b[0m\n',
                category: 'stdout'
            }]);

        for (let index = 0; index < 129; index++) {
            assert.strictEqual(
                renderConsole(
                    coordinator,
                    `Noise.Category.${index}: Debug: Detail ${index}.\n`,
                    'console').length,
                1);
            assert.deepStrictEqual(
                renderConsole(
                    coordinator,
                    `dbug: Noise.Category.${index}[7]\n      Detail ${index}.\n`,
                    'stdout'),
                []);
        }

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'dbug: Pending.Category[7]\n      Still pending.\n',
                'stdout'),
            []);
    });

    test('colors warnings and preserves error stream identity', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ logLevel: 'Warning', message: 'Careful.' })),
            {
                output: '\x1b[33mExample.Category: Warning: Careful.\x1b[0m\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ sequenceNumber: 2, logLevel: 'Error', message: 'Failed.' })),
            {
                output: 'Example.Category: Error: Failed.\n',
                category: 'stderr'
            });
    });

    test('reassembles partial and interleaved stream chunks before correlation', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        assert.ok(coordinator.handleBackchannelEntry(createEntry({
            logLevel: 'Error',
            message: 'Request failed.',
            exception: 'System.InvalidOperationException: boom'
        })));

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('fail: Example.Cate', 'stdout'), []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('Unhandled exception. System.Exception: native\n', 'stderr'),
            [{ output: 'Unhandled exception. System.Exception: native\n', category: 'stderr' }]);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('gory[7]\r', 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('\n      Request fai', 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('led.\r\n      System.InvalidOperation', 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('Exception: boom\r\n', 'stdout'), []);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('retains an interleaved partial ConsoleLogger body', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: 'first partial' })));

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('info: Example.Category[7]\n      first par', 'stdout'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('native error\n', 'stderr'),
            [{
                output: 'native error\n',
                category: 'stderr'
            }]);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('tial\n', 'stdout'),
            []);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('replays a captured AppHost transcript with one output per log record', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const outputs: AppHostParentOutput[] = [];
        const append = (items: AppHostParentOutput[]) => outputs.push(...items);
        const appendEntry = (entry: AppHostLogEntry) => {
            const output = coordinator.handleBackchannelEntry(entry);
            if (output) {
                outputs.push(output);
            }
        };

        appendEntry(createEntry({ sequenceNumber: 1, message: 'Application started.' }));
        append(coordinator.handleDebugAdapterOutput('info: Example.Cate', 'stdout'));
        append(coordinator.handleDebugAdapterOutput('gory[7]\r\n      Application started.\r\n', 'stdout'));
        append(coordinator.handleDebugAdapterOutput('Example.Category: Information: Application started.\r\n', 'console'));

        append(coordinator.handleDebugAdapterOutput('Example.Category: Warning: Port is busy.\r\n', 'console'));
        appendEntry(createEntry({ sequenceNumber: 2, logLevel: 'Warning', message: 'Port is busy.' }));
        append(coordinator.handleDebugAdapterOutput('warn: Example.Category[7]\r\n      Port is busy.\r\n', 'stdout'));

        appendEntry(createEntry({
            sequenceNumber: 3,
            logLevel: 'Error',
            message: 'Request failed.',
            exception: 'System.InvalidOperationException: boom\n   at Example.Run()'
        }));
        append(coordinator.handleDebugAdapterOutput(
            'fail: Example.Category[7]\r\n'
            + '      Request failed.\r\n'
            + '      System.InvalidOperationException: boom\r\n'
            + '         at Example.Run()\r\n',
            'stdout'));
        append(coordinator.handleDebugAdapterOutput(
            'Example.Category: Error: Request failed.\r\n'
            + '\r\n'
            + 'System.InvalidOperationException: boom\r\n'
            + '   at Example.Run()\r\n',
            'console'));
        append(coordinator.flush());

        assert.deepStrictEqual(outputs, [
            {
                output: 'Example.Category: Information: Application started.\n',
                category: 'stdout'
            },
            {
                output: '\x1b[33mExample.Category: Warning: Port is busy.\x1b[0m\n',
                category: 'stdout'
            },
            {
                output: 'Example.Category: Error: Request failed.\n'
                    + 'System.InvalidOperationException: boom\n'
                    + '   at Example.Run()\n',
                category: 'stderr'
            }
        ]);
    });

    test('matches single-line SimpleConsoleFormatter output with an exception', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({
            logLevel: 'Error',
            message: 'Request failed.',
            exception: 'System.InvalidOperationException: boom\n   at Example.Run()'
        });
        const raw = 'fail: Example.Category[7] Request failed. System.InvalidOperationException: boom    at Example.Run()\n';

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Error: Request failed.\n'
                + 'System.InvalidOperationException: boom\n'
                + '   at Example.Run()\n',
            category: 'stderr'
        });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('matches ANSI-colored SimpleConsoleFormatter output', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ logLevel: 'Warning', message: 'Port is busy.' });
        const raw = '\x1b[40m\x1b[33mwarn\x1b[39m\x1b[49m: Example.Category[7] Port is busy.\n';

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: '\x1b[33mExample.Category: Warning: Port is busy.\x1b[0m\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), []);
    });

    test('matches ANSI-colored multiline SimpleConsoleFormatter output', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ logLevel: 'Warning', message: 'Port is busy.' });
        const raw = '\x1b[40m\x1b[33mwarn\x1b[39m\x1b[49m: Example.Category[7]\n'
            + '      Port is busy.\n';

        assert.ok(coordinator.handleBackchannelEntry(entry));
        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), []);
    });

    test('matches single-line SimpleConsoleFormatter output with scopes', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ logLevel: 'Warning', message: 'Scoped warning.' });
        const raw = 'warn: Example.Category[7] => RequestPath:/health Scoped warning.\n';

        assert.ok(coordinator.handleBackchannelEntry(entry));
        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), []);

        const emptyMessageCoordinator = new AppHostLogOutputCoordinator();
        assert.ok(emptyMessageCoordinator.handleBackchannelEntry(createEntry({ logLevel: 'Warning', message: '' })));
        assert.deepStrictEqual(
            renderConsole(
                emptyMessageCoordinator,
                'warn: Example.Category[7] => RequestPath:/health\n',
                'stdout'),
            []);
    });

    test('keeps an exact single-line message that begins with the scope marker', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ message: '=> started' });

        assert.ok(coordinator.handleBackchannelEntry(entry));
        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7] => started\n', 'stdout'),
            []);
    });

    test('does not correlate a scope-like message with its plain suffix', () => {
        const plainEntry = createEntry({ message: 'started' });
        const scopeLikeRaw = 'info: Example.Category[7] => started\n';
        const scopeLikeOutput = {
            output: 'Example.Category: Information: => started\n',
            category: 'stdout'
        };
        const plainOutput = {
            output: 'Example.Category: Information: started\n',
            category: 'stdout'
        };

        const structuredFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(structuredFirst.handleBackchannelEntry(plainEntry), plainOutput);
        assert.deepStrictEqual(renderConsole(structuredFirst, scopeLikeRaw, 'stdout'), [scopeLikeOutput]);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(renderConsole(consoleFirst, scopeLikeRaw, 'stdout'), [scopeLikeOutput]);
        assert.deepStrictEqual(consoleFirst.handleBackchannelEntry(plainEntry), plainOutput);
    });

    test('does not consume a structured record through an unproven single-line scope alias', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const literalMessage = '=> scope message';

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `info: Example.Category[7] ${literalMessage}\n`,
                'stdout'),
            [{
                output: `Example.Category: Information: ${literalMessage}\n`,
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: 1,
                message: 'message'
            })),
            {
                output: 'Example.Category: Information: message\n',
                category: 'stdout'
            });
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: 2,
                message: literalMessage
            })),
            undefined);
    });

    test('matches an empty single-line scope and message regardless of source order', () => {
        const entry = createEntry({ logLevel: 'Warning', message: '' });
        const raw = 'warn: Example.Category[7] => \n';

        const structuredFirst = new AppHostLogOutputCoordinator();
        assert.ok(structuredFirst.handleBackchannelEntry(entry));
        assert.deepStrictEqual(renderConsole(structuredFirst, raw, 'stdout'), []);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(renderConsole(consoleFirst, raw, 'stdout'), [{
            output: '\x1b[33mExample.Category: Warning: =>\x1b[0m\n',
            category: 'stdout'
        }]);
        assert.strictEqual(consoleFirst.handleBackchannelEntry(entry), undefined);
    });

    test('matches a single-line message containing an event-id-shaped value', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ message: 'Processing order [42] now' });

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Information: Processing order [42] now\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7] Processing order [42] now\n', 'stdout'),
            []);
    });

    test('matches a single-line category containing event-id-shaped text in both source orders', () => {
        const entry = createEntry({
            categoryName: 'Worker[42] shard',
            message: 'Message'
        });
        const raw = 'info: Worker[42] shard[7] Message\n';
        const expected = {
            output: 'Worker[42] shard: Information: Message\n',
            category: 'stdout'
        };

        const backchannelFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(
            renderConsole(backchannelFirst, raw, 'stdout'),
            []);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(consoleFirst.handleDebugAdapterOutput(raw, 'stdout'), []);
        assert.deepStrictEqual(consoleFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(consoleFirst.flush(), []);
    });

    test('matches Windows multiline output containing a bare LF', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ message: 'first\nsecond' });

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Information: first\nsecond\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7]\r\n      first\nsecond\r\n', 'stdout'),
            []);
    });

    test('matches Windows single-line output containing a bare LF', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ message: 'first\nsecond' });

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'info: Example.Category[7] first\nsecond\r\n',
                'stdout'),
            []);
        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Information: first\nsecond\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('matches chunked Windows single-line output containing multiple bare LFs', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({ message: 'first\nsecond\nthird' });

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'info: Example.Category[7] first\n',
                'stdout'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('second\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('third\r\n', 'stdout'),
            []);
        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Information: first\nsecond\nthird\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('splits an unconfirmed Linux line after a single-line record', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'info: Example.Category[7] first\n      unrelated\n',
                'stdout'),
            []);
        assert.deepStrictEqual(coordinator.flush(), [
            {
                output: 'Example.Category: Information: first\n',
                category: 'stdout'
            },
            {
                output: '      unrelated\n',
                category: 'stdout'
            }
        ]);
    });

    test('keeps consecutive Linux single-line records separate', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'info: First.Category[1] first\n'
                    + 'warn: Second.Category[2] second\n',
                'stdout'),
            [{
                output: 'First.Category: Information: first\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(coordinator.flush(), [{
            output: '\x1b[33mSecond.Category: Warning: second\x1b[0m\n',
            category: 'stdout'
        }]);
    });

    test('deduplicates unindented multiline DebugLogger messages', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first\nsecond' })),
            {
                output: 'Example.Category: Information: first\nsecond\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: first\nsecond\n',
                'console'),
            []);
    });

    test('deduplicates unindented multiline DebugLogger messages when it arrives first', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: first\nsecond\n',
                'console'),
            [{
                output: 'Example.Category: Information: first\nsecond\n',
                category: 'stdout'
            }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first\nsecond' })),
            undefined);
    });

    test('deduplicates a DebugLogger message containing a level-shaped token', () => {
        const raw = 'Example.Category: Information: Result: Error: failed\n';
        const entry = createEntry({ message: 'Result: Error: failed' });
        const backchannelFirst = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(entry), {
            output: raw,
            category: 'stdout'
        });
        assert.deepStrictEqual(renderConsole(backchannelFirst, raw, 'console'), []);

        const debugLoggerFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(renderConsole(debugLoggerFirst, raw, 'console'), [{
            output: raw,
            category: 'stdout'
        }]);
        assert.strictEqual(debugLoggerFirst.handleBackchannelEntry(entry), undefined);

        const adapterOnly = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(renderConsole(adapterOnly, raw, 'console'), [{
            output: raw,
            category: 'stdout'
        }]);
    });

    test('deduplicates a level-shaped token trailing alias after adapter flush', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'Example.Category: Information: Result: Error: first\nsecond\n';

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'console'), []);
        assert.deepStrictEqual(coordinator.flush(), [{
            output: raw,
            category: 'stdout'
        }]);
        assert.strictEqual(coordinator.handleBackchannelEntry(createEntry({
            logLevel: 'Error',
            message: 'first',
            categoryName: 'Example.Category: Information: Result'
        })), undefined);
    });

    test('deduplicates a DebugLogger-first multiline message whose continuation looks like a header', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\nOther.Category: Warning: second';

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Information: first\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Other.Category: Warning: second\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message })),
            {
                output: 'Example.Category: Information: first\nOther.Category: Warning: second\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('deduplicates a full provider twin after an ambiguous DebugLogger idle flush', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));
        const message = 'first\nOther.Category: Warning: second';

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    `Example.Category: Information: ${message}\n`,
                    'console'),
                []);

            await clock.tickAsync(250);

            assert.deepStrictEqual(emitted, [
                {
                    output: 'Example.Category: Information: first\n',
                    category: 'stdout'
                },
                {
                    output: '\x1b[33mOther.Category: Warning: second\x1b[0m\n',
                    category: 'stdout'
                }
            ]);
            assert.strictEqual(
                coordinator.handleBackchannelEntry(createEntry({ message })),
                undefined);
        } finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('deduplicates a grouped provider prefix after an ambiguous DebugLogger idle flush', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));
        const groupedMessage = 'first\nSecond.Category: Warning: second';

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    `Example.Category: Information: ${groupedMessage}\nThird.Category: Error: third\n`,
                    'console'),
                []);

            await clock.tickAsync(250);

            assert.deepStrictEqual(emitted, [
                {
                    output: 'Example.Category: Information: first\n',
                    category: 'stdout'
                },
                {
                    output: '\x1b[33mSecond.Category: Warning: second\x1b[0m\n',
                    category: 'stdout'
                },
                {
                    output: 'Third.Category: Error: third\n',
                    category: 'stderr'
                }
            ]);
            assert.strictEqual(
                coordinator.handleBackchannelEntry(createEntry({ message: groupedMessage })),
                undefined);
        } finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('bounds DebugLogger flush work with many same-header provider candidates', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const candidateCount = 1024;
        for (let index = 1; index <= candidateCount; index++) {
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: index,
                message: `x${'z'.repeat(index)}`
            }));
        }
        const raw = Array.from(
            { length: candidateCount + 1 },
            () => 'Example.Category: Information: x\n').join('');

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'console'), []);
        let slicedRawCharacters = 0;
        const originalSlice = String.prototype.slice;
        const sliceStub = sinon.stub(String.prototype, 'slice').callsFake(function (
            this: string,
            start?: number,
            end?: number) {
            if (this.toString() === raw) {
                const normalizedStart = start ?? 0;
                const normalizedEnd = end ?? raw.length;
                slicedRawCharacters += Math.max(0, normalizedEnd - normalizedStart);
            }
            return originalSlice.call(this, start, end);
        });
        let outputs: AppHostParentOutput[];
        try {
            outputs = coordinator.flush();
        } finally {
            sliceStub.restore();
        }

        assert.strictEqual(outputs.length, candidateCount + 1);
        assert.deepStrictEqual(outputs[0], {
            output: 'Example.Category: Information: x\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(outputs.at(-1), outputs[0]);
        assert.ok(
            slicedRawCharacters <= raw.length * 160,
            `Expected at most ${raw.length * 160} sliced characters, got ${slicedRawCharacters}.`);
    });

    test('does not reuse one grouped provider record for multiple DebugLogger groups', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const header = 'Example.Category: Information: x';
        const groupedMessage = `x\n${header}`;

        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: groupedMessage })));
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(`${header}\n`.repeat(4), 'console'),
            []);
        assert.deepStrictEqual(coordinator.flush(), [
            {
                output: `${header}\n`,
                category: 'stdout'
            },
            {
                output: `${header}\n`,
                category: 'stdout'
            }
        ]);
    });

    test('finds a confirmed DebugLogger group ending at an evicted middle boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const headers = Array.from(
            { length: 301 },
            (_, index) => `Example.Category: Information: ${index}`);
        const raw = `${headers.join('\n')}\n`;
        const confirmedRaw = `${headers.slice(0, 151).join('\n')}\n`;
        const groupedMessage = `0\n${headers.slice(1, 151).join('\n')}`;

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(raw, 'console'),
            []);
        const pending = (coordinator as any)._pendingDebugRecords.get('console');
        assert.strictEqual(pending.ambiguousLineBoundaries.length, 128);
        assert.ok(!pending.ambiguousLineBoundaries.some(
            (boundary: { rawOffset: number }) => boundary.rawOffset === confirmedRaw.length));
        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: groupedMessage })));
        const outputs = coordinator.flush();

        assert.strictEqual(outputs.length, 150);
        assert.deepStrictEqual(outputs[0], {
            output: `${headers[151]}\n`,
            category: 'stdout'
        });
        assert.deepStrictEqual(outputs.at(-1), {
            output: `${headers[300]}\n`,
            category: 'stdout'
        });
    });

    test('retires the merged DebugLogger identity after split provider confirmation', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const coordinator = new AppHostLogOutputCoordinator(() => { });
        const message = 'first\nOther.Category: Warning: second';

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    `Example.Category: Information: ${message}\n`,
                    'console'),
                []);

            await clock.tickAsync(250);

            assert.strictEqual(
                coordinator.handleBackchannelEntry(createEntry({
                    sequenceNumber: 1,
                    message: 'first'
                })),
                undefined);
            assert.strictEqual(
                coordinator.handleBackchannelEntry(createEntry({
                    sequenceNumber: 2,
                    categoryName: 'Other.Category',
                    logLevel: 'Warning',
                    message: 'second'
                })),
                undefined);
            assert.deepStrictEqual(
                coordinator.handleBackchannelEntry(createEntry({
                    sequenceNumber: 3,
                    message
                })),
                {
                    output: 'Example.Category: Information: first\nOther.Category: Warning: second\n',
                    category: 'stdout'
                });
        } finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('deduplicates a DebugLogger-first low-level message whose continuation looks like a header', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Debug: first\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Other.Category: Warning: second\n',
                'console'),
            []);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'dbug: Example.Category[7]\n'
                + '      first\n'
                + '      Other.Category: Warning: second\n',
                'stdout'),
            [{
                output: '\x1b[2mExample.Category: Debug: first\n'
                    + 'Other.Category: Warning: second\x1b[0m\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('deduplicates bracketed DebugLogger categories in both source orders', () => {
        const entry = createEntry({
            categoryName: 'Example.Category[7]',
            message: 'bracketed'
        });
        const raw = 'Example.Category[7]: Information: bracketed\n';
        const expected = {
            output: 'Example.Category[7]: Information: bracketed\n',
            category: 'stdout'
        };

        const backchannelFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(renderConsole(backchannelFirst, raw, 'console'), []);

        const debugLoggerFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(debugLoggerFirst.handleDebugAdapterOutput(raw, 'console'), []);
        assert.deepStrictEqual(debugLoggerFirst.handleBackchannelEntry(entry), expected);
        assert.deepStrictEqual(debugLoggerFirst.flush(), []);
    });

    test('separates an unconfirmed DebugLogger gap from a confirmed header tail', () => {
        for (const lineEnding of ['\n', '\r\n']) {
            const coordinator = new AppHostLogOutputCoordinator();

            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    `First.Category: Information: first${lineEnding}`
                        + `unconfirmed${lineEnding}`
                        + `Example.Category: Error: failed${lineEnding}`,
                    'console'),
                []);
            assert.deepStrictEqual(
                coordinator.handleBackchannelEntry(createEntry({ logLevel: 'Error', message: 'failed' })),
                {
                    output: 'Example.Category: Error: failed\n',
                    category: 'stderr'
                });
            assert.deepStrictEqual(coordinator.flush(), [{
                output: 'First.Category: Information: first\nunconfirmed\n',
                category: 'stdout'
            }]);
        }
    });

    test('finds a confirmed DebugLogger header after bounded ambiguous boundaries', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const before = Array.from({ length: 65 }, (_, index) => `before-${index}\n`).join('');
        const after = Array.from({ length: 65 }, (_, index) => `after-${index}\n`).join('');

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                `First.Category: Information: first\n${before}`
                    + `Example.Category: Error: failed\n${after}`,
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ logLevel: 'Error', message: 'failed' })),
            {
                output: 'Example.Category: Error: failed\n',
                category: 'stderr'
            });
        assert.deepStrictEqual(coordinator.flush(), [
            {
                output: `First.Category: Information: first\n${before}`,
                category: 'stdout'
            }
        ]);
    });

    test('prefers a fully confirmed DebugLogger record over an inner header twin', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\nOther.Category: Warning: second';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                categoryName: 'Other.Category',
                logLevel: 'Warning',
                message: 'second'
            })),
            {
                output: '\x1b[33mOther.Category: Warning: second\x1b[0m\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                `Example.Category: Information: ${message}\n`,
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ sequenceNumber: 2, message })),
            {
                output: 'Example.Category: Information: first\nOther.Category: Warning: second\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('does not confirm a DebugLogger boundary before an indented continuation', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first' })),
            {
                output: 'Example.Category: Information: first\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Information: first\n'
                    + '   indented\n'
                    + 'Other.Category: Error: failed\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: 2,
                categoryName: 'Other.Category',
                logLevel: 'Error',
                message: 'failed'
            })),
            {
                output: 'Other.Category: Error: failed\n',
                category: 'stderr'
            });
        assert.deepStrictEqual(coordinator.flush(), [{
            output: 'Example.Category: Information: first\n   indented\n',
            category: 'stdout'
        }]);
    });

    test('matches timestamped multiline output with scopes', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = '2026-08-10 17:40:09 warn: Example.Category[7]\n'
            + '      => RequestPath:/health\n'
            + '      Scoped warning.\n';

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('2026-08-10 ', 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('17:40:09 wa', 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw.slice('2026-08-10 17:40:09 wa'.length), 'stdout'), []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ logLevel: 'Warning', message: 'Scoped warning.' })),
            {
                output: '\x1b[33mExample.Category: Warning: Scoped warning.\x1b[0m\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('matches bracketed timestamps in single-line and multiline output', () => {
        const multiline = new AppHostLogOutputCoordinator();
        assert.ok(multiline.handleBackchannelEntry(createEntry({
            logLevel: 'Warning',
            message: 'Bracketed warning.'
        })));
        assert.deepStrictEqual(
            multiline.handleDebugAdapterOutput(
                '[12:34:56] warn: Example.Category[7]\n      Bracketed warning.\n',
                'stdout'),
            []);
        assert.deepStrictEqual(multiline.flush(), []);

        const singleLine = new AppHostLogOutputCoordinator();
        assert.ok(singleLine.handleBackchannelEntry(createEntry({ message: 'Bracketed information.' })));
        assert.deepStrictEqual(
            singleLine.handleDebugAdapterOutput(
                '[12:34:56] info: Example.Category[7] Bracketed information.\n',
                'stdout'),
            []);
        assert.deepStrictEqual(singleLine.flush(), []);
    });

    test('deduplicates a scope-free structured record after scoped ConsoleLogger output arrives first', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'info: Example.Category[7]\n'
                    + '      => RequestPath:/health\n'
                    + '      Scoped message.\n',
                'stdout'),
            [{
                output: 'Example.Category: Information: => RequestPath:/health\nScoped message.\n',
                category: 'stdout'
            }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Scoped message.' })),
            undefined);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Scoped message.\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                sequenceNumber: 2,
                message: 'Scoped message.'
            })),
            {
                output: 'Example.Category: Information: Scoped message.\n',
                category: 'stdout'
            });
    });

    test('keeps a message that begins with the scope marker', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ message: '=> started' })), {
            output: 'Example.Category: Information: => started\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(coordinator, 'info: Example.Category[7]\n      => started\n', 'stdout'),
            []);
    });

    test('deduplicates a message that begins with the scope marker after a real scope regardless of source order', () => {
        const raw = 'info: Example.Category[7]\n'
            + '      => RequestPath:/health\n'
            + '      => ConnectionId:0HN123\n'
            + '      => started\n';

        const backchannelFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(createEntry({ message: '=> started' })), {
            output: 'Example.Category: Information: => started\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(backchannelFirst, raw, 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(backchannelFirst, 'Example.Category: Information: => started\n', 'console'),
            []);

        assert.deepStrictEqual(backchannelFirst.handleBackchannelEntry(createEntry({
            sequenceNumber: 2,
            message: '=> started'
        })), {
            output: 'Example.Category: Information: => started\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(backchannelFirst, raw, 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(backchannelFirst, 'Example.Category: Information: => started\n', 'console'),
            []);

        const consoleFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(renderConsole(consoleFirst, raw, 'stdout'), [{
            output: 'Example.Category: Information: => RequestPath:/health\n=> ConnectionId:0HN123\n=> started\n',
            category: 'stdout'
        }]);
        assert.strictEqual(
            consoleFirst.handleBackchannelEntry(createEntry({ message: '=> started' })),
            undefined);
        assert.deepStrictEqual(
            renderConsole(consoleFirst, 'Example.Category: Information: => started\n', 'console'),
            []);

        const debugLoggerFirst = new AppHostLogOutputCoordinator();
        assert.deepStrictEqual(
            renderConsole(debugLoggerFirst, 'Example.Category: Information: => started\n', 'console'),
            [{
                output: 'Example.Category: Information: => started\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(renderConsole(debugLoggerFirst, raw, 'stdout'), []);
        assert.strictEqual(
            debugLoggerFirst.handleBackchannelEntry(createEntry({ message: '=> started' })),
            undefined);
    });

    test('discards ambiguous scope aliases after another source confirms the message identity', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'info: Example.Category[7]\n'
            + '      => RequestPath:/health\n'
            + '      => ConnectionId:0HN123\n'
            + '      => started\n';

        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), [{
            output: 'Example.Category: Information: => RequestPath:/health\n=> ConnectionId:0HN123\n=> started\n',
            category: 'stdout'
        }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: '=> started' })),
            undefined);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: => ConnectionId:0HN123\n=> started\n',
                'console'),
            [{
                output: 'Example.Category: Information: => ConnectionId:0HN123\n=> started\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(coordinator, 'Example.Category: Information: => started\n', 'console'),
            []);
    });

    test('prefers an exact identity over an older scope alias when DebugLogger omits the event ID', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'info: Example.Category[7]\n'
            + '      => RequestPath:/health\n'
            + '      => ConnectionId:0HN123\n'
            + '      => started\n';

        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), [{
            output: 'Example.Category: Information: => RequestPath:/health\n=> ConnectionId:0HN123\n=> started\n',
            category: 'stdout'
        }]);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ eventId: 8, message: '=> started' })),
            {
                output: 'Example.Category: Information: => started\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(coordinator, 'Example.Category: Information: => started\n', 'console'),
            []);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: => ConnectionId:0HN123\n=> started\n',
                'console'),
            []);
    });

    test('does not treat a scope marker after the first message line as a new scope boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'started\n=> still part of the message';
        const raw = 'info: Example.Category[7]\n'
            + '      => RequestPath:/health\n'
            + '      started\n'
            + '      => still part of the message\n';

        assert.deepStrictEqual(renderConsole(coordinator, raw, 'stdout'), [{
            output: 'Example.Category: Information: => RequestPath:/health\nstarted\n=> still part of the message\n',
            category: 'stdout'
        }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message })),
            undefined);
    });

    test('deduplicates an empty log message', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'info: Example.Category[7]\n';

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'stdout'), []);
        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ message: '' })), {
            output: 'Example.Category: Information: \n',
            category: 'stdout'
        });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('does not add a leading blank line to an empty message with an exception', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const entry = createEntry({
            logLevel: 'Error',
            message: '',
            exception: 'System.InvalidOperationException: boom'
        });

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(entry), {
            output: 'Example.Category: Error: System.InvalidOperationException: boom\n',
            category: 'stderr'
        });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Error: \n\nSystem.InvalidOperationException: boom\n',
                'console'),
            []);
    });

    test('correlates DebugLogger output and preserves fallback filtering elsewhere', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('Example.Category: Debug: Hidden detail.\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            renderConsole(coordinator, 'Example.Category: Error: Failed.\n', 'console'),
            [{ output: 'Example.Category: Error: Failed.\n', category: 'stderr' }]);
    });

    test('does not parse arbitrary console text as a DebugLogger category', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(renderConsole(coordinator, 'Status: Error: connection refused\n', 'console'), []);
        assert.deepStrictEqual(renderConsole(coordinator, 'step 3: Debug: cache miss\n', 'console'), []);
    });

    test('renders an adapter-only Debug record with a single-component DebugLogger category', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(coordinator, 'Something: Debug: Hidden detail.\n', 'console'),
            [{
                output: '\x1b[2mSomething: Debug: Hidden detail.\x1b[0m\n',
                category: 'stdout'
            }]);
    });

    test('renders an adapter-only Debug record with a custom DebugLogger category', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(coordinator, 'worker-job: Debug: Hidden detail.\n', 'console'),
            [{
                output: '\x1b[2mworker-job: Debug: Hidden detail.\x1b[0m\n',
                category: 'stdout'
            }]);
    });

    test('renders an adapter-only Debug record with a whitespace DebugLogger category', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(coordinator, 'Worker job: Debug: detail\n', 'console'),
            [{
                output: '\x1b[2mWorker job: Debug: detail\x1b[0m\n',
                category: 'stdout'
            }]);
    });

    test('does not append unrelated console output when the pending DebugLogger record has a twin', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            {
                output: 'Example.Category: Information: Logged.\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nprocessing\n',
                'console'),
            []);
    });

    test('resolves a DebugLogger-first record before filtering an unrelated following line', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Information: Logged.\nprocessing\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            {
                output: 'Example.Category: Information: Logged.\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('resolves an exception record before filtering an unrelated following line', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'System.InvalidOperationException: boom';

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                `Example.Category: Error: failed\n\n${exception}\nprocessing\n`,
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            {
                output: `Example.Category: Error: failed\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('retains exception line boundaries after an unconfirmed flush', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'System.InvalidOperationException: boom';

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: failed\n\n${exception}\nprocessing\n`,
                'console'),
            [{
                output: `Example.Category: Error: failed\n${exception}\nprocessing\n`,
                category: 'stderr'
            }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            undefined);
    });

    test('preserves severe fallback output after resolving a DebugLogger line boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first\nsecond' })),
            {
                output: 'Example.Category: Information: first\nsecond\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Information: first\nsecond\npanic: runtime failed\n',
                'console'),
            [
                {
                    output: 'panic: runtime failed\n',
                    category: 'stderr'
                }
            ]);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('retains possible DebugLogger boundaries after an unconfirmed flush', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nprocessing\n',
                'console'),
            [{
                output: 'Example.Category: Information: Logged.\nprocessing\n',
                category: 'stdout'
            }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('treats severe runtime output as a hard DebugLogger boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\npanic: runtime failed\n',
                'console'),
            [
                {
                    output: 'Example.Category: Information: Logged.\n',
                    category: 'stdout'
                },
                {
                    output: 'panic: runtime failed\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('preserves ambiguous DebugLogger lines before a severe boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nprocessing\npanic: runtime failed\n',
                'console'),
            [
                {
                    output: 'Example.Category: Information: Logged.\n',
                    category: 'stdout'
                },
                {
                    output: 'processing\n',
                    category: 'stdout'
                },
                {
                    output: 'panic: runtime failed\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('remembers the pending multiline identity before a severe fallback split', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: one\ntwo\npanic: runtime failed\n',
                'console'),
            [
                {
                    output: 'Example.Category: Information: one\n',
                    category: 'stdout'
                },
                {
                    output: 'two\n',
                    category: 'stdout'
                },
                {
                    output: 'panic: runtime failed\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'one\ntwo' })),
            undefined);
    });

    test('keeps a severe-shaped line when another provider confirms the merged message', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\ncritical path selected';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message })),
            {
                output: 'Example.Category: Information: first\ncritical path selected\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: first\ncritical path selected\n',
                'console'),
            []);
    });

    test('remembers a provisional severe-shaped multiline identity for a delayed provider', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\ncritical path selected';

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Information: ${message}\n`,
                'console'),
            [
                {
                    output: 'Example.Category: Information: first\n',
                    category: 'stdout'
                },
                {
                    output: 'critical path selected\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message })),
            undefined);
    });

    test('uses a fully confirmed pending record before a later severe boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(
                'Example.Category: Information: first\nsecond\n',
                'console'),
            []);
        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first\nsecond' })),
            {
                output: 'Example.Category: Information: first\nsecond\n',
                category: 'stdout'
            });
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('panic: runtime failed\n', 'console'),
            [{
                output: 'panic: runtime failed\n',
                category: 'stderr'
            }]);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('does not let indentation hide a severe DebugLogger boundary', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\n    panic: runtime failed\n',
                'console'),
            [
                {
                    output: 'Example.Category: Information: Logged.\n',
                    category: 'stdout'
                },
                {
                    output: '    panic: runtime failed\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('does not let dropped fallback state hide indented severe output', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('dbug: Debugger.Category[0]\n', 'console'),
            []);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\n',
                'console'),
            [{
                output: 'Example.Category: Information: Logged.\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(coordinator, '    panic: runtime failed\n', 'console'),
            [{
                output: '    panic: runtime failed\n',
                category: 'stderr'
            }]);
    });

    test('keeps exception text inside a dropped debug record suppressed', () => {
        const filter = new AppHostParentOutputFilter();

        assert.strictEqual(
            filter.filter(
                'dbug: Polly.Retry[0]\n'
                    + '      Retry due to System.Net.Http.HttpRequestException: timeout\n'
                    + '      Next delay 00:00:01\n',
                'console'),
            undefined);
    });

    test('requires the DebugLogger exception separator before absorbing an exception-shaped line', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nSystem.InvalidOperationException: separate\n',
                'console'),
            [
                {
                    output: 'Example.Category: Information: Logged.\n',
                    category: 'stdout'
                },
                {
                    output: 'System.InvalidOperationException: separate\n',
                    category: 'stderr'
                }
            ]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('recognizes a base System.Exception inside a DebugLogger exception block', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'System.Exception: boom\n   at Example.Run()';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            {
                output: `Example.Category: Error: failed\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: failed\n\n${exception}\n`,
                'console'),
            []);
    });

    test('recognizes a custom CLR exception type without an Exception suffix', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'MyNamespace.Failure: boom\n   at Example.Run()';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            {
                output: `Example.Category: Error: failed\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: failed\n\n${exception}\n`,
                'console'),
            []);
    });

    test('recognizes a nested custom CLR exception type', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'MyNamespace.Outer+Failure: boom\n   at Example.Run()';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            {
                output: `Example.Category: Error: failed\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: failed\n\n${exception}\n`,
                'console'),
            []);
    });

    test('keeps a dotted message line after a blank DebugLogger continuation', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\n\nBuild.Status: healthy';

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ message })), {
            output: `Example.Category: Information: ${message}\n`,
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Information: ${message}\n`,
                'console'),
            []);
    });

    test('keeps an ambiguous boundary before an unproven custom exception type', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ message: 'first' })), {
            output: 'Example.Category: Information: first\n',
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: first\n\nBuild.Status: healthy\n',
                'console'),
            []);
    });

    test('does not treat prose beginning with at as a CLR stack frame', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\n\nMyNamespace.Failure: healthy\n   at least one worker (healthy)';

        assert.deepStrictEqual(coordinator.handleBackchannelEntry(createEntry({ message })), {
            output: `Example.Category: Information: ${message}\n`,
            category: 'stdout'
        });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Information: ${message}\n`,
                'console'),
            []);
    });

    test('recognizes a generic custom CLR exception with a multiline message', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const exception = 'MyNamespace.Failure`1[System.String]: first\nsecond\n   at Example.Run()';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message: 'failed',
                exception
            })),
            {
                output: `Example.Category: Error: failed\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: failed\n\n${exception}\n`,
                'console'),
            []);
    });

    test('does not reuse later exception evidence for an earlier dotted message line', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const message = 'first\n\nBuild.Status: healthy';
        const exception = 'MyNamespace.Failure: boom\n   at Example.Run()';

        assert.deepStrictEqual(
            coordinator.handleBackchannelEntry(createEntry({
                logLevel: 'Error',
                message,
                exception
            })),
            {
                output: `Example.Category: Error: ${message}\n${exception}\n`,
                category: 'stderr'
            });
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Error: ${message}\n\n${exception}\n`,
                'console'),
            []);
    });

    test('preserves multiline identity after matching a single-line ConsoleLogger alias', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: first\nsecond\nprocessing\n',
                'console'),
            [{
                output: 'Example.Category: Information: first\nsecond\nprocessing\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'info: Example.Category[7] first second\n',
                'stdout'),
            []);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'first\nsecond' })),
            undefined);
    });

    test('matches a DebugLogger trailing alias with a ConsoleLogger leading-scope alias', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nprocessing\n',
                'console'),
            [{
                output: 'Example.Category: Information: Logged.\nprocessing\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'info: Example.Category[7]\n'
                    + '      => RequestPath:/health\n'
                    + '      Logged.\n',
                'stdout'),
            []);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'Logged.' })),
            undefined);
    });

    test('matches maximum-size alternative boundary sets', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const debugTail = Array.from({ length: 128 }, (_, index) => `debug-${index}\n`).join('');
        const consoleScopes = Array.from({ length: 128 }, (_, index) => `      => scope-${index}\n`).join('');

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `Example.Category: Information: shared\n${debugTail}`,
                'console'),
            [{
                output: `Example.Category: Information: shared\n${debugTail}`,
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                `info: Example.Category[7]\n${consoleScopes}      shared\n`,
                'stdout'),
            []);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: 'shared' })),
            undefined);
    });

    test('filters a DebugLogger tail after a ConsoleLogger single-line prefix match', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'info: Example.Category[7] Logged.\n',
                'stdout'),
            [{
                output: 'Example.Category: Information: Logged.\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'Example.Category: Information: Logged.\nprocessing\n',
                'console'),
            []);
    });

    test('bounds retained DebugLogger line-boundary candidates', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'Example.Category: Information: first\n'
            + Array.from({ length: 500 }, (_, index) => `line ${index}\n`).join('');

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'console'), []);

        const pending = (coordinator as any)._pendingDebugRecords.get('console');
        assert.strictEqual(pending.ambiguousLineBoundaries.length, 128);
        assert.strictEqual(
            pending.ambiguousLineBoundaries[0].rawOffset,
            'Example.Category: Information: first\n'.length);
        assert.strictEqual(
            pending.ambiguousLineBoundaries.at(-1).rawOffset,
            raw.length - 'line 499\n'.length);
    });

    test('bounds retained ConsoleLogger scope-boundary candidates', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const raw = 'info: Example.Category[7]\n'
            + Array.from({ length: 500 }, (_, index) => `      => scope-${index}\n`).join('');

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'stdout'), []);

        const pending = (coordinator as any)._pendingRecords.get('stdout');
        assert.strictEqual(pending.leadingScopeBodyOffsets.length, 128);
        assert.strictEqual(pending.leadingScopeBodyOffsets[0], '=> scope-0\n'.length);
        assert.strictEqual(
            pending.leadingScopeBodyOffsets.at(-1),
            pending.body.length);
    });

    test('bounds retained single-line scope-boundary candidates', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const body = Array.from(
            { length: 500 },
            (_, index) => `=> scope-${index}:value`).join(' ');
        const raw = `info: Example.Category[7] ${body}\n`;

        assert.deepStrictEqual(coordinator.handleDebugAdapterOutput(raw, 'stdout'), []);

        const pending = (coordinator as any)._pendingRecords.get('stdout');
        assert.strictEqual(pending.leadingScopeBodyOffsets.length, 128);
        assert.strictEqual(pending.leadingScopeBodyOffsets[0], '=> scope-0:value '.length);
        assert.strictEqual(pending.leadingScopeBodyOffsets.at(-1), body.length);
    });

    test('bounds retained single-line header candidates', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const body = `Root${Array.from({ length: 300 }, (_, index) => `[${index}] part`).join('')} Message`;

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput(`info: ${body}\n`, 'stdout'),
            []);

        const pending = (coordinator as any)._pendingRecords.get('stdout');
        assert.strictEqual(pending.alternativeRecords.length, 128);
        assert.strictEqual(pending.alternativeRecords[0].eventId, 1);
        assert.strictEqual(pending.alternativeRecords.at(-1).eventId, 299);
    });

    test('bounds the pending DebugLogger raw buffer', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const line = `${'x'.repeat(1024)}\n`;
        const outputs = coordinator.handleDebugAdapterOutput(
            `Example.Category: Information: first\n${line.repeat(65)}`,
            'console');

        assert.ok(outputs.length > 0);
        assert.strictEqual((coordinator as any)._pendingDebugRecords.has('console'), false);
    });

    test('keeps an exception-shaped one-line DebugLogger message intact', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const expected = {
            output: 'Example.Category: Error: ValidationError: invalid input\n',
            category: 'stderr' as const
        };

        assert.deepStrictEqual(
            renderConsole(coordinator, 'Example.Category: Error: ValidationError: invalid input\n', 'console'),
            [expected]);
        assert.strictEqual(coordinator.handleBackchannelEntry(createEntry({
            logLevel: 'Error',
            message: 'ValidationError: invalid input'
        })), undefined);
    });

    test('does not parse arbitrary text before a ConsoleLogger level token', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(coordinator, 'status warn: Example.Category[7] Still working.\n', 'stdout'),
            [{ output: 'status warn: Example.Category[7] Still working.\n', category: 'stdout' }]);
    });

    test('waits for the complete ConsoleLogger record before correlating it', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        assert.ok(coordinator.handleBackchannelEntry(createEntry({ message: 'First line.' })));

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('info: Example.Category[7]\n      First line.\n', 'stdout'),
            []);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('      \n', 'stdout'),
            []);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('keeps blank lines inside console-category error blocks', () => {
        const filter = new AppHostParentOutputFilter();

        assert.deepStrictEqual(
            filter.filter(
                'Unhandled exception. System.InvalidOperationException: boom\n\n   at Example.Run()\n',
                'console'),
            {
                output: 'Unhandled exception. System.InvalidOperationException: boom\n\n   at Example.Run()\n',
                category: 'stderr'
            });
    });

    test('keeps Python traceback blocks on stderr', () => {
        const filter = new AppHostParentOutputFilter();
        const traceback = 'Traceback (most recent call last):\n'
            + '  File "app.py", line 1, in <module>\n'
            + 'ValueError: invalid value\n';

        assert.deepStrictEqual(
            filter.filter(traceback, 'console'),
            {
                output: traceback,
                category: 'stderr'
            });
    });

    test('keeps Python exception group tracebacks on stderr', () => {
        const filter = new AppHostParentOutputFilter();
        const traceback = '+ Exception Group Traceback (most recent call last):\n'
            + '  |   File "app.py", line 1, in <module>\n'
            + '  | ExceptionGroup: grouped failure (1 sub-exception)\n';

        assert.deepStrictEqual(filter.filter(traceback, 'console'), {
            output: traceback,
            category: 'stderr'
        });
    });

    test('keeps multiline Python exception messages on stderr until a logger boundary', () => {
        const filter = new AppHostParentOutputFilter();
        const traceback = 'Traceback (most recent call last):\n'
            + '  File "app.py", line 1, in <module>\n'
            + 'ValueError: first\n'
            + 'second line\n';

        assert.deepStrictEqual(filter.filter(traceback, 'console'), {
            output: traceback,
            category: 'stderr'
        });
        assert.deepStrictEqual(filter.filter('info: Example.Category[7]\n', 'console'), {
            output: 'info: Example.Category[7]\n',
            category: 'stdout'
        });

        const coordinator = new AppHostLogOutputCoordinator();
        const outputs = coordinator.handleDebugAdapterOutput(traceback, 'console');
        assert.strictEqual(outputs.map(output => output.output).join(''), traceback);
        assert.ok(outputs.every(output => output.category === 'stderr'));
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('ordinary stdout\n', 'stdout'),
            [{
                output: 'ordinary stdout\n',
                category: 'stdout'
            }]);
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('ordinary console\n', 'console'),
            []);
    });

    test('flushes a partial Python exception continuation before a DAP category switch', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const tracebackPrefix = 'Traceback (most recent call last):\n'
            + '  File "app.py", line 1, in <module>\n'
            + 'ValueError: first\n';

        const outputs = coordinator.handleDebugAdapterOutput(`${tracebackPrefix}second line`, 'console');
        assert.strictEqual(outputs.map(output => output.output).join(''), tracebackPrefix);
        assert.ok(outputs.every(output => output.category === 'stderr'));
        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('ordinary stdout\n', 'stdout'),
            [
                {
                    output: 'second line',
                    category: 'stderr'
                },
                {
                    output: 'ordinary stdout\n',
                    category: 'stdout'
                }
            ]);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

    test('keeps custom Python exception terminators on stderr', () => {
        const filter = new AppHostParentOutputFilter();
        const traceback = 'Traceback (most recent call last):\n'
            + '  File "app.py", line 1, in <module>\n'
            + 'MyFailure: invalid value\n';

        assert.deepStrictEqual(
            filter.filter(traceback, 'console'),
            {
                output: traceback,
                category: 'stderr'
            });
        assert.strictEqual(
            new AppHostParentOutputFilter().filter('MyFailure: invalid value\n', 'console'),
            undefined);
    });

    test('keeps Python traceback chain markers on stderr', () => {
        for (const marker of [
            'During handling of the above exception, another exception occurred:',
            'The above exception was the direct cause of the following exception:'
        ]) {
            const filter = new AppHostParentOutputFilter();
            const traceback = 'Traceback (most recent call last):\n'
                + '  File "app.py", line 1, in <module>\n'
                + 'FirstFailure: first\n\n'
                + `${marker}\n\n`
                + 'Traceback (most recent call last):\n'
                + '  File "app.py", line 2, in <module>\n'
                + 'SecondFailure: second\n';

            assert.deepStrictEqual(
                filter.filter(traceback, 'console'),
                {
                    output: traceback,
                    category: 'stderr'
                });
        }
    });

    test('does not start an error block from a standalone Python chain marker', () => {
        const filter = new AppHostParentOutputFilter();

        assert.strictEqual(
            filter.filter(
                'The above exception was the direct cause of the following exception:\n',
                'console'),
            undefined);
        assert.strictEqual(filter.filter('  ordinary output\n', 'console'), undefined);
    });

    test('correlates a scoped ConsoleLogger empty message', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            renderConsole(
                coordinator,
                'info: Example.Category[7]\n'
                    + '      => RequestPath:/health\n'
                    + '      \n',
                'stdout'),
            [{
                output: 'Example.Category: Information: => RequestPath:/health\n',
                category: 'stdout'
            }]);
        assert.strictEqual(
            coordinator.handleBackchannelEntry(createEntry({ message: '' })),
            undefined);
    });

    test('suppresses replayed sequences within a generation and accepts a new generation or reset', () => {
        const coordinator = new AppHostLogOutputCoordinator();
        const laterEntry = createEntry({
            generationId: '11111111-1111-1111-1111-111111111111',
            sequenceNumber: 42,
            message: 'Later entry.'
        });
        const earlierEntry = createEntry({
            generationId: '11111111-1111-1111-1111-111111111111',
            sequenceNumber: 41,
            message: 'Earlier entry.'
        });
        const nextGenerationEntry = createEntry({
            generationId: '22222222-2222-2222-2222-222222222222',
            sequenceNumber: 42,
            message: 'Next generation entry.'
        });

        assert.ok(coordinator.handleBackchannelEntry(laterEntry));
        assert.ok(coordinator.handleBackchannelEntry(earlierEntry));
        assert.strictEqual(coordinator.handleBackchannelEntry(laterEntry), undefined);
        assert.ok(coordinator.handleBackchannelEntry(nextGenerationEntry));

        coordinator.reset();

        assert.ok(coordinator.handleBackchannelEntry(laterEntry));
    });

    test('matches the full backchannel replay buffer window', () => {
        const replayCoordinator = new AppHostLogOutputCoordinator();
        for (let sequenceNumber = 1; sequenceNumber <= 1000; sequenceNumber++) {
            const entry = createEntry({ sequenceNumber, message: `Entry ${sequenceNumber}` });
            assert.ok(replayCoordinator.handleBackchannelEntry(entry));
        }
        for (let sequenceNumber = 1; sequenceNumber <= 1000; sequenceNumber++) {
            const entry = createEntry({ sequenceNumber, message: `Entry ${sequenceNumber}` });
            assert.strictEqual(replayCoordinator.handleBackchannelEntry(entry), undefined);
        }

        const evictionCoordinator = new AppHostLogOutputCoordinator();
        for (let sequenceNumber = 1; sequenceNumber <= 1001; sequenceNumber++) {
            const entry = createEntry({ sequenceNumber, message: `Entry ${sequenceNumber}` });
            assert.ok(evictionCoordinator.handleBackchannelEntry(entry));
        }
        assert.ok(evictionCoordinator.handleBackchannelEntry(
            createEntry({ sequenceNumber: 1, message: 'Evicted entry' })));
    });

    test('idle flush releases final adapter-only and partial output', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput('dbug: Example.Category[7]\n      Last detail.\n', 'stdout'),
                []);
            assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('native partial', 'stderr'), []);

            await clock.tickAsync(1000);

            assert.deepStrictEqual(emitted, [
                {
                    output: '\x1b[2mExample.Category: Debug: Last detail.\x1b[0m\n',
                    category: 'stdout'
                },
                {
                    output: 'native partial',
                    category: 'stderr'
                }
            ]);
        }
        finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('idle flush releases an empty adapter-only record', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput('dbug: Example.Category[7]\n', 'stdout'),
                []);

            await clock.tickAsync(250);

            assert.deepStrictEqual(emitted, [{
                output: '\x1b[2mExample.Category: Debug: \x1b[0m\n',
                category: 'stdout'
            }]);
        }
        finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('continuous partial chunks do not postpone the original idle flush deadline', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));

        try {
            assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('continuous ', 'stderr'), []);
            await clock.tickAsync(100);
            assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('partial ', 'stderr'), []);
            await clock.tickAsync(100);
            assert.deepStrictEqual(coordinator.handleDebugAdapterOutput('output', 'stderr'), []);
            await clock.tickAsync(50);

            assert.deepStrictEqual(emitted, [{
                output: 'continuous partial output',
                category: 'stderr'
            }]);
        }
        finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('restarts idle deadlines for replacements but not continuations', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const emitted: AppHostParentOutput[] = [];
        const coordinator = new AppHostLogOutputCoordinator(output => emitted.push(output));

        try {
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    'info: Extended.Category[7]\n      first\n',
                    'stdout'),
                []);
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    'info: Replaced.Category[7]\n      old\n',
                    'stderr'),
                []);
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    'Replaced.DebugCategory: Information: old\n',
                    'console'),
                []);

            await clock.tickAsync(100);

            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput('      second\n', 'stdout'),
                []);
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    'info: Replacement.Category[7]\n      replacement\n',
                    'stderr'),
                [{
                    output: 'Replaced.Category: Information: old\n',
                    category: 'stdout'
                }]);
            assert.deepStrictEqual(
                coordinator.handleDebugAdapterOutput(
                    'Replacement.DebugCategory: Information: replacement\n',
                    'console'),
                []);

            await clock.tickAsync(150);

            assert.deepStrictEqual(emitted, [
                {
                    output: 'Extended.Category: Information: first\nsecond\n',
                    category: 'stdout'
                },
                {
                    output: 'Replaced.DebugCategory: Information: old\n',
                    category: 'stdout'
                },
                {
                    output: 'Replacement.DebugCategory: Information: replacement\n',
                    category: 'stdout'
                }
            ]);

            await clock.tickAsync(100);

            assert.deepStrictEqual(emitted, [
                {
                    output: 'Extended.Category: Information: first\nsecond\n',
                    category: 'stdout'
                },
                {
                    output: 'Replaced.DebugCategory: Information: old\n',
                    category: 'stdout'
                },
                {
                    output: 'Replacement.DebugCategory: Information: replacement\n',
                    category: 'stdout'
                },
                {
                    output: 'Replacement.Category: Information: replacement\n',
                    category: 'stdout'
                }
            ]);
        }
        finally {
            coordinator.reset();
            clock.restore();
        }
    });

    test('cancellation flush retains the final incomplete record', () => {
        const coordinator = new AppHostLogOutputCoordinator();

        assert.deepStrictEqual(
            coordinator.handleDebugAdapterOutput('crit: Example.Category[7]\n      Fatal', 'stderr'),
            []);
        assert.deepStrictEqual(coordinator.flush(), [{
            output: 'Example.Category: Critical: Fatal\n',
            category: 'stderr'
        }]);
        assert.deepStrictEqual(coordinator.flush(), []);
    });

});

function renderConsole(
    coordinator: AppHostLogOutputCoordinator,
    output: string,
    category: string): AppHostParentOutput[] {
    return [...coordinator.handleDebugAdapterOutput(output, category), ...coordinator.flush()];
}

function createEntry(overrides: Partial<AppHostLogEntry> = {}): AppHostLogEntry {
    return {
        sequenceNumber: 1,
        logLevel: 'Information',
        message: 'Repeated message.',
        categoryName: 'Example.Category',
        eventId: 7,
        exception: null,
        ...overrides
    };
}
