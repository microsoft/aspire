// TEMPORARY diagnostics investigation helper. Do not include in the clean fix PR.
import assert from 'node:assert/strict';
import { appendFileSync, readFileSync } from 'node:fs';
import { join, win32 } from 'node:path';

type Variant = 'baseline' | 'fixed';

const revisions = {
    baseline: { commit: '103ae5d528b9002875b9fd9b322798d51d706cef', collectorBlob: 'b105cbf4fc6a0ca69553a73e14234eb0a83e878f' },
    fixed: { commit: '3489dec97000d1fe3795e82a0643e5dccb4ce7c3', collectorBlob: '47adf205b5146872ea6d5d5aef0dde81b010222d' },
};
const suiteBlob = '5fd895122b4a68af7b57c08bbd96fe888c542598';
const titles = [
    'collects only intended storage diagnostics and redacts them',
    'does not enumerate unselected runtime state as files disappear',
    'uses case-insensitive lease exclusions on Windows',
    'collects storage diagnostics with an exclusively held Dashboard runs lock',
    'collects storage diagnostics with an exclusively held Dashboard resumes lock',
    'collects storage diagnostics with an exclusively held workspace configuration cache lock',
    'collects storage diagnostics with an exclusively held bundle extraction lock',
    'reports a locked diagnostic while retaining and redacting other files and sources',
    'reports failures from multiple sources without skipping later sources',
    'reports a diagnostic disappearing after enumeration and collects other sources',
    'reports redaction read failures without writing unredacted text or skipping other diagnostics',
    'preserves original log bytes when no redaction is needed',
    'does not follow diagnostic symlinks outside selected inputs',
    'tolerates diagnostics sources that were never created',
    'collects and redacts workspace configuration and fixture sources without runtime state',
    'collects workspace diagnostics with an exclusively held package restore lock',
    'collects workspace diagnostics with an exclusively held project layout lock',
    'retains workspace settings and remaining fixture sources when a source fails',
].map(title => `E2E storage diagnostics ${title}`);
// The original fix already handled Dashboard locks and lease casing. Log bytes and absent sources
// also pass unchanged; every other case is an exact regression against the expanded collector.
const baselinePassTitles = [titles[2], titles[3], titles[4], titles[11], titles[13]];
const baselineFailureTitles = titles.filter(title => !baselinePassTitles.includes(title));
const nativeLocks = [
    {
        title: titles[5],
        source: '\\aspire-home\\cache\\workspace-config-locks\\workspace.lock',
        destination: '\\diagnostics\\aspire-home\\cache\\workspace-config-locks\\workspace.lock',
        collector: 'copyStorageDiagnostics',
    },
    {
        title: titles[6],
        source: '\\aspire-home\\packages\\.aspire-bundle-lock',
        destination: '\\diagnostics\\aspire-home\\packages\\.aspire-bundle-lock',
        collector: 'copyStorageDiagnostics',
    },
    {
        title: titles[15],
        source: '\\workspace\\.aspire\\integrations\\package-restore\\hash\\restore.lock',
        destination: '\\workspace-diagnostics\\.aspire\\integrations\\package-restore\\hash\\restore.lock',
        collector: 'copyWorkspaceDiagnostics',
    },
    {
        title: titles[16],
        source: '\\workspace\\.aspire\\integrations\\apphosts\\hash\\project-layouts\\prepare.lock',
        destination: '\\workspace-diagnostics\\.aspire\\integrations\\apphosts\\hash\\project-layouts\\prepare.lock',
        collector: 'copyWorkspaceDiagnostics',
    },
];

