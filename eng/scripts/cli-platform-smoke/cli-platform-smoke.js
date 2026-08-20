#!/usr/bin/env node
'use strict';

const path = require('path');

const { defaultValidationRoot, parseArgs } = require('./lib/options');
const scenarios = require('./scenarios');

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const validationRoot = path.resolve(options.validationRoot || defaultValidationRoot());
  const channel = options.channel || `pr-${options.prNumber}`;
  const aspireCommand = options.aspireCommand || process.env.ASPIRE_SMOKE_ASPIRE_COMMAND || 'aspire';
  const failures = [];

  for (const scenario of scenarios) {
    try {
      await scenario.run({
        validationRoot,
        channel,
        aspireCommand,
        maxStartupSeconds: options.maxStartupSeconds,
        resourceReadyTimeoutSeconds: options.resourceReadyTimeoutSeconds
      });
    } catch (error) {
      const message = `${scenario.templateId}: ${error.message}`;
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
