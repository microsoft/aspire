'use strict';

const { runAspireAddInteractive } = require('../commands/aspire-add');
const { runAspireNewInteractive } = require('../commands/aspire-new');
const { runAspireResources } = require('../commands/aspire-resources');
const { runAspireRunInteractive } = require('../commands/aspire-run');
const { runAspireStart } = require('../commands/aspire-start');
const { runAspireStop } = require('../commands/aspire-stop');
const { runAspireWait } = require('../commands/aspire-wait');
const { stopAppHost } = require('../scriptlets/stop-apphost');

const template = {
  projectName: 'AspireCliTsStarterSmoke',
  selectionText: 'Starter App (Express/React, TypeScript AppHost)',
  expectedResources: ['app', 'frontend'],
  hasTestProjectPrompt: false
};

module.exports = {
  id: 'typescript-starter',
  projectName: template.projectName,
  async run(context) {
    const {
      aspireCommand,
      channel,
      diagnosticsDir,
      maxStartupSeconds,
      projectRoot,
      resourceReadyTimeoutSeconds,
      scenarioRoot
    } = context;

    try {
      await runAspireNewInteractive({
        aspireCommand,
        channel,
        diagnosticsDir,
        scenarioRoot,
        template,
        timeoutMs: 180_000
      });
      await runAspireAddInteractive({
        aspireCommand,
        cwd: projectRoot,
        diagnosticsDir,
        integrationFilter: 'postgres',
        timeoutMs: 180_000
      });
      await runAspireRunInteractive({
        aspireCommand,
        diagnosticsDir,
        projectRoot,
        timeoutMs: Math.max(maxStartupSeconds, resourceReadyTimeoutSeconds) * 1000 + 180_000
      });
      await runAspireStart({
        aspireCommand,
        diagnosticsDir,
        projectRoot,
        timeoutMs: maxStartupSeconds * 1000 + 180_000
      });
      await runAspireResources({
        aspireCommand,
        diagnosticsDir,
        expectedResources: template.expectedResources,
        projectRoot,
        timeoutMs: 180_000
      });

      for (const resourceName of template.expectedResources) {
        await runAspireWait({
          aspireCommand,
          diagnosticsDir,
          projectRoot,
          resourceName,
          resourceReadyTimeoutSeconds,
          timeoutMs: resourceReadyTimeoutSeconds * 1000 + 120_000
        });
      }

      await runAspireStop({
        aspireCommand,
        diagnosticsDir,
        projectRoot,
        timeoutMs: 120_000
      });
    } finally {
      await stopAppHost({ aspireCommand, diagnosticsDir, projectRoot });
    }
  }
};
