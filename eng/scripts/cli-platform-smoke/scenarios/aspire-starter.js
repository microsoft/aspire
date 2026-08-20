'use strict';

const { runStarterScenario } = require('../lib/starter-scenario');

module.exports = {
  templateId: 'aspire-starter',
  async run(context) {
    await runStarterScenario(
      {
        templateId: 'aspire-starter',
        projectName: 'AspireCliCsStarterSmoke',
        expectedResources: ['apiservice'],
        hasTestProjectPrompt: true
      },
      context);
  }
};
