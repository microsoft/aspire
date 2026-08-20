'use strict';

function createAspireNewScenario(template) {
  return {
    description: `Create ${template.projectName} with aspire new`,
    timeoutMs: 180_000,
    cwd: context => context.scenarioRoot,
    callback: async ({ channel, runAspireCommand, timeoutMs }) => {
      const run = await runAspireCommand(['new', '--channel', channel]);

      await run.waitForText('> Starter App', timeoutMs, 'template selection prompt');
      await run.type(template.selectionText);
      await run.enter();

      await run.waitForText('Enter the project name', timeoutMs, 'project name prompt');
      await run.type(template.projectName);
      await run.enter();

      await run.waitForText('Enter the output path', timeoutMs, 'output path prompt');
      await run.enter();

      await run.waitForText('Use *.dev.localhost URLs', timeoutMs, 'URLs prompt');
      await run.enter();

      await run.waitForText('Use Redis Cache', timeoutMs, 'Redis prompt');
      await run.type('n');

      if (template.hasTestProjectPrompt) {
        await run.waitForText('Do you want to create a test project?', timeoutMs, 'test project prompt');
        await run.enter();
      }

      const nextStep = await run.waitForAnyText(
        ['configure AI agent environments', run.exitNeedle],
        timeoutMs,
        'agent init prompt or command completion');

      if (nextStep === 'configure AI agent environments') {
        await run.type('n');
      }
    }
  };
}

module.exports = {
  createAspireNewScenario
};
