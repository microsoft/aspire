'use strict';

const BLOCKING_HARNESS_ERROR_NAMES = new Set([
  'InvalidSessionIdError',
  'NoSuchSessionError',
  'NoSuchWindowError',
  'SessionNotCreatedError',
]);

const BLOCKING_WEBDRIVER_ERROR_MESSAGES = [
  'session deleted because of page crash',
  'disconnected: not connected to devtools',
  'chrome not reachable',
];

function isBlockingHarnessFailure(error) {
  if (BLOCKING_HARNESS_ERROR_NAMES.has(error?.name)) {
    return true;
  }

  if (error?.name !== 'WebDriverError' || typeof error.message !== 'string') {
    return false;
  }

  // Selenium uses the generic WebDriverError name for both transient browser failures and browser
  // lifecycle failures. The latter are serialized with messages such as:
  //   unknown error: session deleted because of page crash
  //   unknown error: disconnected: not connected to DevTools
  //   unknown error: chrome not reachable
  const message = error.message.toLowerCase();
  return BLOCKING_WEBDRIVER_ERROR_MESSAGES.some(fragment => message.includes(fragment));
}

function hasCompletedMochaTestFailures(results) {
  if (!Array.isArray(results?.tests) || !Array.isArray(results?.failures) || results.failures.length === 0) {
    return false;
  }

  // The reporter writes this shape only after Mocha emits EVENT_RUN_END:
  //   { tests: [{ fullTitle: "suite test" }],
  //     failures: [{ fullTitle: "suite test", err: { name: "NoSuchSessionError" } }] }
  // Startup crashes and hook failures can appear without a completed matching test. Require every
  // failure to match EVENT_TEST_END output, and keep browser-session lifecycle failures blocking
  // even though Mocha records them against a completed test.
  const completedTestTitles = new Set(results.tests.map(test => test.fullTitle ?? test.title));
  return results.failures.every(failure =>
    completedTestTitles.has(failure.fullTitle ?? failure.title)
    && !isBlockingHarnessFailure(failure.err));
}

module.exports = {
  hasCompletedMochaTestFailures,
};
