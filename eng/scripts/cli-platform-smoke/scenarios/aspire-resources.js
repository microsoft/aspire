'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');
const { createStarterProject } = require('../scriptlets/create-starter-project');
const { startAppHost } = require('../scriptlets/start-apphost');
const { stopAppHost } = require('../scriptlets/stop-apphost');

async function runAspireResources({
  aspireCommand,
  channel,
  diagnosticsDir,
  expectedResources,
  maxStartupSeconds,
  projectRoot,
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
      fileName: 'aspire-resources.log',
      command: buildAspireCommand(aspireCommand, ['resources']),
      timeoutMs,
      interact: async run => {
        for (const resourceName of expectedResources) {
          await run.waitForText(resourceName, timeoutMs, `resource '${resourceName}'`);
        }
      }
    });
  } finally {
    await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
  }
}

module.exports = {
  id: 'aspire-resources',
  templateIds: ['aspire-starter'],
  async run(context) {
    await runAspireResources({
      ...context,
      expectedResources: context.template.expectedResources,
      timeoutMs: 180_000
    });
  },
  runAspireResources
};
