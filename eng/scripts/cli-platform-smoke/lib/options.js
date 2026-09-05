'use strict';

const os = require('os');
const path = require('path');
const validationDirectoryName = 'aspire-cli-starter-validation';

function defaultValidationRoot() {
  if (process.env.RUNNER_TEMP && process.env.RUNNER_TEMP.trim().length > 0) {
    return path.join(process.env.RUNNER_TEMP, validationDirectoryName);
  }

  return path.join(os.tmpdir(), validationDirectoryName);
}

function resolveValidationRoot(validationRoot) {
  if (validationRoot) {
    return path.resolve(validationRoot, validationDirectoryName);
  }

  return path.resolve(defaultValidationRoot());
}

function parseArgs(argv) {
  const options = {
    prNumber: null,
    maxStartupSeconds: 120,
    resourceReadyTimeoutSeconds: 120,
    validationRoot: '',
    channel: '',
    aspireCommand: ''
  };

  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];

    switch (argument) {
      case '--pr-number':
        options.prNumber = parseRequiredInt(argv[++index], argument);
        break;

      case '--max-startup-seconds':
        options.maxStartupSeconds = parseRequiredInt(argv[++index], argument);
        break;

      case '--resource-ready-timeout-seconds':
        options.resourceReadyTimeoutSeconds = parseRequiredInt(argv[++index], argument);
        break;

      case '--validation-root':
        options.validationRoot = parseRequiredValue(argv[++index], argument);
        break;

      case '--channel':
        options.channel = parseRequiredValue(argv[++index], argument);
        break;

      case '--aspire-command':
        options.aspireCommand = parseRequiredValue(argv[++index], argument);
        break;

      default:
        throw new Error(`Unknown argument '${argument}'.`);
    }
  }

  if (!options.prNumber) {
    throw new Error('The --pr-number argument is required.');
  }

  return options;
}

function parseRequiredInt(value, argumentName) {
  const parsed = Number.parseInt(parseRequiredValue(value, argumentName), 10);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`Argument '${argumentName}' must be a positive integer.`);
  }

  return parsed;
}

function parseRequiredValue(value, argumentName) {
  if (!value || value.trim().length === 0) {
    throw new Error(`Argument '${argumentName}' requires a non-empty value.`);
  }

  return value;
}

module.exports = {
  defaultValidationRoot,
  parseArgs,
  resolveValidationRoot
};
