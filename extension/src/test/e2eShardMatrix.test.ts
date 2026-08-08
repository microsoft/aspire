import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

/**
 * The E2E suite is sharded one spec per matrix row in `extension-e2e-tests.yml`, and the runner is
 * pointed at a single compiled spec through `ASPIRE_EXTENSION_E2E_SPEC`. Nothing enumerates the spec
 * directory at runtime, so a spec that never gets a matrix row is not a failure - it simply never
 * runs, and the workflow stays green while the coverage it was written for is gone.
 *
 * This runs as a unit test rather than as part of the E2E suite on purpose: the failure it detects
 * is "an E2E spec did not run", which the E2E suite by definition cannot report on itself.
 */
suite('E2E shard matrix', () => {
    const extensionRoot = path.resolve(__dirname, '..', '..');
    const workflowPath = path.join(extensionRoot, '..', '.github', 'workflows', 'extension-e2e-tests.yml');
    const specDirectory = path.join(extensionRoot, 'src', 'test-e2e');

    /**
     * Compiled spec paths the matrix is allowed to reference, derived from spec file names.
     *
     * This is the canonical set, and membership in it is the only thing that makes a matrix value
     * correct. Deliberately not a filesystem existence check: `out/test-e2e/test-e2e/helpers/fixtures.js`
     * maps to a TypeScript helper that really is on disk, so an existence check accepts it even though
     * the runner's glob would then match no tests.
     */
    function canonicalSpecPaths(specFileNames: readonly string[]): string[] {
        return specFileNames
            .filter(file => file.endsWith('.e2e.test.ts'))
            .map(file => `out/test-e2e/test-e2e/${file.replace(/\.ts$/, '.js')}`)
            .sort();
    }

    /**
     * Matrix rows carry the compiled spec path:
     *       - name: Linux
     *         shardName: edge-cases
     *         spec: out/test-e2e/test-e2e/edgeCases.e2e.test.js
     * A spec legitimately appears on more than one row (one per platform), so the values are
     * deduplicated before being compared with the canonical set.
     */
    function matrixSpecPaths(workflow: string): string[] {
        return [...new Set([...workflow.matchAll(/^\s*spec:\s*(\S+)\s*$/gm)].map(match => match[1]))].sort();
    }

    function workflowWithSpecs(...specs: readonly string[]): string {
        return [
            'jobs:',
            '  e2e:',
            '    strategy:',
            '      matrix:',
            '        include:',
            ...specs.map(spec => [
                '          - name: Linux',
                '            shardName: shard',
                `            spec: ${spec}`,
            ].join('\n')),
            '',
        ].join('\n');
    }

    /**
     * The comparison every case in this suite runs, including the synthetic ones below.
     *
     * It is a named function rather than an assertion inlined in the real-workflow test so that the
     * negative cases drive the check itself instead of restating it. A negative case written as
     * `assert.notDeepStrictEqual(matrixSpecPaths(...), canonicalSpecPaths(...))` compares two
     * helper outputs and passes no matter what the check does, which is the failure mode those
     * cases exist to rule out: weaken this function to a one-directional containment check and they
     * have to go red.
     */
    function assertMatrixMatchesSpecs(workflow: string, specFileNames: readonly string[]): void {
        // Deliberately full set equality rather than a containment check in either direction. A
        // missing entry means a spec silently never runs; an extra entry that is not a spec (a
        // helper module, or a spec that was renamed or deleted) means the runner's glob matches
        // nothing and the shard reports success while running zero tests. Both are invisible in a
        // green workflow, so the matrix has to equal the spec set exactly.
        assert.deepStrictEqual(
            matrixSpecPaths(workflow),
            canonicalSpecPaths(specFileNames),
            'The spec values in extension-e2e-tests.yml must be exactly the compiled paths of the .e2e.test.ts files under src/test-e2e.');
    }

    test('runs exactly the set of E2E specs in the CI matrix', () => {
        const specFileNames = fs.readdirSync(specDirectory);
        const workflow = fs.readFileSync(workflowPath, 'utf8');

        assert.ok(canonicalSpecPaths(specFileNames).length > 0, `Expected E2E spec files under ${specDirectory}.`);
        assert.ok(matrixSpecPaths(workflow).length > 0, 'Expected spec entries in the E2E workflow matrix.');

        assertMatrixMatchesSpecs(workflow, specFileNames);
    });

    test('rejects a matrix row pointing at a real file that is not an E2E spec', () => {
        // The premise of this case: the row references a file that genuinely exists, so a reverse
        // check written as "the path this maps to exists on disk" accepts it. Assert the premise
        // directly, otherwise renaming the helper would silently turn this into a vacuous test.
        assert.ok(
            fs.existsSync(path.join(specDirectory, 'helpers', 'fixtures.ts')),
            'This case depends on src/test-e2e/helpers/fixtures.ts existing so it models a real non-spec file.');

        const workflow = workflowWithSpecs(
            'out/test-e2e/test-e2e/edgeCases.e2e.test.js',
            'out/test-e2e/test-e2e/helpers/fixtures.js');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']),
            assert.AssertionError,
            'A matrix row pointing at a helper module must be rejected: it produces a shard that runs zero tests.');
    });

    test('rejects a spec that has no matrix row', () => {
        const workflow = workflowWithSpecs('out/test-e2e/test-e2e/edgeCases.e2e.test.js');

        assert.throws(
            () => assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts', 'appHostTree.e2e.test.ts']),
            assert.AssertionError,
            'A spec with no matrix row must be rejected: it never runs and the workflow still reports success.');
    });

    test('accepts one spec listed on several platform rows', () => {
        const workflow = workflowWithSpecs(
            'out/test-e2e/test-e2e/edgeCases.e2e.test.js',
            'out/test-e2e/test-e2e/edgeCases.e2e.test.js');

        // Guards the deduplication: without it every cross-platform shard would look like a
        // duplicate entry and the set equality check would fail on a correct workflow.
        assertMatrixMatchesSpecs(workflow, ['edgeCases.e2e.test.ts']);
    });
});
