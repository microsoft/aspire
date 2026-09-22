'use strict';

const fs = require('fs');
const path = require('path');

function createScenarioContext(baseContext, scenario) {
  const scenarioRoot = path.join(baseContext.validationRoot, scenario.id);
  const diagnosticsDir = path.join(scenarioRoot, 'diagnostics');
  const projectRoot = path.join(scenarioRoot, scenario.projectName);

  fs.rmSync(scenarioRoot, { recursive: true, force: true });
  fs.mkdirSync(diagnosticsDir, { recursive: true });

  return {
    ...baseContext,
    diagnosticsDir,
    projectRoot,
    scenarioRoot
  };
}

module.exports = {
  createScenarioContext
};
