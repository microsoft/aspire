'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');
const { createStarterProject } = require('../scriptlets/create-starter-project');
const { startAppHost } = require('../scriptlets/start-apphost');
const { stopAppHost } = require('../scriptlets/stop-apphost');

async function runAspireWait({
  aspireCommand,
  channel,
  diagnosticsDir,
  maxStartupSeconds,
  projectRoot,
  resourceName,
  resourceReadyTimeoutSeconds,
  scenarioRoot,
  template,
  timeoutMs
}) {
  await createStarterProject({
    aspireCommand,
    channel,
    diagnosticsDir,
    projectRoot,
    scenarioRoot,
    template
  });
  try {
    await startAppHost({ aspireCommand, diagnosticsDir, maxStartupSeconds, projectRoot });

    await runInteractiveCommand({
      cwd: projectRoot,
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
  } finally {
    await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
  }
}

function sanitizeFileName(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '_');
}

module.exports = {
  id: 'aspire-wait',
  templateIds: ['aspire-ts-starter'],
  async run(context) {
    await runAspireWait({
      ...context,
      resourceName: context.template.expectedResources[0],
      timeoutMs: context.resourceReadyTimeoutSeconds * 1000 + 120_000
    });
  },
  runAspireWait
};
