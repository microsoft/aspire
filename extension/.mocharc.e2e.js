'use strict';

const path = require('path');

const resultsDir = process.env.ASPIRE_EXTENSION_E2E_RESULTS_DIR || path.join('.test-results', 'e2e');
const extesterModulePath = process.env.ASPIRE_EXTENSION_E2E_EXTESTER_MODULE ?? 'vscode-extension-tester';
const { VSBrowser } = require(extesterModulePath);

function getScreenshotName(test) {
  return (test?.fullTitle?.() ?? test?.title ?? 'failed-test')
    .replace(/[^a-z0-9._-]+/gi, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 120) || 'failed-test';
}

module.exports = {
  ui: 'tdd',
  timeout: 240000,
  reporter: path.join(__dirname, 'scripts', 'e2e-mocha-reporter.cjs'),
  reporterOptions: {
    output: path.join(resultsDir, 'mocha.json'),
  },
  parallel: false,
  spec: 'out/test-e2e/**/*.e2e.test.js',
  rootHooks: {
    async afterEach() {
      if (this.currentTest?.state !== 'failed') {
        return;
      }

      try {
        await VSBrowser.instance.takeScreenshot(getScreenshotName(this.currentTest));
      }
      catch (error) {
        console.warn(`Failed to capture E2E failure screenshot: ${error instanceof Error ? error.message : String(error)}`);
      }
    },
  },
};
