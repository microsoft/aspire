'use strict';

const { buildAspireCommand } = require('../lib/aspire-command');
const { runInteractiveCommand } = require('../lib/run-interactive-command');

async function createStarterProject({
  aspireCommand,
  channel,
  diagnosticsDir,
  projectRoot,
  scenarioRoot,
  template
}) {
  await runInteractiveCommand({
    cwd: scenarioRoot,
    diagnosticsDir,
    fileName: 'setup-aspire-new.log',
    command: buildAspireCommand(
      aspireCommand,
      [
        'new',
        template.templateId,
        '--name', template.projectName,
        '--output', projectRoot,
        '--channel', channel,
        '--non-interactive',
        '--nologo',
        '--suppress-agent-init'
      ]),
    timeoutMs: 180_000,
    interact: async () => {
    }
  });
}

module.exports = {
  createStarterProject
};
