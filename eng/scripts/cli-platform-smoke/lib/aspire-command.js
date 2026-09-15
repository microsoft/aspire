'use strict';

const { getShellSpec } = require('./shell-spec');

function buildAspireCommand(prefix, args) {
  const shell = getShellSpec();
  return `${prefix} ${args.map(arg => shell.quote(arg)).join(' ')}`.trim();
}

module.exports = {
  buildAspireCommand
};
