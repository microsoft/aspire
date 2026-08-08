'use strict';

/**
 * Shards whose value depends entirely on the assertions actually executing.
 *
 * A test that silently skips (Mocha "pending") still lets the shard report success, which for a
 * debugger proof means CI would go green while proving nothing. Shards listed here therefore fail
 * on any pending test and on a run that passed nothing, instead of only failing on hard errors.
 * Other shards legitimately skip platform-gated tests (for example the Windows-only cmd wrapper
 * test in the command-palette shard), so the pending check stays opt-in.
 */
const shardsWithoutSilentSkips = Object.freeze(['resource-debugger']);

/**
 * Fails a shard that reported success without actually running its tests.
 *
 * `results` is the parsed `mocha.json` written by scripts/e2e-mocha-reporter.cjs:
 *   { "stats": { "tests": 3, "passes": 3, "pending": 0, "failures": 0, "duration": 1234 },
 *     "pending": [ { "fullTitle": "..." } ], ... }
 * The file is absent when Mocha never reached its run-end event, which the ExTester exit code
 * usually also reports — but not always, so treat a missing file as a failure of its own.
 */
function assertShardExecutedTests({ shardName, results }) {
  if (!results || typeof results !== 'object' || !results.stats) {
    throw new Error(`Aspire extension E2E shard '${shardName}' did not produce Mocha results. The shard cannot be considered passing without them.`);
  }

  const stats = results.stats;
  const total = Number(stats.tests ?? 0);
  const passes = Number(stats.passes ?? 0);
  const pending = Number(stats.pending ?? 0);

  if (total === 0) {
    throw new Error(`Aspire extension E2E shard '${shardName}' executed 0 tests. Check ASPIRE_EXTENSION_E2E_SPEC and the compiled shard spec.`);
  }

  if (!shardsWithoutSilentSkips.includes(shardName)) {
    return;
  }

  if (pending > 0) {
    const pendingTitles = Array.isArray(results.pending)
      ? results.pending.map(test => `  - ${test.fullTitle ?? test.title ?? '<unnamed test>'}`).join('\n')
      : '';
    throw new Error(`Aspire extension E2E shard '${shardName}' skipped ${pending} test(s), which is not allowed for this shard because a skipped proof proves nothing:\n${pendingTitles}`);
  }

  if (passes === 0) {
    throw new Error(`Aspire extension E2E shard '${shardName}' did not pass any tests out of ${total}. This shard must prove its scenarios rather than report an empty success.`);
  }
}

module.exports = {
  assertShardExecutedTests,
  shardsWithoutSilentSkips,
};
