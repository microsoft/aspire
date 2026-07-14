// Writes the current git commit SHA to .version for extension build identification.

const { execSync } = require('child_process');
const { writeFileSync } = require('fs');
const { join } = require('path');

const versionFile = join(__dirname, '..', '.version');

let sha = 'unknown';
try {
    sha = execSync('git rev-parse HEAD', { encoding: 'utf8' }).trim();
} catch {
    // git not available or not in a repo — fall back to 'unknown'
}

writeFileSync(versionFile, sha);
console.log(`.version written: ${sha}`);
