'use strict';

const { runStarterScenario } = require('../lib/starter-scenario');

module.exports = {
  templateId: 'aspire-ts-starter',
  async run(context) {
    await runStarterScenario(
      {
        templateId: 'aspire-ts-starter',
        projectName: 'AspireCliTsStarterSmoke',
        expectedResources: ['app', 'frontend'],
        hasTestProjectPrompt: false
      },
      context);
  }
};
