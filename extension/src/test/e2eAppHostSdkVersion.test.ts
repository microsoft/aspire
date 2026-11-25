import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';
import { runInNewContext } from 'vm';

const extensionRoot = path.resolve(__dirname, '..', '..');
const {
    createBrowserDebuggerAppHostProject,
    resolveAppHostSdkVersion,
    selectCompanionPackageVersion,
} = require(path.join(extensionRoot, 'scripts', 'e2e-apphost-sdk-version.js')) as {
    createBrowserDebuggerAppHostProject(appHostSdkVersion: string, blazorPackageVersion: string): string;
    selectCompanionPackageVersion(appHostSdkVersion: string, availablePackageVersions: string[]): string;
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
        const project = createBrowserDebuggerAppHostProject(resolution.version, selectCompanionPackageVersion(resolution.version, ['17.0.0-ci']));

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

    test('uses the local prerelease Blazor package when release branding stabilizes the AppHost SDK', () => {
        const resolution = resolveAppHostSdkVersion({ status: 0, stdout: '17.0.1\n' }, () => ['17.0.1'], () => 'fallback', { ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION: '17.0.1' });
        const blazorPackageVersion = selectCompanionPackageVersion(resolution.version, ['17.0.1-ci', '17.0.0']);
        const project = createBrowserDebuggerAppHostProject(resolution.version, blazorPackageVersion);

        assert.strictEqual(blazorPackageVersion, '17.0.1-ci');
        assert.match(project, /^<Project Sdk="Aspire\.AppHost\.Sdk\/17\.0\.1">/);
        assert.match(project, /<PackageReference Include="Aspire\.Hosting\.Blazor" Version="17\.0\.1-ci" \/>/);
        assert.match(project, /<PackageReference Include="Aspire\.Hosting\.Dotnet" Version="17\.0\.1" \/>/);
    });

    test('keeps the AppHost SDK version for companion packages built with the same version', () => {
        assert.strictEqual(selectCompanionPackageVersion('17.1.0-preview.1', ['17.1.0-preview.1', '17.0.1']), '17.1.0-preview.1');
        assert.strictEqual(selectCompanionPackageVersion('17.0.1', []), '17.0.1');
    });

    test('retains the exact pin for a PR SDK whose companion is missing instead of borrowing another build', () => {
        const project = writeFixtureWithPackages([
            'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1-pr.22586.g789ea272.nupkg',
            'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1.nupkg',
        ], '17.0.1-pr.22586.g789ea272');

        assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1-pr.22586.g789ea272', '17.0.1-pr.22586.g789ea272'));
    });

    test('uses the repository build companion instead of a cached same-patch PR package', () => {
        const project = writeFixtureWithPackages([
            'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
            'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            'home/.aspire/hives/pr-unrelated/packages/Aspire.AppHost.Sdk.17.0.1.nupkg',
            'home/.aspire/hives/pr-unrelated/packages/Aspire.Hosting.Blazor.17.0.1-pr.22500.1.nupkg',
        ]);

        assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1', '17.0.1-ci'));
    });

    test('uses a staged hive companion when no repository package output exists', () => {
        const project = writeFixtureWithPackages([
            'home/.aspire/hives/pr-unrelated/packages/Aspire.Hosting.Blazor.17.0.1-pr.22500.1.nupkg',
            'home/.aspire/hives/staging/packages/Aspire.AppHost.Sdk.17.0.1.nupkg',
            'home/.aspire/hives/staging/packages/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
        ]);

        assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1', '17.0.1-ci'));
    });

    test('uses the release companion instead of a leftover PR package in the same directory', () => {
        const project = writeFixtureWithPackages([
            'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
            'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1-pr.22586.g8683dcc9.nupkg',
        ]);

        assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1', '17.0.1-ci'));
    });

    test('preserves an exact companion match even when it is in a different source', () => {
        const project = writeFixtureWithPackages([
            'artifacts/packages/Release/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
            'artifacts/packages/Release/Shipping/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            'home/.aspire/hives/stable/packages/Aspire.Hosting.Blazor.17.0.1.nupkg',
        ]);

        assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1', '17.0.1'));
    });

    for (const { scenario, packages } of [
        {
            scenario: 'a missing companion',
            packages: [
                'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
                'home/.aspire/hives/pr-unrelated/packages/Aspire.Hosting.Blazor.17.0.1-pr.22500.1.nupkg',
            ],
        },
        {
            scenario: 'only a leftover PR companion beside the SDK',
            packages: [
                'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
                'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.1-pr.22586.g8683dcc9.nupkg',
            ],
        },
        {
            scenario: 'a companion only in a nested source below the SDK',
            packages: [
                'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
                'artifacts/nugets/Debug/Shipping/nested/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            ],
        },
        {
            scenario: 'no local SDK',
            packages: [
                'home/.aspire/hives/pr-unrelated/packages/Aspire.Hosting.Blazor.17.0.1-ci.nupkg',
            ],
        },
        {
            scenario: 'a different patch',
            packages: [
                'artifacts/nugets/Debug/Shipping/Aspire.AppHost.Sdk.17.0.1.nupkg',
                'artifacts/nugets/Debug/Shipping/Aspire.Hosting.Blazor.17.0.0-ci.nupkg',
            ],
        },
    ]) {
        test(`retains the exact restore pin with ${scenario} instead of borrowing an unrelated package`, () => {
            const project = writeFixtureWithPackages(packages);

            assert.strictEqual(project, createBrowserDebuggerAppHostProject('17.0.1', '17.0.1'));
        });
    }
});

