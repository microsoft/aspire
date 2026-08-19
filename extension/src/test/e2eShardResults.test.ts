import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

interface ShardResultOptions {
    shardName: string;
    results: unknown;
}

const shardResultsPath = path.join(__dirname, '..', '..', 'scripts', 'e2e-shard-results.js');

function assertShardExecutedTests(options: ShardResultOptions): void {
    assert.ok(fs.existsSync(shardResultsPath), 'The E2E runner must define a shard result guard.');
    const shardResults = require(shardResultsPath) as {
        assertShardExecutedTests: (options: ShardResultOptions) => void;
    };
    assert.strictEqual(typeof shardResults.assertShardExecutedTests, 'function');
    shardResults.assertShardExecutedTests(options);
}

function results(tests: number, passes: number, pending: number) {
    return {
        stats: {
            tests,
            passes,
            pending,
            failures: 0,
        },
    };
}

suite('E2E shard results', () => {
    test('rejects missing results for the resource debugger proof', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'resource-debugger', results: undefined }),
            /did not produce Mocha results/);
    });

    test('rejects zero tests for the resource debugger proof', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'resource-debugger', results: results(0, 0, 0) }),
            /executed 0 tests/);
    });

    test('rejects an all-pending resource debugger proof', () => {
        assert.throws(
            () => assertShardExecutedTests({ shardName: 'resource-debugger', results: results(3, 0, 3) }),
            /all 3 tests were pending/);
    });

    test('accepts a resource debugger proof with an executed test', () => {
        assertShardExecutedTests({ shardName: 'resource-debugger', results: results(3, 1, 2) });
    });

    test('does not guard shards that have not opted into executed-test proof', () => {
        assertShardExecutedTests({ shardName: 'apphost-tree', results: undefined });
        assertShardExecutedTests({ shardName: 'apphost-tree', results: results(0, 0, 0) });
        assertShardExecutedTests({ shardName: 'apphost-tree', results: results(3, 0, 3) });
    });
});
