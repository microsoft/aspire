import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getBackgroundCliEnvironment, getEnvironmentForChildProcess } from '../utils/environment';

suite('environment', () => {
    const enableBridgeVariable = 'ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE';
    const e2eNuGetPackagesVariable = 'ASPIRE_EXTENSION_E2E_NUGET_PACKAGES';
    let originalEnableBridge: string | undefined;
    let originalE2eNuGetPackages: string | undefined;
    let originalNuGetPackages: string | undefined;

    setup(() => {
        originalEnableBridge = process.env[enableBridgeVariable];
        originalE2eNuGetPackages = process.env[e2eNuGetPackagesVariable];
        originalNuGetPackages = process.env.NUGET_PACKAGES;
    });

    teardown(() => {
        sinon.restore();
        restoreEnvironmentVariable(enableBridgeVariable, originalEnableBridge);
        restoreEnvironmentVariable(e2eNuGetPackagesVariable, originalE2eNuGetPackages);
        restoreEnvironmentVariable('NUGET_PACKAGES', originalNuGetPackages);
    });

    for (const enabled of [false, true]) {
        test(`background file logging is ${enabled ? 'enabled explicitly' : 'disabled by default'}`, () => {
            const get = sinon.stub().withArgs('enableAspireCliDebugLogging', false).returns(enabled);
            sinon.stub(vscode.workspace, 'getConfiguration').withArgs('aspire').returns({ get } as unknown as vscode.WorkspaceConfiguration);

            assert.deepStrictEqual(getBackgroundCliEnvironment(), [
                { name: 'ASPIRE_CLI_DISABLE_FILE_LOGGING', value: String(!enabled) },
            ]);
        });
    }

    test('maps the E2E NuGet cache into child environments when the bridge is enabled', () => {
        process.env[enableBridgeVariable] = 'true';
        process.env[e2eNuGetPackagesVariable] = '/isolated/e2e/packages';
        process.env.NUGET_PACKAGES = '/shared/packages';

        const environment = getEnvironmentForChildProcess();

        assert.strictEqual(environment.NUGET_PACKAGES, '/isolated/e2e/packages');
        assert.strictEqual(environment[e2eNuGetPackagesVariable], undefined);
    });

    test('does not map the E2E NuGet cache when the bridge is disabled', () => {
        delete process.env[enableBridgeVariable];
        process.env[e2eNuGetPackagesVariable] = '/isolated/e2e/packages';
        process.env.NUGET_PACKAGES = '/shared/packages';

        const environment = getEnvironmentForChildProcess();

        assert.strictEqual(environment.NUGET_PACKAGES, '/shared/packages');
        assert.strictEqual(environment[e2eNuGetPackagesVariable], undefined);
    });
});

function restoreEnvironmentVariable(name: string, value: string | undefined): void {
    if (value === undefined) {
        delete process.env[name];
    } else {
        process.env[name] = value;
    }
}
