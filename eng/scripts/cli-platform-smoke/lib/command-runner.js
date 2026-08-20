'use strict';

const fs = require('fs');
const path = require('path');

const { ShellSession } = require('./shell-session');
const { getShellSpec } = require('./shell-spec');

function buildAspireCommand(prefix, args) {
  const shell = getShellSpec();
  return `${prefix} ${args.map(arg => shell.quote(arg)).join(' ')}`.trim();
}

async function runAspireNewInteractive({
  cwd,
  diagnosticsDir,
  command,
  hasTestProjectPrompt,
  projectName,
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-new.log',
    command,
    timeoutMs,
    interact: async run => {
      await run.waitForText('Enter the project name', timeoutMs, 'project name prompt');
      await run.type(projectName);
      await run.enter();

      await run.waitForText('Enter the output path', timeoutMs, 'output path prompt');
      await run.enter();

      await run.waitForText('Use *.dev.localhost URLs', timeoutMs, 'URLs prompt');
      await run.enter();

      await run.waitForText('Use Redis Cache', timeoutMs, 'Redis prompt');
      await run.type('n');

      if (hasTestProjectPrompt) {
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
  });
}

async function runAspireAddInteractive({ cwd, diagnosticsDir, command, integrationFilter, timeoutMs }) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-add.log',
    command,
    timeoutMs,
    interact: async run => {
      await run.waitForText('Select an integration to add:', timeoutMs, 'integration selection prompt');
      await run.type(integrationFilter);
      await run.enter();

      const addState = await run.waitForAnyText(
        ['Select a version of', 'was added successfully.'],
        timeoutMs,
        'package version selection or add success');

      if (addState === 'Select a version of') {
        await run.enter();
      }

      await run.waitForText('was added successfully.', timeoutMs, 'integration add success');
    }
  });
}

async function runAspireRunInteractive({ cwd, diagnosticsDir, command, timeoutMs }) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-run.log',
    command,
    timeoutMs,
    interact: async run => {
      await run.waitForText('Press CTRL+C to stop the AppHost and exit.', timeoutMs, 'run ready banner');
      await run.ctrlC();
    }
  });
}

async function runSimpleAspireCommand({
  cwd,
  diagnosticsDir,
  fileName,
  command,
  waitForTexts = [],
  waitForAnyTexts = [],
  timeoutMs
}) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName,
    command,
    timeoutMs,
    interact: async run => {
      for (const text of waitForTexts) {
        await run.waitForText(text, timeoutMs, `waiting for '${text}'`);
      }

      if (waitForAnyTexts.length > 0) {
        await run.waitForAnyText(waitForAnyTexts, timeoutMs, `waiting for one of: ${waitForAnyTexts.join(', ')}`);
      }
    }
  });
}

async function cleanupProject(projectRoot, diagnosticsDir, aspireCommand) {
  if (!fs.existsSync(projectRoot)) {
    return;
  }

  try {
    await runSimpleAspireCommand({
      cwd: projectRoot,
      diagnosticsDir,
      fileName: 'aspire-stop-cleanup.log',
      command: buildAspireCommand(aspireCommand, ['stop']),
      waitForAnyTexts: ['Running instance stopped successfully.', 'No running AppHost found.'],
      timeoutMs: 120_000
    });
  } catch {
    // Best-effort cleanup so a failed proof does not hide the original validation error.
  }
}

async function runInteractiveCommand({
  cwd,
  diagnosticsDir,
  fileName,
  command,
  timeoutMs,
  interact
}) {
  const logPath = path.join(diagnosticsDir, fileName);
  const session = await ShellSession.start(cwd);
  let run = null;

  try {
    run = await session.startCommand(command, logPath);
    await interact(run);
    await run.waitForExit(timeoutMs);
  } catch (error) {
    if (run && logPath) {
      run.flushArtifacts();
    }

    throw error;
  } finally {
    await session.dispose();
  }
}

function sanitizeFileName(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '_');
}

module.exports = {
  buildAspireCommand,
  cleanupProject,
  runAspireAddInteractive,
  runAspireNewInteractive,
  runAspireRunInteractive,
  runSimpleAspireCommand,
  sanitizeFileName
};
