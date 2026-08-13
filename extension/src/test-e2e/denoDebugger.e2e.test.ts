import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { waitForRepositoryIdle, waitForSelectedWorkspaceAppHost } from './helpers/assertions';
import { createEmptyAppHostProject, executeE2eControlCommand, removeGeneratedProject, restoreWorkspaceAppHostConfig, runE2eTeardown, stopPrimaryAppHostIfRunning, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { getRepoRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire Deno debugger E2E', function () {
    this.timeout(420000);

    const projectName = 'AspireE2E.DenoDebugger';
    teardown(async () => {
        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'clearBreakpoints' }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => removeGeneratedProject(projectName),
            () => restoreWorkspaceAppHostConfig(),
        ], 'Deno debugger E2E teardown failed.');
    });

    test('attaches js-debug to a Deno resource and hits a TypeScript breakpoint', async () => {
        const projectRoot = await createEmptyAppHostProject(projectName);
        const appHostPath = path.join(projectRoot, 'apphost.cs');

        const denoAppDirectory = path.join(projectRoot, 'deno-app');
        fs.mkdirSync(denoAppDirectory, { recursive: true });
        const denoSourcePath = path.join(denoAppDirectory, 'main.ts');
        writeFileWithRetry(denoSourcePath, `const message = "deno debugger breakpoint hit";
console.log(message);
Deno.serve({ hostname: "127.0.0.1", port: Number(Deno.env.get("PORT") ?? "8000") }, () => new Response(message));
`);

        const originalAppHostSource = fs.readFileSync(appHostPath, 'utf8');
        const javascriptHostingProject = path.relative(projectRoot, path.join(getRepoRoot(), 'src', 'Aspire.Hosting.JavaScript', 'Aspire.Hosting.JavaScript.csproj')).replace(/\\/g, '/');
        writeFileWithRetry(path.join(projectRoot, 'Directory.Build.targets'), `<Project>
  <ItemGroup>
    <ProjectReference Update="${javascriptHostingProject}" IsAspireProjectResource="false" />
  </ItemGroup>
</Project>
`);
        const appHostWithLocalJavaScriptHosting = originalAppHostSource.replace(
            /(#:sdk [^\r\n]+)(\r?\n)/,
            `$1$2#:property NoWarn=ASPIREDENO001$2#:project ${javascriptHostingProject}$2`);
        assert.notStrictEqual(appHostWithLocalJavaScriptHosting, originalAppHostSource, 'Expected generated AppHost source to contain an SDK directive.');

        const appHostSource = appHostWithLocalJavaScriptHosting.replace(
            'builder.Build().Run();',
            `builder.AddDenoApp("deno", "deno-app", "main.ts")
    .WithHttpEndpoint(env: "PORT")
    .WithExplicitStart();

builder.Build().Run();`);
        assert.notStrictEqual(appHostSource, appHostWithLocalJavaScriptHosting, 'Expected generated AppHost source to contain builder.Build().Run().');
        writeFileWithRetry(appHostPath, appHostSource);
        writeWorkspaceAppHostConfigForPath(appHostPath);

        await openAspireView();
        await waitForRepositoryIdle();
        await executeE2eControlCommand({ name: 'refreshAppHosts' });
        await waitForSelectedWorkspaceAppHost(appHostPath, 180000);

        const proof = await executeE2eControlCommand({
            name: 'proveDenoResourceDebugging',
            appHostPath,
            resourceName: 'deno',
            sourcePath: denoSourcePath,
            breakpointLine: 1,
            timeoutMs: 300000,
        }, { timeoutMs: 330000 });

        assert.strictEqual(proof.status, 'applied');
        assert.ok(isRecord(proof.result), `Expected Deno debug proof result. Actual result: ${JSON.stringify(proof.result)}`);
        assert.strictEqual(proof.result.proof, 'aspire-deno-resource-debug-breakpoint-hit');
        assert.strictEqual(proof.result.resourceName, 'deno');
    });
});

function isRecord(value: unknown): value is Record<string, unknown> {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}
