import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { load } from 'js-yaml';

/**
 * The E2E suite runs one spec per workflow matrix row. This unit test is the signal for a spec that
 * has no row, because an E2E shard cannot report that a different shard was never scheduled.
 */
suite('E2E shard matrix', () => {
    const extensionRoot = path.resolve(__dirname, '..', '..');
    const workflowPath = path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml');
    const specDirectory = path.join(extensionRoot, 'src', 'test-e2e');
    const disabledIssuePattern = /^https:\/\/github\.com\/microsoft\/aspire\/issues\/\d+$/;

    // Hand-maintained on purpose: disabling an E2E shard must be an explicit, reviewable code change
    // rather than something a workflow edit can do silently. Deriving this from the workflow would
    // make the assertion vacuous. Keys are `name|shardName|spec` and values are the tracking issue.
    // Add an entry when a row gains `disabledIssue`, and delete it when the shard is re-enabled.
    const expectedDisabledRows = new Map<string, string>();

    function canonicalSpecPaths(specFileNames: readonly string[]): string[] {
        return specFileNames
            .filter(file => file.endsWith('.e2e.test.ts'))
            .map(file => `out/test-e2e/test-e2e/${file.replace(/\.ts$/, '.js')}`)
            .sort();
    }

    function readSpecFileNamesRecursively(directory: string, relativeDirectory = ''): string[] {
        return fs.readdirSync(directory, { withFileTypes: true })
            .flatMap(entry => {
                const relativePath = relativeDirectory ? `${relativeDirectory}/${entry.name}` : entry.name;
                const fullPath = path.join(directory, entry.name);

                return entry.isDirectory()
                    ? readSpecFileNamesRecursively(fullPath, relativePath)
                    : [relativePath];
            })
            .sort();
    }

    function matrixRows(workflow: string): MatrixRow[] {
        const document = asRecord(load(workflow), 'Expected extension-e2e-tests.yml to contain a YAML mapping.');
        const jobs = asRecord(document.jobs, 'Expected extension-e2e-tests.yml to contain jobs.');
        const extensionE2e = asRecord(jobs.extension_e2e, 'Expected extension-e2e-tests.yml to contain the extension_e2e job.');
        const strategy = asRecord(extensionE2e.strategy, 'Expected the extension_e2e job to contain a strategy.');
        const matrix = asRecord(strategy.matrix, 'Expected the extension_e2e strategy to contain a matrix.');

        assert.ok(Array.isArray(matrix.include), 'Expected the extension_e2e matrix to contain an include list.');

        return matrix.include.map((value, index) => {
            const row = asRecord(value, `Expected extension_e2e matrix row ${index + 1} to be a mapping.`);

            return {
                name: optionalString(row, 'name', index),
                shardName: optionalString(row, 'shardName', index),
                spec: optionalString(row, 'spec', index),
                allowFailure: optionalBoolean(row, 'allowFailure', index),
                disabledIssue: optionalString(row, 'disabledIssue', index),
            };
        });
    }

    function asRecord(value: unknown, message: string): Record<string, unknown> {
        assert.ok(typeof value === 'object' && value !== null && !Array.isArray(value), message);
        return value as Record<string, unknown>;
    }

    function optionalString(row: Record<string, unknown>, key: string, index: number): string | undefined {
        const value = row[key];
        if (value === undefined) {
            return undefined;
        }

        // js-yaml parses a present-but-empty scalar such as `disabledIssue:` as null.
        // Keep that distinct from an absent field so downstream validation can reject
        // empty workflow values instead of silently treating them as omitted.
        if (value === null) {
            return '';
        }

        assert.strictEqual(typeof value, 'string', `Expected extension_e2e matrix row ${index + 1} field '${key}' to be a string.`);
        return value as string;
    }

    function optionalBoolean(row: Record<string, unknown>, key: string, index: number): boolean | undefined {
        const value = row[key];
        if (value === undefined) {
            return undefined;
        }

        assert.strictEqual(typeof value, 'boolean', `Expected extension_e2e matrix row ${index + 1} field '${key}' to be a boolean.`);
        return value as boolean;
    }

    function matrixSpecPaths(workflow: string): string[] {
        return [...new Set(matrixRows(workflow).map(row => {
            assert.ok(row.spec, 'E2E matrix rows must include a non-empty spec.');
            return row.spec;
        }))].sort();
    }

    function disabledRowKey(row: MatrixRow): string {
        assert.ok(row.name, 'Disabled E2E matrix rows must include name.');
        assert.ok(row.shardName, 'Disabled E2E matrix rows must include shardName.');
        assert.ok(row.spec, 'Disabled E2E matrix rows must include spec.');

        return `${row.name}|${row.shardName}|${row.spec}`;
    }

    function assertDisabledRowsAreTracked(workflow: string, expectedRowsByKey: ReadonlyMap<string, string>): void {
        const actualRows = matrixRows(workflow)
            .filter(row => row.disabledIssue !== undefined)
            .map(row => {
                assert.ok(
                    disabledIssuePattern.test(row.disabledIssue ?? ''),
                    `Disabled E2E matrix row '${disabledRowKey(row)}' must use a microsoft/aspire issue URL.`);

                return [disabledRowKey(row), row.disabledIssue] as [string, string];
            })
            .sort(([left], [right]) => left.localeCompare(right));
        const expectedRows = [...expectedRowsByKey.entries()]
            .sort(([left], [right]) => left.localeCompare(right));

        assert.deepStrictEqual(
            actualRows,
            expectedRows,
            'Disabled E2E matrix rows must exactly match the explicit allowlist in e2eShardMatrix.test.ts.');
    }

    function assertAllRowsAllowFailure(workflow: string): void {
        for (const row of matrixRows(workflow)) {
            assert.strictEqual(
                row.allowFailure,
                true,
                `E2E matrix row '${row.name}|${row.shardName}|${row.spec}' must set allowFailure: true.`);
        }
    }

    function assertMatrixMatchesSpecs(workflow: string, specFileNames: readonly string[]): void {
        assert.deepStrictEqual(
            matrixSpecPaths(workflow),
            canonicalSpecPaths(specFileNames),
            'The E2E workflow matrix must exactly match the .e2e.test.ts files under src/test-e2e.');
    }

    function workflowWithRows(...rows: readonly string[]): string {
        return [
            'jobs:',
            '  extension_e2e:',
            '    strategy:',
            '      matrix:',
            '        include:',
            ...rows.map(row => row.split('\n').map(line => `          ${line}`).join('\n')),
            '',
        ].join('\n');
    }

    test('runs exactly the set of E2E specs in the CI matrix', () => {
        const specFileNames = readSpecFileNamesRecursively(specDirectory);
        const workflow = fs.readFileSync(workflowPath, 'utf8');

        assert.ok(canonicalSpecPaths(specFileNames).length > 0, `Expected E2E spec files under ${specDirectory}.`);
        assert.ok(matrixSpecPaths(workflow).length > 0, 'Expected spec entries in the E2E workflow matrix.');
        assertMatrixMatchesSpecs(workflow, specFileNames);
        assertAllRowsAllowFailure(workflow);
        assertDisabledRowsAreTracked(workflow, expectedDisabledRows);
    });

    test('rejects a spec that has no matrix row', () => {
        const workflow = workflowWithRows(
            '- name: Linux\n  shardName: edge-cases\n  spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts', 'appHostTree.e2e.test.ts']),
            assert.AssertionError);
    });

    test('rejects a matrix row that does not point at an E2E spec', () => {
        const workflow = workflowWithRows(
            '- name: Linux\n  shardName: edge-cases\n  spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js',
            '- name: Linux\n  shardName: helper\n  spec: out/test-e2e/test-e2e/helpers/fixtures.js');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']),
            assert.AssertionError);
    });

    test('discovers nested E2E specs recursively', () => {
        const fixtureRoot = fs.mkdtempSync(path.join(extensionRoot, 'out', 'e2e-shard-matrix-'));
        try {
            fs.mkdirSync(path.join(fixtureRoot, 'nested'), { recursive: true });
            fs.writeFileSync(path.join(fixtureRoot, 'edgeCases.e2e.test.ts'), '');
            fs.writeFileSync(path.join(fixtureRoot, 'nested', 'futureCoverage.e2e.test.ts'), '');
            const workflow = workflowWithRows(
                '- name: Linux\n  shardName: edge-cases\n  spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js');

            assert.throws(
                () => assertMatrixMatchesSpecs(workflow, readSpecFileNamesRecursively(fixtureRoot)),
                assert.AssertionError);
        }
        finally {
            fs.rmSync(fixtureRoot, { recursive: true, force: true });
        }
    });

    test('accepts one spec on multiple platform rows', () => {
        const spec = 'out/test-e2e/test-e2e/edgeCases.e2e.test.js';
        const workflow = workflowWithRows(
            `- name: Linux\n  shardName: edge-cases\n  spec: ${spec}`,
            `- { name: Windows, shardName: edge-cases, spec: ${spec} }`);

        assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']);
    });

    test('does not treat nested or unrelated spec fields as matrix.spec', () => {
        const workflow = [
            'unrelated:',
            '  spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js',
            workflowWithRows(
                '- name: Linux\n  shardName: edge-cases\n  options:\n    spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js'),
        ].join('\n');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']),
            /E2E matrix rows must include a non-empty spec/);
    });

    test('rejects a matrix row with an empty spec', () => {
        const workflow = workflowWithRows('- name: Linux\n  shardName: edge-cases\n  spec:');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']),
            /E2E matrix rows must include a non-empty spec/);
    });

    test('requires disabled rows to be explicitly tracked', () => {
        const spec = 'out/test-e2e/test-e2e/azureFunctions.e2e.test.js';
        const issue = 'https://github.com/microsoft/aspire/issues/19151';
        const workflow = workflowWithRows(
            `- name: Linux\n  shardName: azure-functions\n  spec: ${spec}\n  disabledIssue: ${issue}`);

        assert.throws(
            () => assertDisabledRowsAreTracked(workflow, new Map()),
            assert.AssertionError);
        assertDisabledRowsAreTracked(workflow, new Map([[`Linux|azure-functions|${spec}`, issue]]));
    });

    test('tracks disabled platform rows separately and validates issue URLs', () => {
        const spec = 'out/test-e2e/test-e2e/azureFunctions.e2e.test.js';
        const issue = 'https://github.com/microsoft/aspire/issues/19151';
        const workflow = workflowWithRows(
            `- name: Linux\n  shardName: azure-functions\n  spec: ${spec}\n  disabledIssue: ${issue}`,
            `- name: Windows\n  shardName: azure-functions\n  spec: ${spec}\n  disabledIssue: ${issue}`);

        assertDisabledRowsAreTracked(workflow, new Map([
            [`Linux|azure-functions|${spec}`, issue],
            [`Windows|azure-functions|${spec}`, issue],
        ]));

        const malformed = workflow.replaceAll(issue, 'not-an-issue-url');
        assert.throws(
            () => assertDisabledRowsAreTracked(malformed, new Map()),
            /must use a microsoft\/aspire issue URL/);
    });

    test('rejects a disabled row when disabledIssue is present but empty', () => {
        const spec = 'out/test-e2e/test-e2e/azureFunctions.e2e.test.js';
        const workflow = workflowWithRows(
            `- name: Linux\n  shardName: azure-functions\n  spec: ${spec}\n  disabledIssue:`);

        assert.throws(
            () => assertDisabledRowsAreTracked(workflow, new Map()),
            /must use a microsoft\/aspire issue URL/);
    });
});

interface MatrixRow {
    name?: string;
    shardName?: string;
    spec?: string;
    allowFailure?: boolean;
    disabledIssue?: string;
}
