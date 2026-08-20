'use strict';

function createAspireWaitScenario(resourceName, resourceReadyTimeoutSeconds) {
  return {
    description: `Wait for ${resourceName} to be running`,
    timeoutMs: resourceReadyTimeoutSeconds * 1000 + 120_000,
    callback: async ({ runAspireCommand, timeoutMs }) => {
      const run = await runAspireCommand(
        ['wait', resourceName, '--status', 'up', '--timeout', String(resourceReadyTimeoutSeconds)]);

      await run.waitForText('is up (running).', timeoutMs, `resource '${resourceName}' running`);
    }
  };
}

module.exports = {
  createAspireWaitScenario
};
