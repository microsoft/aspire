'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const ts = require('typescript');

const root = '.stress';
const implementation = 'out/data/appHostCliRunner.js';
const baseline = '8aadc2b05baec78e3dd10062759cbabb39d670f4';
const trials = 20;
const names = [
  'drains one-shot JSON output after process exit before completing on close',
  'retains failure diagnostics written after process exit',
  'drains buffered output from a real subprocess after it exits',
];
fs.mkdirSync(root, { recursive: true });

switch (process.argv[2]) {
  case 'baseline': {
    fs.copyFileSync(implementation, path.join(root, 'fixed.js'));
    const source = execFileSync('git', ['--no-pager', 'show', `${baseline}:extension/src/data/appHostCliRunner.ts`], { encoding: 'utf8' });
    const configFile = ts.readConfigFile('tsconfig.json', ts.sys.readFile);
    if (configFile.error) {
      throw new Error(ts.flattenDiagnosticMessageText(configFile.error.messageText, '\n'));
    }
    const config = ts.parseJsonConfigFileContent(configFile.config, ts.sys, process.cwd());
    if (config.errors.length) {
      throw new Error(config.errors.map(error => ts.flattenDiagnosticMessageText(error.messageText, '\n')).join('\n'));
    }
    fs.writeFileSync(implementation, ts.transpileModule(source, { compilerOptions: config.options }).outputText);
    fs.writeFileSync('out/test/repeated-cli-output.js', `
      for (let i = 1; i <= ${trials}; i++) {
        suite('trial ' + i, () => {
          delete require.cache[require.resolve('./appHostCliRunner.test.js')];
          require('./appHostCliRunner.test.js');
        });
      }
    `);
    fs.writeFileSync('out/test/cli-output-reporter.cjs', `
      const fs = require('node:fs');
      const Spec = require('mocha').reporters.Spec;
      module.exports = class extends Spec {
        constructor(runner, options) {
          super(runner, options);
          const results = [];
          runner.on('pass', test => results.push({ name: test.fullTitle(), outcome: 'pass' }));
          runner.on('fail', (test, error) => results.push({
            name: test.fullTitle(), outcome: 'fail', error: error.name, message: error.message,
          }));
          runner.on('pending', test => results.push({ name: test.fullTitle(), outcome: 'pending' }));
          runner.once('end', () => fs.writeFileSync(
            process.env.ASPIRE_CLI_STRESS_REPORT, JSON.stringify(results, null, 2)));
        }
      };
    `);
    break;
  }
  case 'fixed':
    fs.copyFileSync(path.join(root, 'fixed.js'), implementation);
    break;
  case 'assess-baseline':
  case 'assess-fixed': {
    const variant = process.argv[2].replace('assess-', '');
    const results = JSON.parse(fs.readFileSync(path.join(root, `${variant}.json`), 'utf8'));
    if (results.length !== names.length * trials) {
      throw new Error(`Expected ${names.length * trials} executed tests, found ${results.length}`);
    }
    for (const name of names) {
      const matching = results.filter(result => result.name.endsWith(name));
      if (matching.length !== trials) {
        throw new Error(`Expected ${trials} trials for "${name}", found ${matching.length}`);
      }
      for (const result of matching) {
        if (variant === 'baseline' && (result.outcome !== 'fail' || result.error !== 'AssertionError')) {
          throw new Error(`Baseline must reproduce the output assertion, not a different error: ${JSON.stringify(result)}`);
        }
        if (variant === 'fixed' && result.outcome !== 'pass') {
          throw new Error(`Fixed output regression failed: ${JSON.stringify(result)}`);
        }
      }
    }
    console.log(`${variant}: ${trials} trials of each of ${names.length} lifecycle regressions confirmed`);
    break;
  }
  default:
    throw new Error(`Unknown mode: ${process.argv[2]}`);
}
