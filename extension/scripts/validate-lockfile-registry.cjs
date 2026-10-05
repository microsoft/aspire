const fs = require('fs');

const internalFeed = 'pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm';
const lockfilePath = 'yarn.lock';

const bad = fs.readFileSync(lockfilePath, 'utf8')
  .split(/\r?\n/)
  .filter(line => /^\s*resolved\s+"/.test(line))
  .filter(line => !line.includes(internalFeed));

if (bad.length) {
  throw new Error(`extension/${lockfilePath} contains resolved entries outside the internal dotnet-public-npm feed. Regenerate it through the internal feed before restoring. First offender -> ${bad[0]}`);
}
