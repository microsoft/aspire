'use strict';

async function runAspireWait(shell, { resourceName, resourceReadyTimeoutSeconds }) {
  const timeoutMs = resourceReadyTimeoutSeconds * 1000 + 120_000;
  await shell.runAspireCommand(
    ['wait', resourceName, '--status', 'up', '--timeout', String(resourceReadyTimeoutSeconds)],
    { artifactName: `aspire-wait-${resourceName}`, timeoutMs });
  await shell.waitFor('is up (running).', `resource '${resourceName}' running`, timeoutMs);
}

module.exports = {
  runAspireWait
};
