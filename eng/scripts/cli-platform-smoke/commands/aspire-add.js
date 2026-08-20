'use strict';

async function runAspireAdd(shell, { integrationFilter, timeoutMs = 180_000 }) {
  await shell.runAspireCommand(['add'], { artifactName: 'aspire-add' });

  await shell.waitFor('Select an integration to add:', 'integration selection prompt', timeoutMs);
  await shell.type(integrationFilter);
  await shell.enter();

  const addState = await shell.waitForAny(
    ['Select a version of', 'was added successfully.'],
    'package version selection or add success',
    timeoutMs);

  if (addState === 'Select a version of') {
    await shell.enter();
  }

  await shell.waitFor('was added successfully.', 'integration add success', timeoutMs);
}

module.exports = {
  runAspireAdd
};
