'use strict';

const fs = require('fs');
const path = require('path');

function createScenarioContext(baseContext, scenarioId, template) {
  const scenarioRoot = path.join(baseContext.validationRoot, template.templateId, scenarioId);
  const diagnosticsDir = path.join(scenarioRoot, 'diagnostics');
  const projectRoot = path.join(scenarioRoot, template.projectName);

  fs.rmSync(scenarioRoot, { recursive: true, force: true });
  fs.mkdirSync(diagnosticsDir, { recursive: true });

  return {
    ...baseContext,
    diagnosticsDir,
    projectRoot,
    scenarioRoot,
    template
  };
}

module.exports = {
  createScenarioContext
};
