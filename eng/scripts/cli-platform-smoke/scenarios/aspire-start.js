'use strict';

function createAspireStartScenario(timeoutMs) {
  return {
    description: 'Start the AppHost in the background',
    timeoutMs,
    callback: async ({ runAspireCommand, timeoutMs: scenarioTimeoutMs }) => {
      const run = await runAspireCommand(['start']);

      await run.waitForText(
        'AppHost started successfully.',
        scenarioTimeoutMs,
        'AppHost start success');
    }
  };
}

module.exports = {
  createAspireStartScenario
};