function writeFixtureWithPackages(packagePaths: string[], appHostSdkVersion = '17.0.1'): string {
    const testArtifactsRoot = path.join(extensionRoot, '.test-artifacts', 'unit');
    fs.mkdirSync(testArtifactsRoot, { recursive: true });
    const root = fs.mkdtempSync(path.join(testArtifactsRoot, 'companion-packages-'));
    try {
        for (const packagePath of packagePaths) {
            const filePath = path.join(root, packagePath);
            fs.mkdirSync(path.dirname(filePath), { recursive: true });
            fs.writeFileSync(filePath, '');
        }

        const runner = fs.readFileSync(path.join(extensionRoot, 'scripts', 'run-e2e.js'), 'utf8');
        const source = ts.createSourceFile('run-e2e.js', runner, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
        const functionNames = new Set([
            'writeBrowserDebuggerAppHostProject',
            'getAvailableCompanionPackageVersions',
            'getAvailablePackageVersions',
            'getLocalPackageSourceDirectories',
            'getDirectoriesContainingPackages',
            'comparePackageVersionsDescending',
        ]);
        const declarations = source.statements.filter((statement): statement is ts.FunctionDeclaration =>
            ts.isFunctionDeclaration(statement) && statement.name !== undefined && functionNames.has(statement.name.text));
        assert.strictEqual(declarations.length, functionNames.size);

        // Exercise the real discovery and writer against isolated package filenames, without
        // launching VS Code or downloading the CLI. Shipping and flat hive layouts mirror CI.
        runInNewContext(`${declarations.map(declaration => declaration.getText(source)).join('\n')}
writeBrowserDebuggerAppHostProject('AppHost', ${JSON.stringify(appHostSdkVersion)});`, {
            fs,
            path,
            os: { homedir: () => path.join(root, 'home') },
            repoRoot: root,
            workspaceRoot: path.join(root, 'workspace'),
            csharpFileHeader: '',
            console: { log: () => undefined },
            createBrowserDebuggerAppHostProject,
            selectCompanionPackageVersion,
        });

        return fs.readFileSync(path.join(root, 'workspace', 'AppHost', 'AppHost.csproj'), 'utf8');
    }
    finally {
        fs.rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    }
}
