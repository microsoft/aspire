'use strict';

const fs = require('node:fs');
const path = require('node:path');
const Spec = require('mocha/lib/reporters/spec');
const { inherits } = require('mocha/lib/utils');
const constants = require('mocha/lib/runner').constants;

const {
  EVENT_RUN_END,
  EVENT_TEST_END,
  EVENT_TEST_FAIL,
  EVENT_TEST_PASS,
  EVENT_TEST_PENDING,
} = constants;

function E2eMochaReporter(runner, options = {}) {
  Spec.call(this, runner, options);

  const tests = [];
  const pending = [];
  const failures = [];
  const passes = [];
  const output = options.reporterOption?.output;

  runner.on(EVENT_TEST_END, test => tests.push(test));
  runner.on(EVENT_TEST_PASS, test => passes.push(test));
  runner.on(EVENT_TEST_FAIL, test => failures.push(test));
  runner.on(EVENT_TEST_PENDING, test => pending.push(test));

  runner.once(EVENT_RUN_END, () => {
    if (!output) {
      return;
    }

    const results = {
      stats: this.stats,
      tests: tests.map(cleanTest),
      pending: pending.map(cleanTest),
      failures: failures.map(cleanTest),
      passes: passes.map(cleanTest),
    };

    runner.testResults = results;
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, JSON.stringify(results, null, 2));
  });
}

inherits(E2eMochaReporter, Spec);

module.exports = E2eMochaReporter;

function cleanTest(test) {
  let err = test.err || {};
  if (err instanceof Error) {
    err = errorToJson(err);
  }

  return {
    title: test.title,
    fullTitle: test.fullTitle(),
    file: test.file,
    duration: test.duration,
    currentRetry: test.currentRetry(),
    speed: test.speed,
    err: cleanCycles(err),
  };
}

function errorToJson(error) {
  const result = {};
  for (const key of Object.getOwnPropertyNames(error)) {
    result[key] = error[key];
  }

  return result;
}

function cleanCycles(value) {
  const seen = [];
  return JSON.parse(JSON.stringify(value, (_key, item) => {
    if (item && typeof item === 'object') {
      if (seen.includes(item)) {
        return String(item);
      }

      seen.push(item);
    }

    return item;
  }));
}
