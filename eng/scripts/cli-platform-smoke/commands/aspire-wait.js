'use strict';

const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireWait({
  aspireCommand,
  diagnosticsDir,
  projectRoot,
  resourceName,
  resourceReadyTimeoutSeconds,
  timeoutMs
}) {
  await runInteractiveCommand({
    aspireCommand,
    cwd: projectRoot,
    diagnosticsDir,
    fileName: `aspire-wait-${sanitizeFileName(resourceName)}.log`,
    timeoutMs,
    body: async runAspireCommand => {
      const run = await runAspireCommand(
        ['wait', resourceName, '--status', 'up', '--timeout', String(resourceReadyTimeoutSeconds)]);

      await run.waitForText('is up (running).', timeoutMs, `resource '${resourceName}' running`);
    }
  });
}

function sanitizeFileName(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '_');
}

module.exports = {
  runAspireWait
};
