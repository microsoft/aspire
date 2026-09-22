'use strict';

async function runAspireRun(shell, { timeoutMs }) {
  await shell.runAspireCommand(['run'], { artifactName: 'aspire-run', timeoutMs });
  await shell.waitFor(
    'Press CTRL+C to stop the AppHost and exit.',
    'run ready banner',
    timeoutMs);
  await shell.ctrlC();
}

module.exports = {
  runAspireRun
};
