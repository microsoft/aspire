#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

const { createScenarioContext } = require('./lib/scenario-context');
const { defaultValidationRoot, parseArgs } = require('./lib/options');
const { runScenario } = require('./lib/run-scenario');
const { createDotnetStarterScenario } = require('./scenarios/dotnet-starter');
const { createTypeScriptStarterScenario } = require('./scenarios/typescript-starter');

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
  const scenarioOptions = {
    maxStartupSeconds: options.maxStartupSeconds,
    resourceReadyTimeoutSeconds: options.resourceReadyTimeoutSeconds
  };
  const scenarios = [
    createTypeScriptStarterScenario(scenarioOptions),
    createDotnetStarterScenario(scenarioOptions)
  ];
  const failures = [];

  fs.rmSync(validationRoot, { recursive: true, force: true });
  fs.mkdirSync(validationRoot, { recursive: true });

  for (const scenario of scenarios) {
    try {
      await runScenario(scenario, createScenarioContext(baseContext, scenario));
    } catch (error) {
      const message = `${scenario.id}: ${error.message}`;
      console.warn(message);
      if (error.terminalOutput) {
        console.warn([
          `--- ${scenario.id}: latest terminal output (up to 48 lines) ---`,
          error.terminalOutput,
          `--- ${scenario.id}: end terminal output ---`
        ].join('\n'));
      }
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
