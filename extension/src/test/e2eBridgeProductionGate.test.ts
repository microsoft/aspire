import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

type WebpackConfigFactory = ((env: unknown, argv: { mode?: string }) => Array<{ plugins: unknown[] }>) & {
    e2eBridgeRequestPattern: RegExp;
    e2eBridgeProductionStub: string;
};

const extensionRoot = path.resolve(__dirname, '..', '..');
const loadWebpackConfig = (): WebpackConfigFactory => require(path.join(extensionRoot, 'webpack.config.js')) as WebpackConfigFactory;

/**
 * `e2eStateFileBridge.ts` is a test control channel that registers a wildcard debug adapter tracker
 * and executes commands read from a file path in an environment variable. `extension.ts` imports it
 * unconditionally, so it has to be removed at build time rather than gated at runtime, or it ships
 * inside the published extension.
 */
suite('E2E bridge production gate', () => {
    test('replaces the E2E bridge in production builds', () => {
        const configure = loadWebpackConfig();

        const [productionConfig] = configure({}, { mode: 'production' });

        assert.strictEqual(productionConfig.plugins.length, 1);
        assert.strictEqual((productionConfig.plugins[0] as object).constructor.name, 'NormalModuleReplacementPlugin');
    });

    test('keeps the E2E bridge in development and E2E builds', () => {
        const configure = loadWebpackConfig();

        // `yarn compile` passes no mode, which is what the E2E runner builds with and what has to
        // keep driving the real bridge.
        assert.deepStrictEqual(configure({}, {}).map(config => config.plugins), [[]]);
        assert.deepStrictEqual(configure({}, { mode: 'none' }).map(config => config.plugins), [[]]);
    });

    test('does not accumulate plugins across repeated configuration calls', () => {
        const configure = loadWebpackConfig();

        configure({}, { mode: 'production' });

        assert.strictEqual(configure({}, { mode: 'production' })[0].plugins.length, 1);
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
