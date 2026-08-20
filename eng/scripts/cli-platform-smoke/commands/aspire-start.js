'use strict';

async function runAspireStart(shell, { timeoutMs }) {
  await shell.runAspireCommand(['start'], { artifactName: 'aspire-start' });
  await shell.waitFor('AppHost started successfully.', 'AppHost start success', timeoutMs);
}

module.exports = {
  runAspireStart
};
