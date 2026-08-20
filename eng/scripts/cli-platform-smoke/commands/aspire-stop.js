'use strict';

const fs = require('fs');

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function runAspireStop({
  aspireCommand,
  cwd,
  diagnosticsDir,
  fileName = 'aspire-stop.log',
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName,
    command: buildAspireCommand(aspireCommand, ['stop']),
    timeoutMs,
    interact: async run => {
      await run.waitForAnyText(
        ['Running instance stopped successfully.', 'No running AppHost found.'],
        timeoutMs,
        'AppHost stop result');
    }
  });
}

async function cleanupProject(projectRoot, diagnosticsDir, aspireCommand) {
  if (!fs.existsSync(projectRoot)) {
    return;
  }

  try {
    await runAspireStop({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-stop-cleanup.log',
      timeoutMs: 120_000
    });
  } catch {
    // Best-effort cleanup so a failed proof does not hide the original validation error.
  }
}

module.exports = {
  cleanupProject,
  runAspireStop
};
