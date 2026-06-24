import * as assert from 'assert';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { getAppHostTargetVersion, summarizeAppHostTargetVersions } from '../utils/appHostTargetVersion';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';

function candidate(path: string, language: string | null, aspireHostingVersion?: string | null): CandidateAppHostDisplayInfo {
    return { path, language, status: 'buildable', aspireHostingVersion };
}

suite('appHostTargetVersion', () => {
    const tempDirs: string[] = [];

    function makeTempDir(): string {
        const parent = join(process.cwd(), '.test-tmp');
        mkdirSync(parent, { recursive: true });
        const dir = mkdtempSync(join(parent, 'apphost-target-version-'));
        tempDirs.push(dir);
        return dir;
    }

    teardown(() => {
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                rmSync(dir, { recursive: true, force: true });
            }
        }
        tempDirs.length = 0;
    });

    test('summarizes no AppHost candidates as none', () => {
        assert.strictEqual(summarizeAppHostTargetVersions([]), 'none');
    });

    test('summarizes candidate AspireHostingVersion values from aspire ls output', () => {
        const candidates = [
            candidate('/workspace/AppHost/AppHost.csproj', 'csharp', '13.5.0'),
            candidate('/workspace/Other/AppHost.csproj', 'csharp', '13.5.0'),
        ];

        assert.strictEqual(summarizeAppHostTargetVersions(candidates), '13.5.0');
    });

    test('summarizes multiple distinct target versions deterministically', () => {
        const candidates = [
            candidate('/workspace/New/AppHost.csproj', 'csharp', '13.6.0'),
            candidate('/workspace/Old/AppHost.csproj', 'csharp', '13.5.0'),
        ];

        assert.strictEqual(summarizeAppHostTargetVersions(candidates), '13.5.0,13.6.0');
    });

    test('reads the C# project SDK version', () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, `<Project Sdk="Microsoft.NET.Sdk; Aspire.AppHost.Sdk/13.5.1">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);

        assert.strictEqual(getAppHostTargetVersion(appHostPath), '13.5.1');
    });

    test('does not use polyglot config as the version for an unversioned C# project SDK', () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));

        assert.strictEqual(getAppHostTargetVersion(appHostPath), undefined);
    });

    test('does not use polyglot config as the version for an unversioned C# project directory', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'AppHost.csproj'), '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));

        assert.strictEqual(getAppHostTargetVersion(dir), undefined);
    });

    test('reads the C# single-file SDK directive version', () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.cs');
        writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@13.6.0-preview.1

var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);
`);

        assert.strictEqual(getAppHostTargetVersion(appHostPath), '13.6.0-preview.1');
    });

    test('reads the polyglot SDK version from aspire.config.json', () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), `{
  // JSONC comments are allowed in Aspire config files.
  "sdk": {
    "version": "13.4.2"
  }
}`);

        assert.strictEqual(getAppHostTargetVersion(appHostPath), '13.4.2');
        assert.strictEqual(getAppHostTargetVersion(dir), '13.4.2');
    });

    test('falls back to the legacy sdkVersion config key', () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdkVersion: '13.3.1' }));

        assert.strictEqual(getAppHostTargetVersion(appHostPath), '13.3.1');
    });

    test('returns unknown when candidates have no available target version', () => {
        assert.strictEqual(summarizeAppHostTargetVersions([candidate('/does/not/exist/apphost.ts', 'typescript')]), 'unknown');
    });
});
