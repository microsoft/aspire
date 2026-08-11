import * as assert from 'assert';
import { EventEmitter } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

suite('E2E Mocha reporter', () => {
    test('prints spec progress and writes JSON results', () => {
        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-e2e-reporter-'));
        const outputPath = path.join(tempDir, 'mocha.json');
        const constants = require('mocha/lib/runner').constants;
        const Base = require('mocha/lib/reporters/base');
        const previousConsoleLog = Base.consoleLog;
        const outputLines: string[] = [];
        Base.consoleLog = (...args: unknown[]) => outputLines.push(args.map(value => String(value)).join(' '));

        try {
            const Reporter = require(path.join(__dirname, '..', '..', 'scripts', 'e2e-mocha-reporter.cjs'));
            const runner = new EventEmitter() as EventEmitter & { stats: Record<string, unknown>; total: number };
            runner.stats = {
                suites: 1,
                tests: 1,
                passes: 1,
                pending: 0,
                failures: 0,
                duration: 7,
            };
            runner.total = 1;

            new Reporter(runner, { reporterOption: { output: outputPath } });
            const test = createReporterTest('prints live progress to the console');

            runner.emit(constants.EVENT_RUN_BEGIN);
            runner.emit(constants.EVENT_SUITE_BEGIN, { title: 'Aspire E2E' });
            runner.emit(constants.EVENT_TEST_PASS, test);
            runner.emit(constants.EVENT_TEST_END, test);
            runner.emit(constants.EVENT_SUITE_END);
            runner.emit(constants.EVENT_RUN_END);

            assert.ok(outputLines.some(line => line.includes('prints live progress to the console')));

            const results = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
            assert.strictEqual(results.stats.passes, 1);
            assert.deepStrictEqual(results.passes.map((pass: { fullTitle: string }) => pass.fullTitle), [
                'Aspire E2E prints live progress to the console',
            ]);
        }
        finally {
            Base.consoleLog = previousConsoleLog;
            fs.rmSync(tempDir, { recursive: true, force: true });
        }
    });
});

function createReporterTest(title: string) {
    return {
        title,
        file: 'out/test-e2e/sample.e2e.test.js',
        duration: 5,
        slow: () => 75,
        fullTitle: () => `Aspire E2E ${title}`,
        currentRetry: () => 0,
        titlePath: () => ['Aspire E2E', title],
    };
}
