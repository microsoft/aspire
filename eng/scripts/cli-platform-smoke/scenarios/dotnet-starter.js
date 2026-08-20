'use strict';

const { createAspireAddScenario } = require('./aspire-add');
const { createAspireNewScenario } = require('./aspire-new');
const { createAspireResourcesScenario } = require('./aspire-resources');
const { createAspireRunScenario } = require('./aspire-run');
const { createAspireStartScenario } = require('./aspire-start');
const { createAspireStopScenario } = require('./aspire-stop');
const { createAspireWaitScenario } = require('./aspire-wait');

const template = {
  projectName: 'AspireCliCsStarterSmoke',
  selectionText: 'Starter App (ASP.NET Core/Blazor)',
  expectedResources: ['apiservice'],
  hasTestProjectPrompt: true
};

function createDotnetStarterScenario({ maxStartupSeconds, resourceReadyTimeoutSeconds }) {
  const cleanupScenario = createAspireStopScenario({
    allowNotRunning: true,
    description: 'Clean up the .NET AppHost',
    runAfterCancellation: true
  });

  return {
    id: 'dotnet-starter',
    description: 'Validate the .NET starter command lifecycle',
    projectName: template.projectName,
    timeoutMs: 40 * 60_000,
    callback: async ({ runScenario }) => {
      try {
        await runScenario(createAspireNewScenario(template));
        await runScenario(createAspireAddScenario('postgres'));
        await runScenario(createAspireRunScenario(
          Math.max(maxStartupSeconds, resourceReadyTimeoutSeconds) * 1000 + 180_000));
        await runScenario(createAspireStartScenario(maxStartupSeconds * 1000 + 180_000));
        await runScenario(createAspireResourcesScenario(template.expectedResources));

        for (const resourceName of template.expectedResources) {
          await runScenario(createAspireWaitScenario(resourceName, resourceReadyTimeoutSeconds));
        }

        await runScenario(createAspireStopScenario());
      } finally {
        await runScenario(cleanupScenario).catch(() => {
          // Cleanup must not hide the original scenario failure.
        });
      }
    }
  };
}

module.exports = {
  createDotnetStarterScenario
};
