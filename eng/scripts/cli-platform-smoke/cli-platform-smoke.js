#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

const { createScenarioContext } = require('./lib/scenario-context');
const { defaultValidationRoot, parseArgs } = require('./lib/options');
const scenarios = [
  require('./scenarios/aspire-new'),
  require('./scenarios/aspire-add'),
  require('./scenarios/aspire-run'),
  require('./scenarios/aspire-start'),
  require('./scenarios/aspire-stop'),
  require('./scenarios/aspire-resources'),
  require('./scenarios/aspire-wait')
];

const templates = new Map([
  {
    templateId: 'aspire-ts-starter',
    projectName: 'AspireCliTsStarterSmoke',
    selectionText: 'Starter App (Express/React, TypeScript AppHost)',
    expectedResources: ['app', 'frontend'],
    hasTestProjectPrompt: false
  },
  {
    templateId: 'aspire-starter',
    projectName: 'AspireCliCsStarterSmoke',
    selectionText: 'Starter App (ASP.NET Core/Blazor)',
    expectedResources: ['apiservice'],
    hasTestProjectPrompt: true
  }
].map(template => [template.templateId, template]));

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
    for (const templateId of scenario.templateIds) {
      const template = templates.get(templateId);
      if (!template) {
        throw new Error(`Scenario '${scenario.id}' references unknown template '${templateId}'.`);
      }

      try {
        await scenario.run(createScenarioContext(baseContext, scenario.id, template));
      } catch (error) {
        const message = `${scenario.id} (${templateId}): ${error.message}`;
        console.warn(message);
        failures.push(message);
      }
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
