'use strict';

const fs = require('fs');
const path = require('path');

const {
  buildAspireCommand,
  cleanupProject,
  runAspireAddInteractive,
  runAspireNewInteractive,
  runAspireRunInteractive,
  runSimpleAspireCommand,
  sanitizeFileName
} = require('./command-runner');

async function runStarterScenario(
  {
    expectedResources,
    hasTestProjectPrompt,
    projectName,
    templateId
  },
  {
    aspireCommand,
    channel,
    maxStartupSeconds,
    resourceReadyTimeoutSeconds,
    validationRoot
  }) {
  const templateRoot = path.join(validationRoot, templateId);
  const diagnosticsDir = path.join(templateRoot, 'diagnostics');
  const projectRoot = path.join(templateRoot, projectName);

  fs.mkdirSync(diagnosticsDir, { recursive: true });

  try {
    await runAspireNewInteractive({
      cwd: templateRoot,
      diagnosticsDir,
      command: buildAspireCommand(
        aspireCommand,
        [
          'new',
          templateId,
          '--name', projectName,
          '--output', projectRoot,
          '--channel', channel
        ]),
      hasTestProjectPrompt,
      projectName,
      timeoutMs: 180_000
    });

    await runAspireAddInteractive({
      cwd: projectRoot,
      diagnosticsDir,
      command: buildAspireCommand(aspireCommand, ['add']),
      integrationFilter: 'postgres',
      timeoutMs: 180_000
    });

    await runAspireRunInteractive({
      cwd: projectRoot,
      diagnosticsDir,
      command: buildAspireCommand(aspireCommand, ['run']),
      timeoutMs: Math.max(maxStartupSeconds, resourceReadyTimeoutSeconds) * 1000 + 180_000
    });

    await runSimpleAspireCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-start.log',
      command: buildAspireCommand(aspireCommand, ['start']),
      waitForTexts: ['AppHost started successfully.'],
      timeoutMs: maxStartupSeconds * 1000 + 180_000
    });

    await runSimpleAspireCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-resources.log',
      command: buildAspireCommand(aspireCommand, ['resources']),
      waitForTexts: expectedResources,
      timeoutMs: 180_000
    });

    for (const resourceName of expectedResources) {
      await runSimpleAspireCommand({
        cwd: projectRoot,
        diagnosticsDir,
        fileName: `aspire-wait-${sanitizeFileName(resourceName)}.log`,
        command: buildAspireCommand(aspireCommand, ['wait', resourceName, '--status', 'up', '--timeout', String(resourceReadyTimeoutSeconds)]),
        waitForTexts: ['is up (running).'],
        timeoutMs: resourceReadyTimeoutSeconds * 1000 + 120_000
      });
    }

    await runSimpleAspireCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-stop.log',
      command: buildAspireCommand(aspireCommand, ['stop']),
      waitForAnyTexts: ['Running instance stopped successfully.', 'No running AppHost found.'],
      timeoutMs: 120_000
    });
  } finally {
    await cleanupProject(projectRoot, diagnosticsDir, aspireCommand);
  }
}

module.exports = {
  runStarterScenario
};
