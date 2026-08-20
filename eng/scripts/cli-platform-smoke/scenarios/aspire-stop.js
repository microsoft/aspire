'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');
const { createStarterProject } = require('../scriptlets/create-starter-project');
const { startAppHost } = require('../scriptlets/start-apphost');
const { stopAppHost } = require('../scriptlets/stop-apphost');

async function runAspireStop({
  aspireCommand,
  channel,
  diagnosticsDir,
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
      fileName: 'aspire-stop.log',
      command: buildAspireCommand(aspireCommand, ['stop']),
      timeoutMs,
      interact: async run => {
        await run.waitForAnyText(
          ['Running instance stopped successfully.', 'No running AppHost found.'],
          timeoutMs,
          'AppHost stop result');
      }
    });
  } finally {
    await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
  }
}

module.exports = {
  id: 'aspire-stop',
  templateIds: ['aspire-starter'],
  async run(context) {
    await runAspireStop({
      ...context,
      timeoutMs: 120_000
    });
  },
  runAspireStop
};
