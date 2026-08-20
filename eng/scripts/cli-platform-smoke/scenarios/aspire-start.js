'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');
const { createStarterProject } = require('../scriptlets/create-starter-project');
const { stopAppHost } = require('../scriptlets/stop-apphost');

async function runAspireStart({
  aspireCommand,
  channel,
  diagnosticsDir,
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
    await runInteractiveCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-start.log',
      command: buildAspireCommand(aspireCommand, ['start']),
      timeoutMs,
      interact: async run => {
        await run.waitForText('AppHost started successfully.', timeoutMs, 'AppHost start success');
      }
    });
  } finally {
    await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
  }
}

module.exports = {
  id: 'aspire-start',
  templateIds: ['aspire-ts-starter'],
  async run(context) {
    await runAspireStart({
      ...context,
      timeoutMs: context.maxStartupSeconds * 1000 + 180_000
    });
  },
  runAspireStart
};
