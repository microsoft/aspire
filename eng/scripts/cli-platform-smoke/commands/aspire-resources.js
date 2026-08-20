'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireResources({
  aspireCommand,
  diagnosticsDir,
  expectedResources,
  projectRoot,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd: projectRoot,
    diagnosticsDir,
    fileName: 'aspire-resources.log',
    timeoutMs,
    body: async runAspireCommand => {
      const run = await runAspireCommand(['resources']);

      for (const resourceName of expectedResources) {
        await run.waitForText(resourceName, timeoutMs, `resource '${resourceName}'`);
      }
    }
  });
}

module.exports = {
  runAspireResources
};
