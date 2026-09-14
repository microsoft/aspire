// TEMPORARY diagnostics investigation helper. Do not include in the clean fix PR.
import assert from 'node:assert/strict';
import { appendFileSync, readFileSync } from 'node:fs';
import { join, win32 } from 'node:path';

type Variant = 'baseline' | 'fixed';

const titles = [
    'retains and redacts diagnostics while excluding Dashboard persistence and CLI leases',
    'does not descend into Dashboard persistence when run files disappear',
    'uses case-insensitive exclusions on Windows',
    'collects VS Code and Aspire diagnostics with an exclusively held Dashboard runs lock',
    'collects VS Code and Aspire diagnostics with an exclusively held Dashboard resumes lock',
    'does not suppress an unexpected locked diagnostic',
    'does not suppress an unexpected diagnostic disappearing during copy',
    'tolerates diagnostics sources that were never created',
].map(title => `E2E storage diagnostics ${title}`);

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

function assertionDiff(error: Record<string, unknown> | undefined, extraFiles: Record<string, string>): void {
    assert.ok(error);
    assert.equal(error.name, 'AssertionError');
    assert.equal(error.code, 'ERR_ASSERTION');
    assert.equal(error.operator, 'deepStrictEqual');
    // Mocha's JSON reporter serializes actual/expected with its own stringify(), not JSON:
    //   {\n  "aspire-home/dashboard/runs/example.lock": ""\n}
    // There are no commas. Require exactly the known extra files, preserving every other line.
    // https://mochajs.org/next/reporters/json/
    const extras = Object.entries(extraFiles).map(([name, value]) => `  ${JSON.stringify(name)}: ${JSON.stringify(value)}`);
    assert.deepEqual(
        text(error.actual).split(/\r?\n/).sort(),
        [...text(error.expected).split(/\r?\n/), ...extras].sort(),
        'Baseline assertion changed for reasons other than the known unwanted files',
    );
}

function validate(reportValue: unknown, mode: Variant, exitCode: number) {
    const report = object(reportValue);
    const stats = object(report.stats);
    const failureCount = mode === 'baseline' ? 5 : 0;
    assert.equal(exitCode, failureCount, 'Unexpected Mocha exit code');
    assert.equal(stats.suites, 1);
    assert.equal(stats.tests, 8, 'All eight tests must execute');
    assert.equal(stats.passes, 8 - failureCount);
    assert.equal(stats.pending, 0, 'Skipped tests are not evidence');
    assert.equal(stats.failures, failureCount);
    assert.ok(typeof stats.duration === 'number' && stats.duration > 0);
    assert.deepEqual(report.pending, []);

    const all = testSet(report.tests, titles);
    const failures = testSet(report.failures, mode === 'baseline' ? titles.slice(0, 5) : []);
    const passes = testSet(report.passes, mode === 'baseline' ? titles.slice(5) : titles);
    for (const [title, error] of all) {
        assert.deepEqual(error, failures.get(title) ?? passes.get(title), 'Inconsistent Mocha result arrays');
    }
    for (const error of passes.values()) {
        assert.deepEqual(error, {}, 'Passing tests must not carry errors');
    }

    if (mode === 'baseline') {
        assertionDiff(failures.get(titles[0]), {
            'aspire-home/dashboard/resumes/app.lock': '',
            'aspire-home/dashboard/resumes/app/dashboard.db-wal': 'live WAL',
            'aspire-home/dashboard/runs/20260914T045039914Z.lock': '',
            'aspire-home/dashboard/runs/20260914T045039914Z/dashboard.db': 'run database',
            'aspire-home/dashboard/runs/20260914T045039914Z/run.json': '{"runId":"20260914T045039914Z"}',
        });
        const disappearing = failures.get(titles[1]);
        assert.ok(disappearing);
        assert.equal(disappearing.name, 'AssertionError');
        assert.equal(disappearing.code, 'ERR_ASSERTION');
        assert.equal(disappearing.operator, 'strictEqual');
        assert.equal(disappearing.actual, 'true');
        assert.equal(disappearing.expected, 'false');
        assertionDiff(failures.get(titles[2]), {
            'aspire-home/DASHBOARD/runs/app.lock': '',
            'aspire-home/cache/apphost-info/.LEASES/app.json': 'lease state',
            'aspire-home/cache/apphost-info/app.LEASE': '',
        });

        for (const [index, persistenceMode] of ['runs', 'resumes'].entries()) {
            const error = failures.get(titles[3 + index]);
            assert.ok(error);
            assert.equal(error.code, 'EBUSY');
            assert.equal(error.syscall, 'copyfile');
            const source = win32.normalize(text(error.path));
            const suffix = `\\aspire-home\\dashboard\\${persistenceMode}\\20260914T045039914Z.lock`;
            assert.ok(win32.isAbsolute(source) && source.endsWith(suffix), 'Wrong native lock source');
            const root = source.slice(0, -suffix.length);
            assert.match(win32.basename(root), /^aspire-e2e-storage-diagnostics-[^\\]+$/);
            assert.equal(win32.normalize(text(error.dest)), `${root}\\diagnostics${suffix}`, 'Wrong copy destination');
            // The workflow pins the unchanged suite. Its control copy must establish EBUSY before
            // collectDiagnostics() is called; control/setup/cleanup assertion failures cannot pass here.
            assert.match(text(error.stack), /\bat collectDiagnostics \(/);
            assert.match(text(error.stack), /\bat withExclusiveFileLock \(/);
        }
    }

    return { tests: 8, passes: 8 - failureCount, pending: 0, failures: failureCount };
}

function readJson(file: string): unknown {
    return JSON.parse(readFileSync(file, 'utf8'));
}

function parseExitCode(value: string): number {
    assert.match(value.trim(), /^-?\d+$/, 'Missing or invalid process exit code');
    return Number(value.trim());
}

function summarize(root: string): void {
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
    emit('Three isolated runners per variant, three iterations each: 9 baseline + 9 fixed suite executions, 144 test executions expected, no skips or retries.');
    emit('A baseline row is accepted only for 3 passes / 5 exact regressions, including both native runs/resumes EBUSY copyfile errors after the OS-lock controls.');
    emit('');
    emit('| Variant | Runner | Iteration | OS version | Node | Tests | Pass | Fail | Pending | Contract |');
    emit('|---|---|---|---|---|---|---|---|---|---|');

    let accepted = 0;
    const suiteBlobs = new Set<string>();
    const collectorBlobs = { baseline: new Set<string>(), fixed: new Set<string>() };
    for (const mode of ['baseline', 'fixed']) {
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
                    assert.match(text(context.suiteBlob), /^[0-9a-f]{40}$/);
                    assert.match(text(context.collectorBlob), /^[0-9a-f]{40}$/);
                    suiteBlobs.add(text(context.suiteBlob));
                    collectorBlobs[variant(mode)].add(text(context.collectorBlob));

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
    assert.equal(suiteBlobs.size, 1, 'Runners did not use the same regression suite');
    assert.equal(collectorBlobs.baseline.size, 1);
    assert.equal(collectorBlobs.fixed.size, 1);
    assert.notDeepEqual([...collectorBlobs.baseline], [...collectorBlobs.fixed], 'Before/after collectors must differ');
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
