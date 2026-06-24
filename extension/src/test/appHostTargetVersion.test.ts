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
    const tempParent = join(process.cwd(), '.test-tmp');

    function makeTempDir(): string {
        mkdirSync(tempParent, { recursive: true });
        const dir = mkdtempSync(join(tempParent, 'apphost-target-version-'));
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

    suiteTeardown(() => {
        if (existsSync(tempParent)) {
            rmSync(tempParent, { recursive: true, force: true });
        }
    });

    test('summarizes no AppHost candidates as none', async () => {
        assert.strictEqual(await summarizeAppHostTargetVersions([]), 'none');
    });

    test('summarizes candidate AspireHostingVersion values from aspire ls output', async () => {
        const candidates = [
            candidate('/workspace/AppHost/AppHost.csproj', 'csharp', '13.5.0'),
            candidate('/workspace/Other/AppHost.csproj', 'csharp', '13.5.0'),
        ];

        assert.strictEqual(await summarizeAppHostTargetVersions(candidates), '13.5.0');
    });

    test('buckets multiple distinct target versions from aspire ls output', async () => {
        const candidates = [
            candidate('/workspace/AppHost/AppHost.csproj', 'csharp', '13.5.0-preview.1'),
            candidate('/workspace/Other/AppHost.csproj', 'csharp', '13.5.0-pr.18457.gabcdef'),
        ];

        assert.strictEqual(await summarizeAppHostTargetVersions(candidates), 'multiple');
    });

    test('accepts bounded prerelease target version segments', async () => {
        const candidates = [
            candidate('/workspace/Other/AppHost.csproj', 'csharp', '13.5.0-abcdefghijklmnopqrst'),
        ];

        assert.strictEqual(await summarizeAppHostTargetVersions(candidates), '13.5.0-abcdefghijklmnopqrst');
    });

    test('maps arbitrary aspire ls target versions to unknown', async () => {
        const candidates = [
            candidate('/empty/does/not/exist/apphost.ts', 'typescript', ''),
            candidate('/long/does/not/exist/apphost.ts', 'typescript', `13.5.0-${'a'.repeat(80)}`),
            candidate('/segment/does/not/exist/apphost.ts', 'typescript', '13.5.0-abcdefghijklmnopqrstu'),
            candidate('/does/not/exist/apphost.ts', 'typescript', 'C:\\Users\\me\\workspace\\AppHost.csproj'),
            candidate('/also/does/not/exist/apphost.ts', 'typescript', '<Project Sdk="Aspire.AppHost.Sdk/13.5.0">'),
            candidate('/still/does/not/exist/apphost.ts', 'typescript', '13.5.0-preview.1 with arbitrary text'),
        ];

        assert.strictEqual(await summarizeAppHostTargetVersions(candidates), 'unknown');
    });

    test('falls back to project parsing when aspire ls omits target versions', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.1" />');

        assert.strictEqual(await summarizeAppHostTargetVersions([candidate(appHostPath, 'csharp')]), '13.5.1');
    });

    test('falls back to project parsing when aspire ls returns a null target version', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk/13.5.1" />');

        assert.strictEqual(await summarizeAppHostTargetVersions([candidate(appHostPath, 'csharp', null)]), '13.5.1');
    });

    test('keeps the target version summary bounded for many distinct target versions', async () => {
        const candidates = [
            ...Array.from({ length: 100 }, (_, index) => candidate(`/workspace/AppHost${index}/AppHost.csproj`, 'csharp', `13.${index}.0`)),
        ];
        const result = await summarizeAppHostTargetVersions(candidates);

        assert.strictEqual(result, 'multiple');
        assert.ok(result.length <= 16);
    });

    test('reads the C# project SDK version', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, `<Project Sdk="Microsoft.NET.Sdk; Aspire.AppHost.Sdk/13.5.1">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.5.1');
    });

    test('ignores commented C# project SDK versions', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, `<!-- <Project Sdk="Aspire.AppHost.Sdk/1.2.3"> -->
<Project Sdk="Microsoft.NET.Sdk; Aspire.AppHost.Sdk/13.5.1">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.5.1');
    });

    test('ignores commented C# SDK element and property versions', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, `<Project Sdk="Microsoft.NET.Sdk">
  <!-- <Sdk Name="Aspire.AppHost.Sdk" Version="1.2.3" /> -->
  <!-- <AspireHostingSDKVersion>2.3.4</AspireHostingSDKVersion> -->
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.1" />
</Project>
`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.5.1');
    });

    test('rejects malformed C# project SDK versions', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk/C:\\Users\\me\\AppHost" />');

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), undefined);
    });

    test('does not use polyglot config as the version for an unversioned C# project SDK', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), undefined);
    });

    test('reads an unversioned C# project SDK version from global.json msbuild-sdks', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'global.json'), JSON.stringify({
            'msbuild-sdks': {
                'Aspire.AppHost.Sdk': '13.5.2',
            },
        }));

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.5.2');
    });

    test('summarizes a BOM-prefixed unversioned C# project SDK version from global.json msbuild-sdks', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'global.json'), `\uFEFF${JSON.stringify({
            'msbuild-sdks': {
                'Aspire.AppHost.Sdk': '13.5.2',
            },
        })}`);

        assert.strictEqual(await summarizeAppHostTargetVersions([candidate(appHostPath, 'csharp')]), '13.5.2');
    });

    test('reads an unversioned C# project Sdk element version from global.json msbuild-sdks', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project><Sdk Name="Aspire.AppHost.Sdk" /></Project>');
        writeFileSync(join(dir, 'global.json'), JSON.stringify({
            'msbuild-sdks': {
                'Aspire.AppHost.Sdk': '13.5.3',
            },
        }));

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.5.3');
    });

    test('does not use polyglot config as the version for an unversioned C# project directory', async () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'AppHost.csproj'), '<Project Sdk="Aspire.AppHost.Sdk" />');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));

        assert.strictEqual(await getAppHostTargetVersion(dir), undefined);
    });

    test('buckets multiple project SDK versions from a directory', async () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'New.AppHost.csproj'), '<Project Sdk="Aspire.AppHost.Sdk/13.6.0" />');
        writeFileSync(join(dir, 'Old.AppHost.csproj'), '<Project Sdk="Aspire.AppHost.Sdk/13.5.0" />');

        assert.strictEqual(await getAppHostTargetVersion(dir), 'multiple');
    });

    test('reads the C# single-file SDK directive version', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.cs');
        writeFileSync(appHostPath, `#:sdk Aspire.AppHost.Sdk@13.6.0-preview.1

var builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);
`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.6.0-preview.1');
    });

    test('reads the polyglot SDK version from aspire.config.json', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), `{
  // JSONC comments are allowed in Aspire config files.
  "sdk": {
    "version": "13.4.2"
  }
}`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.4.2');
        assert.strictEqual(await getAppHostTargetVersion(dir), '13.4.2');
    });

    test('summarizes a BOM-prefixed polyglot SDK version from aspire.config.json', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), `\uFEFF${JSON.stringify({ sdk: { version: '13.4.2' } })}`);

        assert.strictEqual(await summarizeAppHostTargetVersions([candidate(appHostPath, 'typescript')]), '13.4.2');
    });

    test('reads the polyglot SDK version from a directory with non-AppHost C# files', async () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'apphost.ts'), 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'helper.cs'), '#:sdk Aspire.AppHost.Sdk@13.5.0\npublic static class Helper { }');
        writeFileSync(join(dir, 'Helper.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));

        assert.strictEqual(await getAppHostTargetVersion(dir), '13.4.2');
    });

    test('reads the polyglot SDK version from JSONC config with a trailing comma', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), `{
  "sdk": {
    "version": "13.4.2",
  },
}`);

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.4.2');
    });

    test('does not read ancestor polyglot config past a nearer aspire.config.json', async () => {
        const dir = makeTempDir();
        const appHostDir = join(dir, 'src', 'AppHost');
        mkdirSync(appHostDir, { recursive: true });
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '13.4.2' } }));
        writeFileSync(join(appHostDir, 'aspire.config.json'), JSON.stringify({}));
        writeFileSync(join(appHostDir, 'apphost.ts'), 'import { aspire } from "@microsoft/aspire";');

        assert.strictEqual(await getAppHostTargetVersion(appHostDir), undefined);
    });

    test('ignores malformed polyglot SDK versions from config', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdk: { version: '../arbitrary/path' } }));

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), undefined);
    });

    test('falls back to the legacy sdkVersion config key', async () => {
        const dir = makeTempDir();
        const appHostPath = join(dir, 'apphost.ts');
        writeFileSync(appHostPath, 'import { aspire } from "@microsoft/aspire";');
        writeFileSync(join(dir, 'aspire.config.json'), JSON.stringify({ sdkVersion: '13.3.1' }));

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), '13.3.1');
    });

    test('does not read ancestor global.json past a nearer global.json', async () => {
        const dir = makeTempDir();
        const appHostDir = join(dir, 'src', 'AppHost');
        mkdirSync(appHostDir, { recursive: true });
        writeFileSync(join(dir, 'global.json'), JSON.stringify({
            'msbuild-sdks': {
                'Aspire.AppHost.Sdk': '13.5.2',
            },
        }));
        writeFileSync(join(appHostDir, 'global.json'), JSON.stringify({}));
        const appHostPath = join(appHostDir, 'AppHost.csproj');
        writeFileSync(appHostPath, '<Project Sdk="Aspire.AppHost.Sdk" />');

        assert.strictEqual(await getAppHostTargetVersion(appHostPath), undefined);
    });

    test('returns unknown when candidates have no available target version', async () => {
        assert.strictEqual(await summarizeAppHostTargetVersions([candidate('/does/not/exist/apphost.ts', 'typescript')]), 'unknown');
    });
});
