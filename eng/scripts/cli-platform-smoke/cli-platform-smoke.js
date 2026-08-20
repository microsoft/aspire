#!/usr/bin/env node
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const pty = require('node-pty');

const readySentinel = '__ASPIRE_SMOKE_READY__';

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const validationRoot = path.resolve(options.validationRoot || defaultValidationRoot());
  const channel = options.channel || `pr-${options.prNumber}`;
  const aspireCommand = options.aspireCommand || process.env.ASPIRE_SMOKE_ASPIRE_COMMAND || 'aspire';

  fs.rmSync(validationRoot, { recursive: true, force: true });
  fs.mkdirSync(validationRoot, { recursive: true });

  const failures = [];

  for (const template of templates()) {
    try {
      await validateTemplate({
        template,
        validationRoot,
        channel,
        aspireCommand,
        maxStartupSeconds: options.maxStartupSeconds,
        resourceReadyTimeoutSeconds: options.resourceReadyTimeoutSeconds
      });
    } catch (error) {
      const message = `${template.templateId}: ${error.message}`;
      console.warn(message);
      failures.push(message);
    }
  }

  if (failures.length > 0) {
    throw new Error(`Starter validation failures:\n- ${failures.join('\n- ')}`);
  }
}

function templates() {
  return [
    {
      templateId: 'aspire-ts-starter',
      projectName: 'AspireCliTsStarterSmoke',
      expectedResources: ['app', 'frontend'],
      hasTestProjectPrompt: false
    },
    {
      templateId: 'aspire-starter',
      projectName: 'AspireCliCsStarterSmoke',
      expectedResources: ['apiservice'],
      hasTestProjectPrompt: true
    }
  ];
}