function isObject(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function object(value: unknown): Record<string, unknown> {
    assert.ok(isObject(value), 'Expected a JSON object');
    return value;
}

function text(value: unknown): string {
    assert.ok(typeof value === 'string', 'Expected a string');
    return value;
}

function variant(value: unknown): Variant {
    assert.ok(value === 'baseline' || value === 'fixed', 'Expected baseline or fixed');
    return value;
}

function testSet(value: unknown, expectedTitles: string[]): Map<string, Record<string, unknown>> {
    assert.ok(Array.isArray(value), 'Expected a Mocha test array');
    const names: string[] = [];
    const errors = new Map<string, Record<string, unknown>>();
    for (const item of value) {
        const test = object(item);
        const title = text(test.fullTitle);
        names.push(title);
        assert.equal(test.currentRetry, 0, `${title}: retries are not evidence`);
        assert.ok(typeof test.duration === 'number' && test.duration >= 0, `${title}: not executed`);
        assert.ok(win32.normalize(text(test.file)).endsWith('\\out\\test\\e2eStorageDiagnostics.test.js'));
        errors.set(title, object(test.err));
    }
    assert.deepEqual(names.sort(), [...expectedTitles].sort(), 'Unexpected, missing, or duplicated test titles');
    return errors;
}

function assertion(error: Record<string, unknown> | undefined, operator: string): Record<string, unknown> {
    assert.ok(error);
    assert.equal(error.name, 'AssertionError');
    assert.equal(error.code, 'ERR_ASSERTION');
    assert.equal(error.operator, operator);
    return error;
}

function assertionDiff(error: Record<string, unknown> | undefined, actualOnly: Record<string, string>, expectedOnly: Record<string, string>): void {
    error = assertion(error, 'deepStrictEqual');
    // Mocha's JSON reporter serializes actual/expected with its own stringify(), not JSON:
    //   {\n  "aspire-home/packages/.aspire-bundle-lock": ""\n}
    // There are no commas. Require exactly the known added/omitted/unredacted files while
    // preserving every other line; array values use the same newline-separated representation.
    // https://mochajs.org/next/reporters/json/
    const fileLines = (files: Record<string, string>) => Object.entries(files)
        .map(([name, value]) => `  ${JSON.stringify(name)}: ${JSON.stringify(value)}`);
    assert.deepEqual(
        [...text(error.actual).split(/\r?\n/), ...fileLines(expectedOnly)].sort(),
        [...text(error.expected).split(/\r?\n/), ...fileLines(actualOnly)].sort(),
        'Baseline assertion changed for reasons other than the known file-selection/redaction differences',
    );
}

function validate(reportValue: unknown, mode: Variant, exitCode: number) {
    const report = object(reportValue);
    const stats = object(report.stats);
    const failureCount = mode === 'baseline' ? 13 : 0;
    assert.equal(exitCode, failureCount, 'Unexpected Mocha exit code');
    assert.equal(stats.suites, 1);
    assert.equal(stats.tests, 18, 'All eighteen tests must execute');
    assert.equal(stats.passes, 18 - failureCount);
    assert.equal(stats.pending, 0, 'Skipped tests are not evidence');
    assert.equal(stats.failures, failureCount);
    assert.ok(typeof stats.duration === 'number' && stats.duration > 0);
    assert.deepEqual(report.pending, []);

    const all = testSet(report.tests, titles);
    const failures = testSet(report.failures, mode === 'baseline' ? baselineFailureTitles : []);
    const passes = testSet(report.passes, mode === 'baseline' ? baselinePassTitles : titles);
    for (const [title, error] of all) {
        assert.deepEqual(error, failures.get(title) ?? passes.get(title), 'Inconsistent Mocha result arrays');
    }
    for (const error of passes.values()) {
        assert.deepEqual(error, {}, 'Passing tests must not carry errors');
    }

    if (mode === 'baseline') {
        assertionDiff(failures.get(titles[0]), {
            'aspire-home/cache/skills/content.json': 'Cached runtime state.\n',
            'aspire-home/cache/workspace-config-locks/workspace.lock': '',
            'aspire-home/dashboard-backup/info.json': 'Not a selected diagnostic.\n',
            'aspire-home/packages/.aspire-bundle-lock': '',
            'aspire-home/unrecognized-state.json': 'Not a selected diagnostic.\n',
            'settings/CrashpadMetrics-active.pma': 'Active runtime state.\n',
        }, {});
        const enumeration = assertion(failures.get(titles[1]), 'deepStrictEqual');
        assert.equal(text(enumeration.actual).replace(/\r\n/g, '\n'),
            '[\n  "cache/workspace-config-locks"\n  "cache/workspace-config-locks/workspace.lock"\n  "dashboard"\n]');
        assert.equal(enumeration.expected, '[]');

        const aggregateAssertionTitles = [titles[7], titles[8], titles[9]];
        const missingRejectionTitles = [titles[10], titles[17]];
        const symlinkError = failures.get(titles[12]);
        assert.ok(symlinkError);
        // This baseline junction case resolves on hosted Windows/Node 22 but rejects a
        // non-AggregateError on local Windows/Node 25. Accept those two exact assertion
        // signatures only for this title; every other failure retains its single contract.
        if (symlinkError.operator === 'rejects') {
            missingRejectionTitles.push(titles[12]);
        } else {
            aggregateAssertionTitles.push(titles[12]);
        }

        for (const title of aggregateAssertionTitles) {
            const error = assertion(failures.get(title), '==');
            assert.equal(error.actual, 'false');
            assert.equal(error.expected, 'true');
            assert.equal(text(error.message).replace(/\r\n/g, '\n'),
                'The expression evaluated to a falsy value:\n\n  assert.ok(error instanceof AggregateError)\n');
        }
        for (const title of missingRejectionTitles) {
            const error = assertion(failures.get(title), 'rejects');
            assert.equal(error.message, 'Missing expected rejection.');
            assert.equal(error.actual, undefined);
            assert.equal(error.expected, undefined);
        }
        assertionDiff(failures.get(titles[14]), {
            '.aspire/integrations/apphosts/hash/project-layouts/prepare.lock': '',
            '.aspire/integrations/package-restore/hash/restore.lock': '',
            '.aspire/modules/generated.ts': 'Generated SDK cache.\n',
            '.aspire/other-runtime-state.json': 'Not selected diagnostics.\n',
            'AspireE2E.AppHost/AspireE2E.AppHost.csproj': '<Project url="http://localhost:1234/login?t=private-token" />\n',
            'AspireE2E.WinUI/App.xaml': '<Application url="http://localhost:1234/login?t=private-token" />\n',
        }, {
            'aspire.config.json': '{"appHost":{"path":"AspireE2E.AppHost/AppHost.cs"}}\n',
            'AspireE2E.AppHost/AspireE2E.AppHost.csproj': '<Project url="http://localhost:1234/login?t=<redacted>" />\n',
            'AspireE2E.WinUI/App.xaml': '<Application url="http://localhost:1234/login?t=<redacted>" />\n',
        });

        for (const lock of nativeLocks) {
            const error = failures.get(lock.title);
            assert.ok(error);
            assert.equal(error.code, 'EBUSY');
            assert.equal(error.syscall, 'copyfile');
            const source = win32.normalize(text(error.path));
            assert.ok(win32.isAbsolute(source) && source.endsWith(lock.source), 'Wrong native lock source');
            const root = source.slice(0, -lock.source.length);
            assert.match(win32.basename(root), /^aspire-e2e-storage-diagnostics-[^\\]+$/);
            assert.equal(win32.normalize(text(error.dest)), `${root}${lock.destination}`, 'Wrong copy destination');
            // The workflow pins the unchanged suite. Its control copy must establish EBUSY before
            // collectDiagnostics() is called; control/setup/cleanup assertion failures cannot pass here.
            // Workspace cases also audit cpSync's filter so the lock goes through copyfile, not the
            // uninstrumented Node 25 native-cp fast path that can report an unrelated EPIPE instead.
            assert.ok(text(error.stack).includes(`at Object.${lock.collector} [as collect] (`));
            assert.match(text(error.stack), /\bat collectDiagnostics \(/);
            assert.match(text(error.stack), /\bat withExclusiveFileLock \(/);
        }
    }

    return { tests: 18, passes: 18 - failureCount, pending: 0, failures: failureCount };
}

function readJson(file: string): unknown {
    return JSON.parse(readFileSync(file, 'utf8'));
}

function parseExitCode(value: string): number {
    assert.match(value.trim(), /^-?\d+$/, 'Missing or invalid process exit code');
    return Number(value.trim());
}

function summarize(root: string): void {
    assert.equal(process.env.BASELINE_COMMIT, revisions.baseline.commit, 'Wrong baseline revision');
    assert.equal(process.env.FIXED_COMMIT, revisions.fixed.commit, 'Wrong fixed revision');
    assert.match(text(process.env.GITHUB_SHA), /^[0-9a-f]{40}$/, 'Missing workflow revision');
    function emit(line: string) {
        console.log(line);
        if (process.env.GITHUB_STEP_SUMMARY) {
            appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${line}\n`);
        }
    }

    emit('# Temporary diagnostics lock reproduction');
    emit('');
    emit(`Baseline collector: \`${process.env.BASELINE_COMMIT}\`. Fixed collector and unchanged suite: \`${process.env.FIXED_COMMIT}\`.`);
    emit(`Workflow revision: \`${process.env.GITHUB_SHA}\`. Target: windows-latest / x64 / Node 22.`);
    emit('Three isolated runners per variant, three iterations each: 9 baseline + 9 fixed suite executions, 324 test executions expected, no skips or retries.');
    emit('A baseline row requires 5 passes / 13 exact regressions: 9 selection/aggregation/redaction assertions and 4 native EBUSY copyfile errors after the OS-lock controls (workspace configuration cache, bundle extraction, package restore, project layout). Fixed rows require all 18 tests passing.');
    emit('');
    emit('| Variant | Runner | Iteration | OS version | Node | Tests | Pass | Fail | Pending | Contract |');
    emit('|---|---|---|---|---|---|---|---|---|---|');

    let accepted = 0;
    for (const mode of ['baseline', 'fixed'] as const) {
        for (let runner = 1; runner <= 3; runner++) {
            const directory = join(root, `diagnostics-lock-${mode}-${runner}`);
            for (let iteration = 1; iteration <= 3; iteration++) {
                try {
                    const context = object(readJson(join(directory, 'context.json')));
                    assert.equal(context.variant, mode);
                    assert.equal(context.runner, runner);
                    assert.equal(context.iterations, 3);
                    assert.equal(context.workflowRevision, process.env.GITHUB_SHA);
                    assert.equal(context.baselineRevision, process.env.BASELINE_COMMIT);
                    assert.equal(context.fixedRevision, process.env.FIXED_COMMIT);
                    assert.equal(context.testRevision, process.env.FIXED_COMMIT);
                    assert.equal(context.collectorRevision, mode === 'baseline' ? process.env.BASELINE_COMMIT : process.env.FIXED_COMMIT);
                    assert.equal(context.runnerOs, 'Windows');
                    assert.equal(context.runnerArch, 'X64');
                    assert.equal(context.nodePlatform, 'win32');
                    assert.equal(context.nodeArch, 'x64');
                    assert.match(text(context.nodeVersion), /^v22\.\d+\.\d+$/);
                    assert.equal(context.corepack, '0.34.7');
                    assert.equal(context.yarn, '1.22.22');
                    assert.equal(context.suiteBlob, suiteBlob, 'Not the final eighteen-test suite');
                    assert.equal(context.collectorBlob, revisions[mode].collectorBlob, 'Wrong collector blob');

                    const prefix = join(directory, `iteration-${iteration}`);
                    const exitCode = parseExitCode(readFileSync(`${prefix}.exit-code.txt`, 'utf8'));
                    const result = validate(readJson(`${prefix}.json`), variant(mode), exitCode);
                    const os = text(context.osVersion).replace(/[|\r\n]/g, ' ');
                    emit(`| ${mode} | ${runner} | ${iteration} | ${os} | ${context.nodeVersion} | ${result.tests} | ${result.passes} | ${result.failures} | ${result.pending} | Accepted |`);
                    accepted++;
                } catch (error) {
                    // Keep reporting other runners, but never turn a missing/malformed result into success.
                    const message = (error instanceof Error ? error.message : String(error)).replace(/[|\r\n]/g, ' ');
                    emit(`| ${mode} | ${runner} | ${iteration} | - | - | - | - | - | - | REJECTED: ${message} |`);
                    console.error(error);
                }
            }
        }
    }

    emit('');
    emit(`Accepted suite executions: ${accepted}/18. Matrix job result: ${process.env.REPRODUCE_RESULT}.`);
    assert.equal(accepted, 18, 'Missing, skipped, malformed, or unexpected results; this is not a successful reproduction');
    assert.equal(process.env.REPRODUCE_RESULT, 'success', 'A matrix job failed outside the result parser');
}

const [command, ...args] = process.argv.slice(2);
if (command === 'verify') {
    assert.equal(args.length, 3, 'Usage: verify <baseline|fixed> <mocha.json> <exit-code>');
    console.log(JSON.stringify(validate(readJson(args[1]), variant(args[0]), parseExitCode(args[2]))));
} else if (command === 'summarize') {
    assert.equal(args.length, 1, 'Usage: summarize <downloaded-artifact-directory>');
    summarize(args[0]);
} else {
    throw new Error('Expected verify or summarize');
}
