'use strict';

async function runAspireNew(shell, { template, timeoutMs = 180_000 }) {
  await shell.runAspireCommand(
    ['new', '--channel', shell.channel],
    { artifactName: 'aspire-new', cwd: shell.scenarioRoot });

  await shell.waitFor('> Starter App', 'template selection prompt', timeoutMs);
  await shell.type(template.selectionText);
  await shell.enter();

  await shell.waitFor('Enter the project name', 'project name prompt', timeoutMs);
  await shell.type(template.projectName);
  await shell.enter();

  await shell.waitFor('Enter the output path', 'output path prompt', timeoutMs);
  await shell.enter();

  await shell.waitFor('Use *.dev.localhost URLs [y/N]:', 'URLs prompt', timeoutMs);
  await shell.enter();

  if (template.hasRedisCachePrompt) {
    await shell.waitFor('Use Redis Cache [Y/n]:', 'Redis prompt', timeoutMs);
    await shell.type('n');
  }

  if (template.hasTestProjectPrompt) {
    await shell.waitFor('Do you want to create a test project? [y/N]:', 'test project prompt', timeoutMs);
    await shell.enter();
  }

  const agentInitPrompt = 'Would you like to configure AI agent environments for this project? [Y/n]:';
  const nextStep = await shell.waitForTextOrExit(
    agentInitPrompt,
    'agent init prompt or command completion',
    timeoutMs);

  if (nextStep === agentInitPrompt) {
    await shell.type('n');
  }
}

module.exports = {
  runAspireNew
};
