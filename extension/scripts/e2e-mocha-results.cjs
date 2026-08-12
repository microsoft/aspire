'use strict';

function hasCompletedMochaTestFailures(results) {
  if (!Array.isArray(results?.tests) || !Array.isArray(results?.failures) || results.failures.length === 0) {
    return false;
  }

  // The reporter writes this shape only after Mocha emits EVENT_RUN_END:
  //   { tests: [{ fullTitle: "suite test" }], failures: [{ fullTitle: "suite test" }] }
  // Startup crashes and hook failures can appear without a completed matching test. Require every
  // failure to match EVENT_TEST_END output so only actual test failures become advisory.
  const completedTestTitles = new Set(results.tests.map(test => test.fullTitle ?? test.title));
  return results.failures.every(failure => completedTestTitles.has(failure.fullTitle ?? failure.title));
}

module.exports = {
  hasCompletedMochaTestFailures,
};
