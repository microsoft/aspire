import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

type WebpackConfigFactory = ((env: unknown, argv: { mode?: string }) => Array<{ plugins: unknown[] }>) & {
    e2eBridgeRequestPattern: RegExp;
    e2eBridgeProductionStub: string;
    e2eBridgeIncludeEnvironmentVariable: string;
};

const extensionRoot = path.resolve(__dirname, '..', '..');
const loadWebpackConfig = (): WebpackConfigFactory => require(path.join(extensionRoot, 'webpack.config.js')) as WebpackConfigFactory;
const withEnvironmentVariable = (name: string, value: string | undefined, action: () => void): void => {
    const originalValue = process.env[name];

    try {
        if (value === undefined) {
            delete process.env[name];
        } else {
            process.env[name] = value;
        }
        action();
    } finally {
        if (originalValue === undefined) {
            delete process.env[name];
        } else {
            process.env[name] = originalValue;
        }
    }
};

/**
 * `e2eStateFileBridge.ts` is a test control channel that registers a wildcard debug adapter tracker
 * and executes commands read from a file path in an environment variable. `extension.ts` imports it
 * unconditionally, so it has to be removed at build time rather than gated at runtime, or it ships
 * inside the published extension.
 */
suite('E2E bridge production gate', () => {
    test('redacts every debugger environment shape captured by the E2E bridge', () => {
        const bridge = fs.readFileSync(path.join(extensionRoot, 'src', 'testing', 'e2eStateFileBridge.ts'), 'utf8');

        assert.ok(bridge.includes("if ('env' in copy)"));
        assert.ok(bridge.includes("if ('environment' in copy)"));
        assert.ok(bridge.includes("if ('environmentVariables' in copy)"));
    });

    test('replaces the E2E bridge in production builds', () => {
        const configure = loadWebpackConfig();

        withEnvironmentVariable(configure.e2eBridgeIncludeEnvironmentVariable, undefined, () => {
            const [productionConfig] = configure({}, { mode: 'production' });

            assert.strictEqual(productionConfig.plugins.length, 1);
            assert.strictEqual((productionConfig.plugins[0] as object).constructor.name, 'NormalModuleReplacementPlugin');
        });
    });

    test('keeps the E2E bridge in development builds', () => {
        const configure = loadWebpackConfig();

        // `yarn compile` passes no mode, so local development bundles keep driving the real bridge.
        assert.deepStrictEqual(configure({}, {}).map(config => config.plugins), [[]]);
        assert.deepStrictEqual(configure({}, { mode: 'none' }).map(config => config.plugins), [[]]);
    });

    test('keeps the E2E bridge in production mode when the E2E VSIX build opts in', () => {
        const configure = loadWebpackConfig();

        withEnvironmentVariable(configure.e2eBridgeIncludeEnvironmentVariable, 'true', () => {
            assert.deepStrictEqual(
                configure({}, { mode: 'production' }).map(config => config.plugins),
                [[]],
                'The E2E VSIX build packages through vscode:prepublish, so its explicit bridge opt-in must override the production stub replacement.');
        });
    });

    test('packages the E2E VSIX with the bridge opt-in and asserts the emitted bundle', () => {
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'tests.yml'), 'utf8');

        assert.ok(
            workflow.includes('ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE: "true"'),
            'The E2E VSIX package step must opt into bundling the real bridge.');
        assert.ok(
            workflow.includes('assert-extension-e2e-bridge-vsix.ps1 -VsixPath out/aspire-extension.vsix -Expected Present'),
            'The E2E VSIX package step must assert the emitted VSIX still contains the real bridge.');
    });

    test('packages the local E2E VSIX with the bridge opt-in', () => {
        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');

        assert.ok(
            runner.includes("run('corepack', ['yarn@1.22.22', 'run', 'vsce', 'package', '--pre-release', '-o', defaultVsixPath], { ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE: 'true' }, { timeout: 300000 });"),
            'The local E2E runner packages in production mode, so it must opt into bundling the real bridge.');
    });

    /**
     * The bridge-included VSIX above only proves the E2E opt-in still works; it says nothing about
     * what ships when nobody sets ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE. Without a separate assertion
     * against a VSIX packaged the same way Extension.proj packages one for real users, the
     * NormalModuleReplacementPlugin wiring in webpack.config.js could regress silently: every other
     * check in this workflow would stay green while the bridge shipped to the Marketplace.
     */
    test('packages a production VSIX without the bridge opt-in and asserts the bridge is absent', () => {
        const workflow = fs.readFileSync(path.join(extensionRoot, '..', '.github', 'workflows', 'tests.yml'), 'utf8');

        assert.ok(
            workflow.includes('corepack yarn run vsce package --pre-release -o out/aspire-extension-production.vsix'),
            'The workflow must package a second VSIX without the bridge include env var to represent the real shipping build.');
        assert.ok(
            workflow.includes('assert-extension-e2e-bridge-vsix.ps1 -VsixPath out/aspire-extension-production.vsix -Expected Absent'),
            'The E2E VSIX package step must assert the production VSIX excludes the real bridge.');
    });

    test('does not accumulate plugins across repeated configuration calls', () => {
        const configure = loadWebpackConfig();

        withEnvironmentVariable(configure.e2eBridgeIncludeEnvironmentVariable, undefined, () => {
            configure({}, { mode: 'production' });

            assert.strictEqual(configure({}, { mode: 'production' })[0].plugins.length, 1);
        });
    });

    test('matches the bridge import that extension.ts issues', () => {
        const configure = loadWebpackConfig();
        const extensionSource = fs.readFileSync(path.join(extensionRoot, 'src', 'extension.ts'), 'utf8');
        const bridgeImport = /from '(\.[^']*e2eStateFileBridge)'/.exec(extensionSource);

        assert.ok(bridgeImport, 'Expected extension.ts to import the E2E state file bridge.');
        assert.ok(
            configure.e2eBridgeRequestPattern.test(bridgeImport[1]),
            `The webpack replacement pattern must match the request extension.ts issues (${bridgeImport[1]}).`);
    });

    test('substitutes a stub that exports everything extension.ts imports from the bridge', () => {
        const configure = loadWebpackConfig();
        const stubSource = fs.readFileSync(configure.e2eBridgeProductionStub, 'utf8');
        const extensionSource = fs.readFileSync(path.join(extensionRoot, 'src', 'extension.ts'), 'utf8');
        const importedNames = /import\s*{([^}]*)}\s*from\s*'\.[^']*e2eStateFileBridge'/.exec(extensionSource)?.[1]
            .split(',')
            .map(name => name.trim())
            .filter(Boolean) ?? [];

        assert.ok(importedNames.length > 0, 'Expected extension.ts to import named bindings from the bridge.');
        assert.deepStrictEqual(
            importedNames.filter(name => !new RegExp(`export function ${name}\\b`).test(stubSource)),
            [],
            'The production stub must export every binding extension.ts imports, or the production build breaks.');
    });
});
