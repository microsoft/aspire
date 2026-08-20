#!/usr/bin/env node
'use strict';

const path = require('path');

const { defaultValidationRoot, parseArgs } = require('./lib/options');
const { runStarterValidation } = require('./lib/starter-validation');

const templates = [
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

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const validationRoot = path.resolve(options.validationRoot || defaultValidationRoot());
  const channel = options.channel || `pr-${options.prNumber}`;
  const aspireCommand = options.aspireCommand || process.env.ASPIRE_SMOKE_ASPIRE_COMMAND || 'aspire';
  const failures = [];

  for (const template of templates) {
    try {
      await runStarterValidation(
        template,
        {
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

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
