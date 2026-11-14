import * as assert from 'assert';
import * as path from 'path';

const extensionRoot = path.resolve(__dirname, '..', '..');
const { selectAppHostSdkVersion } = require(path.join(extensionRoot, 'scripts', 'e2e-apphost-sdk-version.js')) as {
    selectAppHostSdkVersion(cliVersion: string, availablePackageVersions: string[]): string;
};

suite('E2E AppHost SDK version selection', () => {
    test('uses the CLI version when it is available locally', () => {
        assert.strictEqual(selectAppHostSdkVersion('17.0.0', ['17.0.0', '17.0.0-ci']), '17.0.0');
    });

    test('uses the local package version when the CLI version is unavailable', () => {
        assert.strictEqual(selectAppHostSdkVersion('17.0.0', ['17.0.0-ci']), '17.0.0-ci');
    });

    test('uses the CLI version when no local package versions are available', () => {
        assert.strictEqual(selectAppHostSdkVersion('17.0.0', []), '17.0.0');
    });
});
