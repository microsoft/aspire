'use strict';

function createAspireAddScenario(integrationFilter) {
  return {
    description: `Add the ${integrationFilter} integration`,
    timeoutMs: 180_000,
    callback: async ({ runAspireCommand, timeoutMs }) => {
      const run = await runAspireCommand(['add']);

      await run.waitForText('Select an integration to add:', timeoutMs, 'integration selection prompt');
      await run.type(integrationFilter);
      await run.enter();

      const addState = await run.waitForAnyText(
        ['Select a version of', 'was added successfully.'],
        timeoutMs,
        'package version selection or add success');

      if (addState === 'Select a version of') {
        await run.enter();
      }

      await run.waitForText('was added successfully.', timeoutMs, 'integration add success');
    }
  };
}

module.exports = {
  createAspireAddScenario
};
