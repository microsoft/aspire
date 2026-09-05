'use strict';

async function runAspireStop(shell, {
  allowNotRunning = false,
  artifactName = 'aspire-stop',
  timeoutMs = 120_000
} = {}) {
  await shell.runAspireCommand(['stop'], { artifactName, timeoutMs });
  const expectedResults = ['stopped successfully.'];

  if (allowNotRunning) {
    expectedResults.push('No running AppHost found.');
  }

  await shell.waitForAny(expectedResults, 'AppHost stop result', timeoutMs);
}

module.exports = {
  runAspireStop
};
