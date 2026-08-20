#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

const { createScenarioContext } = require('./lib/scenario-context');
const { defaultValidationRoot, parseArgs } = require('./lib/options');
const scenarios = [
  require('./scenarios/typescript-starter'),
  require('./scenarios/dotnet-starter')
];

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const validationRoot = path.resolve(options.validationRoot || defaultValidationRoot());
  const channel = options.channel || `pr-${options.prNumber}`;
  const aspireCommand = options.aspireCommand || process.env.ASPIRE_SMOKE_ASPIRE_COMMAND || 'aspire';
  const baseContext = {
    validationRoot,
    channel,
    aspireCommand,
    maxStartupSeconds: options.maxStartupSeconds,
    resourceReadyTimeoutSeconds: options.resourceReadyTimeoutSeconds
  };
  const failures = [];

  fs.rmSync(validationRoot, { recursive: true, force: true });
  fs.mkdirSync(validationRoot, { recursive: true });

  for (const scenario of scenarios) {
    try {
      await scenario.run(createScenarioContext(baseContext, scenario));
    } catch (error) {
      const message = `${scenario.id}: ${error.message}`;
      console.warn(message);
      failures.push(message);
    }
  }

  if (failures.length > 0) {
    throw new Error(`Starter validation failures:\n- ${failures.join('\n- ')}`);
  }
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
