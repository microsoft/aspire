'use strict';

async function runAspireResources(shell, { expectedResources, timeoutMs = 180_000 }) {
  await shell.runAspireCommand(['resources'], { artifactName: 'aspire-resources', timeoutMs });

  for (const resourceName of expectedResources) {
    await shell.waitFor(resourceName, `resource '${resourceName}'`, timeoutMs);
  }
}

module.exports = {
  runAspireResources
};
