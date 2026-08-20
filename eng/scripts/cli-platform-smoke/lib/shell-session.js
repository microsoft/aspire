'use strict';

const fs = require('fs');
const pty = require('node-pty');

const { delay, escapeRegExp, getShellSpec, stripAnsi } = require('./shell-spec');

const readySentinel = '__ASPIRE_SMOKE_READY__';

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
        // The Aspire CLI intentionally disables prompts when it detects CI. Playground mode
        // forces interactive behavior so this PTY-driven smoke test exercises the real prompts.
        ASPIRE_PLAYGROUND: 'true',
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

  waitForTextOrExit(text, timeoutMs, description) {
    return this.session.waitForSlice(
      this.startIndex,
      slice => {
        if (slice.includes(text)) {
          return text;
        }

        // The PTY first echoes the wrapper; in bash that includes
        // `__ASPIRE_SMOKE_DONE_...__:%s`. Only the later marker with an integer
        // exit code represents command completion.
        return this.findExitMatch(slice) ? 'command-exited' : null;
      },
      timeoutMs,
      description);
  }

  async waitForExit(timeoutMs) {
    const marker = await this.session.waitForSlice(
      this.startIndex,
      slice => this.findExitMatch(slice)?.[0] || null,
      timeoutMs,
      'waiting for command completion');

    const slice = this.session.cleanOutput.slice(this.startIndex);
    const match = this.findExitMatch(slice);
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

  findExitMatch(slice) {
    return new RegExp(`${escapeRegExp(this.sentinel)}:(-?\\d+)`).exec(slice);
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

module.exports = {
  ShellSession
};
