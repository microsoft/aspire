'use strict';

const shardsRequiringExecutedTests = new Set(['resource-debugger']);

/**
 * Fails an opted-in shard that reported success without executing any tests.
 *
 * `results` is the parsed `mocha.json` written by e2e-mocha-reporter.cjs:
 *   { "stats": { "tests": 3, "passes": 1, "pending": 2, "failures": 0 } }
 */
function assertShardExecutedTests({ shardName, results }) {
  if (!shardsRequiringExecutedTests.has(shardName)) {
    return;
  }

  if (!results || typeof results !== 'object' || !results.stats) {
    throw new Error(`Aspire extension E2E shard '${shardName}' did not produce Mocha results. The shard cannot be considered passing without them.`);
  }

  const tests = Number(results.stats.tests ?? 0);
  const pending = Number(results.stats.pending ?? 0);
  if (tests === 0) {
    throw new Error(`Aspire extension E2E shard '${shardName}' executed 0 tests. Check ASPIRE_EXTENSION_E2E_SPEC and the compiled shard spec.`);
  }

  if (pending >= tests) {
    throw new Error(`Aspire extension E2E shard '${shardName}' executed no tests because all ${tests} tests were pending.`);
  }
}

module.exports = {
  assertShardExecutedTests,
};
