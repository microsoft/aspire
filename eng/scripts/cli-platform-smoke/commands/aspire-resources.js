'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireResources({
  aspireCommand,
  cwd,
  diagnosticsDir,
  expectedResources,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-resources.log',
    command: buildAspireCommand(aspireCommand, ['resources']),
    timeoutMs,
    interact: async run => {
      for (const resourceName of expectedResources) {
        await run.waitForText(resourceName, timeoutMs, `resource '${resourceName}'`);
      }
    }
  });
}

module.exports = {
  runAspireResources
};
