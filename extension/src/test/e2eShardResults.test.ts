import * as assert from 'assert';
import * as path from 'path';

const { assertShardExecutedTests, shardsWithoutSilentSkips } = require(path.join(__dirname, '..', '..', 'scripts', 'e2e-shard-results.js')) as {
    assertShardExecutedTests: (options: { shardName: string; results: unknown }) => void;
    shardsWithoutSilentSkips: readonly string[];
};

suite('E2E shard results', () => {
    function results(stats: { tests: number; passes: number; pending: number; failures: number }, pending: Array<{ fullTitle: string }> = []) {
        return { stats, pending };
    }

    test('accepts a shard that executed and passed tests', () => {
        assertShardExecutedTests({ shardName: 'apphost-tree', results: results({ tests: 3, passes: 3, pending: 0, failures: 0 }) });
    });

    test('rejects a shard whose Mocha results are missing', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'apphost-tree', results: undefined }),
            /did not produce Mocha results/);
    });

    test('rejects a shard that executed no tests at all', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'apphost-tree', results: results({ tests: 0, passes: 0, pending: 0, failures: 0 }) }),
            /executed 0 tests/);
    });

    test('allows platform-gated pending tests in shards that legitimately skip', () => {
        assertShardExecutedTests({
            shardName: 'command-palette',
            results: results({ tests: 4, passes: 3, pending: 1, failures: 0 }, [{ fullTitle: 'routes terminal commands through a configured Windows cmd wrapper path with spaces' }]),
        });
    });

    test('rejects pending tests in a shard that must never silently skip', () => {
        assert.throws(
            () => assertShardExecutedTests({
                shardName: 'resource-debugger',
                results: results({ tests: 3, passes: 2, pending: 1, failures: 0 }, [{ fullTitle: 'Aspire resource debugger E2E tears down the Node process tree' }]),
            }),
            /skipped 1 test\(s\).*tears down the Node process tree/s);
    });

    test('rejects a no-silent-skip shard that passed nothing', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'resource-debugger', results: results({ tests: 2, passes: 0, pending: 0, failures: 2 }) }),
            /did not pass any tests/);
    });

    test('lists the resource debugger shard as a no-silent-skip shard', () => {
        assert.deepStrictEqual([...shardsWithoutSilentSkips], ['resource-debugger']);
    });
});
