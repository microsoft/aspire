'use strict';

function createAspireResourcesScenario(expectedResources) {
  return {
    description: 'List running Aspire resources',
    timeoutMs: 180_000,
    callback: async ({ runAspireCommand, timeoutMs }) => {
      const run = await runAspireCommand(['resources']);

      for (const resourceName of expectedResources) {
        await run.waitForText(resourceName, timeoutMs, `resource '${resourceName}'`);
      }
    }
  };
}

module.exports = {
  createAspireResourcesScenario
};
