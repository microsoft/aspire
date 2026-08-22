'use strict';

function getShellSpec() {
  if (process.platform === 'win32') {
    return {
      // node-pty's Windows path resolver requires the executable extension.
      file: 'pwsh.exe',
      args: ['-NoLogo', '-NoProfile'],
      enterKey: '\r',
      quote: quotePowerShell,
      wrapCommand(command, sentinel) {
        // Cmdlets such as the shell readiness probe don't set $LASTEXITCODE, while native
        // commands do. Normalize both paths so every command emits a numeric completion marker.
        return [
          '$LASTEXITCODE = $null',
          command,
          '$aspireSmokeSucceeded = $?',
          '$aspireSmokeExitCode = if ($aspireSmokeSucceeded) { 0 } elseif ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 1 }',
          `Write-Output ('${sentinel}:' + $aspireSmokeExitCode)`
        ].join('; ');
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
