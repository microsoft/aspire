'use strict';

const fs = require('fs');
const path = require('path');

const { runAspireAddInteractive } = require('../scenarios/aspire-add');
const { runAspireNewInteractive } = require('../scenarios/aspire-new');
const { runAspireResources } = require('../scenarios/aspire-resources');
const { runAspireRunInteractive } = require('../scenarios/aspire-run');
const { runAspireStart } = require('../scenarios/aspire-start');
const { cleanupProject, runAspireStop } = require('../scenarios/aspire-stop');
const { runAspireWait } = require('../scenarios/aspire-wait');

async function runStarterValidation(
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
      aspireCommand,
      channel,
      cwd: templateRoot,
      diagnosticsDir,
      hasTestProjectPrompt,
      outputPath: projectRoot,
      projectName,
      templateId,
      timeoutMs: 180_000
    });

    await runAspireAddInteractive({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      integrationFilter: 'postgres',
      timeoutMs: 180_000
    });

    await runAspireRunInteractive({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      timeoutMs: Math.max(maxStartupSeconds, resourceReadyTimeoutSeconds) * 1000 + 180_000
    });

    await runAspireStart({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      timeoutMs: maxStartupSeconds * 1000 + 180_000
    });

    await runAspireResources({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      expectedResources,
      timeoutMs: 180_000
    });

    for (const resourceName of expectedResources) {
      await runAspireWait({
        aspireCommand,
        cwd: projectRoot,
        diagnosticsDir,
        resourceName,
        resourceReadyTimeoutSeconds,
        timeoutMs: resourceReadyTimeoutSeconds * 1000 + 120_000
      });
    }

    await runAspireStop({
      aspireCommand,
      cwd: projectRoot,
      diagnosticsDir,
      timeoutMs: 120_000
    });
  } finally {
    await cleanupProject(projectRoot, diagnosticsDir, aspireCommand);
  }
}

module.exports = {
  runStarterValidation
};
