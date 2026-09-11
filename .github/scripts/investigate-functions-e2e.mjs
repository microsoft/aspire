// Investigation-only external observer. The baseline runner, VSIX and Mocha assertion stay unchanged.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const root = path.resolve('.test-results', 'functions-investigation');
fs.mkdirSync(root, { recursive: true });
const deadline = Date.now() + 140 * 60_000;
const statuses = [];
const expectedFailure = 'Expected the Azure Functions resource to expose an HTTPS URL.';

for (let iteration = 1; iteration <= 10; iteration++) {
  // Reserve a full iteration plus cleanup; the workflow has a separate outer time limit.
  if (Date.now() + 15 * 60_000 > deadline) {
    console.log('Stopping at the reproduction time budget.');
    break;
  }
  const shard = `azure-functions-baseline-${iteration}`;
  const results = path.resolve('.test-results', 'e2e', shard);
  fs.mkdirSync(results, { recursive: true });
  const statePath = path.join(results, 'extension-state.json');
  const chronologyPath = path.join(results, 'resource-chronology.jsonl');
  const log = fs.createWriteStream(path.join(results, 'raw-process.log'));
  const started = new Date().toISOString();
  let previous = '';
  let readErrors = 0;
  let finished = false;
  let observerError;

  function observe() {
    let text;
    try {
      text = fs.readFileSync(statePath, 'utf8');
    } catch (error) {
      if (error.code === 'ENOENT') { return; }
      throw error;
    }
    let payload;
    try {
      payload = JSON.parse(text);
    } catch (error) {
      // The bridge can be observed between truncation and write. Count partial reads, do not
      // invent empty state. JSON shape: {"state":{"workspaceAppHost":{...},"appHosts":[...]}}.
      if (!(error instanceof SyntaxError)) { throw error; }
      readErrors++;
      return;
    }
    const state = payload.state;
    const hosts = [
      { appHostPath: state?.workspaceAppHostPath, resources: state?.workspaceResources },
      state?.workspaceAppHost, ...(state?.appHosts ?? []),
    ].filter(Boolean);
    const snapshot = hosts.map(host => ({
      appHostPath: host.appHostPath,
      resources: host.resources?.map(resource => ({
        name: resource.name,
        state: resource.state,
        // Keep endpoint identity/timing but never persist credentials or dashboard query tokens.
        urls: resource.urls?.map(endpoint => {
          const url = new URL(endpoint.url);
          return { name: endpoint.name, url: `${url.protocol}//${url.host}${url.pathname}` };
        }),
      })),
    }));
    const serialized = JSON.stringify(snapshot);
    if (serialized !== previous) {
      fs.appendFileSync(chronologyPath, JSON.stringify({
        observedAt: new Date().toISOString(), updatedAt: payload.updatedAt,
        runId: payload.runId, hosts: snapshot,
      }) + '\n');
      previous = serialized;
    }
  }

  const child = spawn('xvfb-run', ['-a', 'node', 'scripts/run-e2e.js'], {
    env: {
      ...process.env,
      ASPIRE_EXTENSION_E2E_SHARD: shard,
      ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE: '',
      ASPIRE_EXTENSION_E2E_RUN_TESTS_TIMEOUT_MS: '720000',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', data => { log.write(data); process.stdout.write(data); });
  child.stderr.on('data', data => { log.write(data); process.stderr.write(data); });
  const result = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({ code, signal }));
  });
  // Observe from a separate process because run-e2e uses synchronous setup child processes.
  // Polling samples state before teardown; it is not an exhaustive DCP event trace.
  const observer = (async () => {
    while (!finished) {
      observe();
      await delay(25);
    }
    observe();
  })().catch(error => { observerError = error; });
  let exit;
  try {
    exit = await result;
  } finally {
    finished = true;
    await observer;
    await new Promise(resolve => log.end(resolve));
  }

  const mochaPath = path.join(results, 'mocha.json');
  const mocha = fs.existsSync(mochaPath) ? JSON.parse(fs.readFileSync(mochaPath, 'utf8')) : null;
  const matchingFailure = JSON.stringify(mocha?.failures ?? []).includes(expectedFailure);
  const status = {
    iteration, shard, started, finished: new Date().toISOString(), ...exit,
    stats: mocha?.stats ?? null, matchingFailure, partialStateReads: readErrors,
    observerError: observerError?.message,
  };
  statuses.push(status);
  fs.writeFileSync(path.join(root, 'statuses.json'), JSON.stringify(statuses, null, 2));
  console.log(JSON.stringify(status));
  if (process.env.GITHUB_STEP_SUMMARY) {
    fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `\nIteration ${iteration}: raw exit ${exit.code}, matching assertion ${matchingFailure}, stats ${JSON.stringify(status.stats)}\n`);
  }
  if (matchingFailure || exit.code !== 0 || !mocha?.stats?.passes || observerError) {
    // Unrelated setup/test failures stop too, but are explicitly not marked as reproductions.
    process.exitCode = 1;
    break;
  }
}
