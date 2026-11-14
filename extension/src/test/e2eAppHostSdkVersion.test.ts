import * as assert from 'assert';
import * as path from 'path';

const extensionRoot = path.resolve(__dirname, '..', '..');
const {
    createBrowserDebuggerAppHostProject,
    resolveAppHostSdkVersion,
} = require(path.join(extensionRoot, 'scripts', 'e2e-apphost-sdk-version.js')) as {
    createBrowserDebuggerAppHostProject(appHostSdkVersion: string): string;
    resolveAppHostSdkVersion(versionResult: { status: number; stdout: string }, getAvailablePackageVersions: () => string[], getFallbackVersion: () => string, environment?: NodeJS.ProcessEnv): { version: string; usedLocalVersion: boolean };
};

suite('E2E AppHost SDK version selection', () => {
    test('uses the CLI version when it is available locally', () => {
        const result = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.0\n' }, () => ['17.0.0', '17.0.0-ci'], () => 'fallback', {});
        assert.strictEqual(result.version, '17.0.0');
        assert.strictEqual(result.usedLocalVersion, false);
    });

    test('uses the local package version when the CLI version is unavailable', () => {
        const result = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.0\n' }, () => ['17.0.0-ci'], () => 'fallback', {});
        assert.strictEqual(result.version, '17.0.0-ci');
        assert.strictEqual(result.usedLocalVersion, true);
    });

    test('uses the local prerelease version throughout the browser debugger fixture when CI has a stable CLI', () => {
        const resolution = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.0\n' }, () => ['17.0.0-ci'], () => 'fallback', { CI: 'true' });
        const project = createBrowserDebuggerAppHostProject(resolution.version);

        assert.strictEqual(resolution.version, '17.0.0-ci');
        assert.strictEqual(resolution.usedLocalVersion, true);
        assert.match(project, /^<Project Sdk="Aspire\.AppHost\.Sdk\/17\.0\.0-ci">/);
        assert.match(project, /<PackageReference Include="Aspire\.Hosting\.Blazor" Version="17\.0\.0-ci" \/>/);
        assert.match(project, /<PackageReference Include="Aspire\.Hosting\.Dotnet" Version="17\.0\.0-ci" \/>/);
    });

    test('uses the CLI version when no local package versions are available', () => {
        const result = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.0\n' }, () => [], () => 'fallback', {});
        assert.strictEqual(result.version, '17.0.0');
        assert.strictEqual(result.usedLocalVersion, false);
    });

    test('uses the explicit fixture version override', () => {
        const result = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.0\n' }, () => ['17.0.0-ci'], () => 'fallback', { ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION: '16.0.0' });
        assert.strictEqual(result.version, '16.0.0');
        assert.strictEqual(result.usedLocalVersion, false);
    });
});