async function validateTemplate({
  template,
  validationRoot,
  channel,
  aspireCommand,
  maxStartupSeconds,
  resourceReadyTimeoutSeconds
}) {
  const templateRoot = path.join(validationRoot, template.templateId);
  const diagnosticsDir = path.join(templateRoot, 'diagnostics');
  const projectRoot = path.join(templateRoot, template.projectName);

  fs.mkdirSync(diagnosticsDir, { recursive: true });

  try {
    await runAspireNewInteractive({
      cwd: templateRoot,
      diagnosticsDir,
      command: buildAspireCommand(
        aspireCommand,
        [
          'new',
          template.templateId,
          '--name', template.projectName,
          '--output', projectRoot,
          '--channel', channel
        ]),
      template,
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
      waitForTexts: template.expectedResources,
      timeoutMs: 180_000
    });

    for (const resourceName of template.expectedResources) {
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

function buildAspireCommand(prefix, args) {
  const shell = getShellSpec();
  return `${prefix} ${args.map(arg => shell.quote(arg)).join(' ')}`.trim();
}

async function runAspireNewInteractive({ cwd, diagnosticsDir, command, template, timeoutMs }) {
  await runInteractiveCommand({
    cwd,
    diagnosticsDir,
    fileName: 'aspire-new.log',
    command,
    timeoutMs,
    interact: async run => {
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

class ShellSession {
  static async start(cwd) {
    const shell = getShellSpec();
    const ptyProcess = pty.spawn(shell.file, shell.args, {
      cwd,
      cols: 160,
      rows: 48,
      env: {
        ...process.env,
        ASPIRE_CLI_TELEMETRY_OPTOUT: 'true',
        DOTNET_CLI_TELEMETRY_OPTOUT: 'true',
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true',
        DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
        TERM: process.env.TERM || 'xterm-256color'
      }
    });

    const session = new ShellSession(shell, ptyProcess);
    await session.ready();
    return session;
  }

  constructor(shell, ptyProcess) {
    this.shell = shell;
    this.ptyProcess = ptyProcess;
    this.cols = 160;
    this.rows = 48;
    this.outputEvents = [];
    this.cleanOutput = '';
    this.startedAtMs = performance.now();
    this.startedAtUnixSeconds = Math.floor(Date.now() / 1000);
    this.waiters = [];

    ptyProcess.onData(data => {
      this.outputEvents.push({
        seconds: this.currentElapsedSeconds(),
        data
      });
      this.cleanOutput += stripAnsi(data);
      this.flushWaiters();
    });
  }

  async ready() {
    await delay(100);
    const readyRun = await this.startCommand('echo __ASPIRE_SMOKE_READY__');
    await readyRun.waitForText(readySentinel, 10_000, 'shell ready probe');
    await readyRun.waitForExit(10_000);
  }

  async startCommand(command, logPath = null) {
    const run = new ShellCommandRun(this, command, logPath);
    run.start();
    return run;
  }

  waitForSlice(fromIndex, predicate, timeoutMs, description) {
    const immediate = predicate(this.cleanOutput.slice(fromIndex));
    if (immediate) {
      return Promise.resolve(immediate);
    }

    return new Promise((resolve, reject) => {
      const waiter = {
        fromIndex,
        predicate,
        resolve,
        reject,
        description,
        timeout: setTimeout(() => {
          this.waiters = this.waiters.filter(candidate => candidate !== waiter);
          reject(new Error(`Timed out after ${Math.round(timeoutMs / 1000)} second(s) ${description}.`));
        }, timeoutMs)
      };

      this.waiters.push(waiter);
    });
  }

  flushWaiters() {
    for (const waiter of [...this.waiters]) {
      const result = waiter.predicate(this.cleanOutput.slice(waiter.fromIndex));
      if (!result) {
        continue;
      }

      clearTimeout(waiter.timeout);
      this.waiters = this.waiters.filter(candidate => candidate !== waiter);
      waiter.resolve(result);
    }
  }

  currentElapsedSeconds() {
    return (performance.now() - this.startedAtMs) / 1000;
  }

  async dispose() {
    this.waiters.splice(0).forEach(waiter => {
      clearTimeout(waiter.timeout);
      waiter.reject(new Error('Shell session disposed before the expected output appeared.'));
    });

    try {
      this.ptyProcess.kill();
    } catch {
      // Ignore disposal races where the shell already exited.
    }

    await delay(25);
  }
}

class ShellCommandRun {
  constructor(session, command, logPath) {
    this.session = session;
    this.command = command;
    this.logPath = logPath;
    this.startIndex = session.cleanOutput.length;
    this.eventStartIndex = session.outputEvents.length;
    this.eventStartSeconds = session.currentElapsedSeconds();
    this.sentinel = `__ASPIRE_SMOKE_DONE_${Date.now()}_${Math.random().toString(16).slice(2)}__`;
    this.exitNeedle = `${this.sentinel}:`;
  }

  start() {
    const wrapped = this.session.shell.wrapCommand(this.command, this.sentinel);
    this.session.ptyProcess.write(wrapped);
    this.session.ptyProcess.write(this.session.shell.enterKey);
  }

  waitForText(text, timeoutMs, description) {
    return this.session.waitForSlice(
      this.startIndex,
      slice => slice.includes(text) ? text : null,
      timeoutMs,
      description);
  }

  waitForAnyText(texts, timeoutMs, description) {
    return this.session.waitForSlice(
      this.startIndex,
      slice => texts.find(text => slice.includes(text)) || null,
      timeoutMs,
      description);
  }

  async waitForExit(timeoutMs) {
    const marker = await this.session.waitForSlice(
      this.startIndex,
      slice => {
        const match = new RegExp(`${escapeRegExp(this.sentinel)}:(-?\\d+)`).exec(slice);
        return match ? match[0] : null;
      },
      timeoutMs,
      'waiting for command completion');

    const slice = this.session.cleanOutput.slice(this.startIndex);
    const match = new RegExp(`${escapeRegExp(this.sentinel)}:(-?\\d+)`).exec(slice);
    const exitCode = match ? Number.parseInt(match[1], 10) : Number.NaN;

    this.flushArtifacts();

    if (!marker || Number.isNaN(exitCode)) {
      throw new Error(`Could not determine the exit code for '${this.command}'.`);
    }

    if (exitCode !== 0) {
      throw new Error(`'${this.command}' failed with exit code ${exitCode}. See ${this.logPath}.`);
    }

    return exitCode;
  }

  async type(text) {
    this.session.ptyProcess.write(text);
    await delay(10);
  }

  async enter() {
    this.session.ptyProcess.write(this.session.shell.enterKey);
    await delay(10);
  }

  async ctrlC() {
    this.session.ptyProcess.write('\u0003');
    await delay(10);
  }

  flushArtifacts() {
    if (!this.logPath) {
      return;
    }

    fs.writeFileSync(this.logPath, this.session.cleanOutput.slice(this.startIndex), 'utf8');
    // Keep the PTY transcript in asciinema v2 JSONL so the workflow can upload a replayable
    // artifact immediately, without a separate post-processing step on failure.
    fs.writeFileSync(this.getCastPath(), this.buildCastContents(), 'utf8');
  }

  getCastPath() {
    if (this.logPath.endsWith('.log')) {
      return `${this.logPath.slice(0, -4)}.cast`;
    }

    return `${this.logPath}.cast`;
  }

  buildCastContents() {
    const header = {
      version: 2,
      width: this.session.cols,
      height: this.session.rows,
      timestamp: this.session.startedAtUnixSeconds,
      env: {
        TERM: process.env.TERM || 'xterm-256color',
        SHELL: this.session.shell.file
      }
    };

    const lines = [JSON.stringify(header)];
    for (const event of this.session.outputEvents.slice(this.eventStartIndex)) {
      lines.push(JSON.stringify([
        Number((event.seconds - this.eventStartSeconds).toFixed(6)),
        'o',
        event.data
      ]));
    }

    return `${lines.join('\n')}\n`;
  }
}

function getShellSpec() {
  if (process.platform === 'win32') {
    return {
      file: 'pwsh',
      args: ['-NoLogo', '-NoProfile'],
      enterKey: '\r',
      quote: quotePowerShell,
      wrapCommand(command, sentinel) {
        return `${command}; Write-Output ('${sentinel}:' + $LASTEXITCODE)`;
      }
    };
  }

  return {
    file: 'bash',
    args: ['--noprofile', '--norc'],
    enterKey: '\r',
    quote: quoteBash,
    wrapCommand(command, sentinel) {
      return `${command}; printf '\\n${sentinel}:%s\\n' $?`;
    }
  };
}

function quoteBash(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function quotePowerShell(value) {
  return `'${String(value).replace(/'/g, `''`)}'`;
}

function stripAnsi(value) {
  return value.replace(/\u001b\[[0-9;?]*[A-Za-z]/g, '');
}

function sanitizeFileName(value) {
  return value.replace(/[^A-Za-z0-9_.-]/g, '_');
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function defaultValidationRoot() {
  if (process.env.RUNNER_TEMP && process.env.RUNNER_TEMP.trim().length > 0) {
    return path.join(process.env.RUNNER_TEMP, 'aspire-cli-starter-validation');
  }

  return path.join(os.tmpdir(), 'aspire-cli-starter-validation');
}

function parseArgs(argv) {
  const options = {
    prNumber: null,
    maxStartupSeconds: 120,
    resourceReadyTimeoutSeconds: 120,
    validationRoot: '',
    channel: '',
    aspireCommand: ''
  };

  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];

    switch (argument) {
      case '--pr-number':
        options.prNumber = parseRequiredInt(argv[++index], argument);
        break;

      case '--max-startup-seconds':
        options.maxStartupSeconds = parseRequiredInt(argv[++index], argument);
        break;

      case '--resource-ready-timeout-seconds':
        options.resourceReadyTimeoutSeconds = parseRequiredInt(argv[++index], argument);
        break;

      case '--validation-root':
        options.validationRoot = parseRequiredValue(argv[++index], argument);
        break;

      case '--channel':
        options.channel = parseRequiredValue(argv[++index], argument);
        break;

      case '--aspire-command':
        options.aspireCommand = parseRequiredValue(argv[++index], argument);
        break;

      default:
        throw new Error(`Unknown argument '${argument}'.`);
    }
  }

  if (!options.prNumber) {
    throw new Error('The --pr-number argument is required.');
  }

  return options;
}

function parseRequiredInt(value, argumentName) {
  const parsed = Number.parseInt(parseRequiredValue(value, argumentName), 10);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`Argument '${argumentName}' must be a positive integer.`);
  }

  return parsed;
}

function parseRequiredValue(value, argumentName) {
  if (!value || value.trim().length === 0) {
    throw new Error(`Argument '${argumentName}' requires a non-empty value.`);
  }

  return value;
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
