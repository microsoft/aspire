const { spawnSync } = require('child_process');
const { existsSync, statSync } = require('fs');
const { join } = require('path');

const extensionRoot = join(__dirname, '..');
const nodeModulesPath = join(extensionRoot, 'node_modules');
const installMarkerPath = join(nodeModulesPath, '.package-lock.json');
const dependencyInputs = [
    join(extensionRoot, 'package.json'),
    join(extensionRoot, 'package-lock.json')
];

function getModifiedTime(path) {
    try {
        return statSync(path).mtimeMs;
    } catch {
        return 0;
    }
}

const installMarkerModifiedTime = getModifiedTime(installMarkerPath);
const shouldInstall = !existsSync(nodeModulesPath)
    || installMarkerModifiedTime === 0
    || dependencyInputs.some(path => getModifiedTime(path) > installMarkerModifiedTime);

if (!shouldInstall) {
    console.log('Extension dependencies are already installed.');
    process.exit(0);
}

console.log('Installing extension dependencies with npm ci...');

const result = spawnSync('npm', ['ci', '--no-audit', '--no-fund'], {
    cwd: extensionRoot,
    shell: process.platform === 'win32',
    stdio: 'inherit'
});

if (result.error) {
    console.error(`Failed to run npm ci: ${result.error.message}`);
    process.exit(1);
}

process.exit(result.status ?? 1);