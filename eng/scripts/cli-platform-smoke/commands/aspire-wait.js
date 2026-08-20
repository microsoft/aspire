'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireWait({
  aspireCommand,
  cwd,
  diagnosticsDir,
  resourceName,
  resourceReadyTimeoutSeconds,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: `aspire-wait-${sanitizeFileName(resourceName)}.log`,
    command: buildAspireCommand(
      aspireCommand,
      ['wait', resourceName, '--status', 'up', '--timeout', String(resourceReadyTimeoutSeconds)]),
    timeoutMs,
    interact: async run => {
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
