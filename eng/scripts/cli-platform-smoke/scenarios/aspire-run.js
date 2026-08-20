'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');
const { createStarterProject } = require('../scriptlets/create-starter-project');
const { stopAppHost } = require('../scriptlets/stop-apphost');

async function runAspireRunInteractive({
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
      fileName: 'aspire-run.log',
      command: buildAspireCommand(aspireCommand, ['run']),
      timeoutMs,
      interact: async run => {
        await run.waitForText('Press CTRL+C to stop the AppHost and exit.', timeoutMs, 'run ready banner');
        await run.ctrlC();
      }
    });
  } finally {
    await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
  }
}

module.exports = {
  id: 'aspire-run',
  templateIds: ['aspire-starter'],
  async run(context) {
    await runAspireRunInteractive({
      ...context,
      timeoutMs: Math.max(
        context.maxStartupSeconds,
        context.resourceReadyTimeoutSeconds) * 1000 + 180_000
    });
  },
  runAspireRunInteractive
};
