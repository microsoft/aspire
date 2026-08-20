'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireAddInteractive({
  aspireCommand,
  cwd,
  diagnosticsDir,
  integrationFilter,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd,
    diagnosticsDir,
    fileName: 'aspire-add.log',
    timeoutMs,
    body: async runAspireCommand => {
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
  });
}

module.exports = {
  runAspireAddInteractive
};
