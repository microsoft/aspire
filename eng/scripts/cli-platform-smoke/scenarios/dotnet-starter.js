'use strict';

const fs = require('fs');

const { runAspireAdd } = require('../commands/aspire-add');
const { runAspireNew } = require('../commands/aspire-new');
const { runAspireResources } = require('../commands/aspire-resources');
const { runAspireRun } = require('../commands/aspire-run');
const { runAspireStart } = require('../commands/aspire-start');
const { runAspireStop } = require('../commands/aspire-stop');
const { runAspireWait } = require('../commands/aspire-wait');

const template = {
  projectName: 'AspireCliCsStarterSmoke',
  selectionText: 'Starter App (ASP.NET Core/Blazor)',
  expectedResources: ['apiservice'],
  hasTestProjectPrompt: true
};

function createDotnetStarterScenario({ maxStartupSeconds, resourceReadyTimeoutSeconds }) {
  return {
    id: 'dotnet-starter',
    description: 'Validate the .NET starter command lifecycle',
    projectName: template.projectName,
    timeoutMs: 40 * 60_000,
    callback: async shell => {
      await runAspireNew(shell, { template });
      await runAspireAdd(shell, { integrationFilter: 'postgres' });
      await runAspireRun(shell, {
        timeoutMs: Math.max(maxStartupSeconds, resourceReadyTimeoutSeconds) * 1000 + 180_000
      });
      await runAspireStart(shell, {
        timeoutMs: maxStartupSeconds * 1000 + 180_000
      });
      await runAspireResources(shell, { expectedResources: template.expectedResources });

      for (const resourceName of template.expectedResources) {
        await runAspireWait(shell, { resourceName, resourceReadyTimeoutSeconds });
      }

      await runAspireStop(shell);
    },
    cleanup: async shell => {
      if (fs.existsSync(shell.projectRoot)) {
        await runAspireStop(shell, {
          allowNotRunning: true,
          artifactName: 'cleanup-aspire-stop'
        });
      }
    }
  };
}

module.exports = {
  createDotnetStarterScenario
};
