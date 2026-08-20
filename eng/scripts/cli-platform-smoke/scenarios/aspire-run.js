'use strict';

function createAspireRunScenario(timeoutMs) {
  return {
    description: 'Run the AppHost interactively',
    timeoutMs,
    callback: async ({ runAspireCommand, timeoutMs: scenarioTimeoutMs }) => {
      const run = await runAspireCommand(['run']);

      await run.waitForText(
        'Press CTRL+C to stop the AppHost and exit.',
        scenarioTimeoutMs,
        'run ready banner');
      await run.ctrlC();
    }
  };
}

module.exports = {
  createAspireRunScenario
};
