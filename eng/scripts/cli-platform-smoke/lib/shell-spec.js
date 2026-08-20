'use strict';

function getShellSpec() {
  if (process.platform === 'win32') {
    return {
      file: 'pwsh',
      args: ['-NoLogo', '-NoProfile'],
      enterKey: '\r',
      quote: quotePowerShell,
      wrapCommand(command, sentinel) {
        return `${command}; Write-Output ('${sentinel}:' + $LASTEXITCODE)`;
      }
    };
  }

  return {
    file: 'bash',
    args: ['--noprofile', '--norc'],
    enterKey: '\r',
    quote: quoteBash,
    wrapCommand(command, sentinel) {
      return `${command}; printf '\\n${sentinel}:%s\\n' $?`;
    }
  };
}

function quoteBash(value) {
  return `'${String(value).replace(/'/g, `'\\''`)}'`;
}

function quotePowerShell(value) {
  return `'${String(value).replace(/'/g, `''`)}'`;
}

function stripAnsi(value) {
  return value.replace(/\u001b\[[0-9;?]*[A-Za-z]/g, '');
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

module.exports = {
  delay,
  escapeRegExp,
  getShellSpec,
  stripAnsi
};
