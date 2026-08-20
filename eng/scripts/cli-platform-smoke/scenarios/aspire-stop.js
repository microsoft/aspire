'use strict';

function createAspireStopScenario({
  allowNotRunning = false,
  description = 'Stop the running AppHost',
  runAfterCancellation = false
} = {}) {
  return {
    description,
    runAfterCancellation,
    timeoutMs: 120_000,
    callback: async ({ runAspireCommand, timeoutMs }) => {
      const run = await runAspireCommand(['stop']);
      const expectedResults = ['Running instance stopped successfully.'];

      if (allowNotRunning) {
        expectedResults.push('No running AppHost found.');
      }

      await run.waitForAnyText(expectedResults, timeoutMs, 'AppHost stop result');
    }
  };
}

module.exports = {
  createAspireStopScenario
};
