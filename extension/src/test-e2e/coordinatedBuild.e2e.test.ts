import * as assert from 'assert';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { AspireResourceExtendedDebugConfiguration, ProjectLaunchConfiguration } from '../dcp/types';
import { waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceFoldersForE2E, writeFileWithRetry } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire coordinated build E2E', function () {
    this.timeout(360000);

    suiteSetup(async () => {
        await openAspireView();
        await restoreWorkspaceFoldersForE2E({ waitForExtensionHostReload: true });
    });

    for (const language of ['C#', 'F#', 'VB'] as const) {
        test(`runs and debugs an externally built Release ${language} resource with launch profiles`, async () => {
            await openAspireView();
            await waitForRepositoryIdle();

            const capabilities = (await executeE2eControlCommand({ name: 'getSupportedCapabilities' })).result as string[];
            assert.ok(capabilities.includes('project-with-external-build.v1'), 'The coordinated-build shard requires the installed C# coreclr debugger.');
            const fixture = createResourceProject(language);
            try {
                execFileSync('dotnet', ['build', fixture.projectPath, '--configuration', 'Release', '--nologo', '--property:BUILD_FLAVOR=custom'], {
                    cwd: fixture.directory,
                    env: { ...process.env, BUILD_FLAVOR: 'custom' },
                    stdio: 'pipe',
                    timeout: 180000,
                });
                // Keep Program's PDB checksum intact so Debug must bind a real source breakpoint.
                // A separate broken compile item makes any extension-owned rebuild fail.
                writeFileWithRetry(fixture.buildSentinelPath, 'this does not compile\n');

                const launchConfig: ProjectLaunchConfiguration = {
                    type: 'project-with-external-build.v1',
                    project_path: fixture.projectPath,
                    build_configuration: 'Release',
                    build_environment: { [process.platform === 'win32' ? 'build_flavor' : 'BUILD_FLAVOR']: 'custom' },
                    build_working_directory: fixture.directory,
                    suppress_build: true,
                };
                const profileSource = language === 'VB' ? 'my-project' : 'properties';
                let launchNumber = 0;
                const proveLaunch = async (debug: boolean, profileOptions: Partial<ProjectLaunchConfiguration>, expectedProfile: string, expectedArgs: string) => {
                    const readyFilePath = path.join(fixture.directory, `ready-${++launchNumber}.txt`);
                    const status = await executeE2eControlCommand({
                        name: 'proveDotnetResourceLaunch',
                        launchConfig: { ...launchConfig, ...profileOptions },
                        sourcePath: fixture.sourcePath,
                        breakpointLine: fixture.breakpointLine,
                        readyFilePath,
                        debug,
                        env: [
                            { name: 'E2E_READY_PATH', value: readyFilePath },
                            { name: 'BUILD_FLAVOR', value: 'runtime' },
                            { name: 'RUNTIME_ONLY', value: 'runtime-value' },
                        ],
                        timeoutMs: 120000,
                    }, { timeoutMs: 150000 });
                    const proof = status.result as DotnetResourceLaunchProof;

                    assert.strictEqual(proof.proof, debug ? 'dotnet-resource-breakpoint-hit' : 'dotnet-resource-run-completed');
                    assert.strictEqual(proof.configuration.type, 'coreclr');
                    assert.strictEqual(proof.configuration.isApphost, false);
                    assert.strictEqual(proof.configuration.noDebug, !debug);
                    assert.strictEqual(proof.exitCode, 0);
                    assert.deepStrictEqual(proof.launchRequests.map(request => [request.type, request.noDebug]), [['coreclr', !debug]]);
                    // Assembly.Location proves which output actually ran without exposing redacted launch arguments.
                    assert.strictEqual(proof.readiness, `Release|custom|${expectedProfile}|runtime|runtime-value|${expectedArgs}|${fixture.targetPath}`);
                    assert.ok(proof.output.join('').includes('coordinated-resource-ready'), 'Expected output from the actual resource process.');
                    if (debug) {
                        assert.ok(proof.breakpointResponses.some(response => response.success), 'Expected the adapter to accept a source breakpoint.');
                        assert.strictEqual(proof.breakpointHit?.reason, 'breakpoint');
                        assert.strictEqual(proof.breakpointHit?.line, fixture.breakpointLine + 1);
                        assert.strictEqual(path.resolve(proof.breakpointHit!.sourcePath).toLowerCase(), path.resolve(fixture.sourcePath).toLowerCase());
                    } else {
                        assert.strictEqual(proof.breakpointHit, undefined);
                    }
                    assert.strictEqual(fs.existsSync(path.join(fixture.directory, 'bin', 'wrong')), false);
                };

                await proveLaunch(false, {}, `${profileSource}-primary`, '--primary');
                await proveLaunch(true, { launch_profile: 'Named' }, `${profileSource}-named`, '--named,value');
                await proveLaunch(false, { disable_launch_profile: true }, '<none>', '');
                if (language === 'VB') {
                    fs.rmSync(path.join(fixture.directory, 'My Project', 'launchSettings.json'));
                    await proveLaunch(false, {}, 'properties-primary', '--primary');
                }
            } finally {
                fs.rmSync(fixture.directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
            }
        });
    }

    test('uses coordinated build environment and Release output without rebuilding the project', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const projectDirectory = path.join(getWorkspaceRoot(), 'CoordinatedReleaseProject');
        const projectPath = path.join(projectDirectory, 'CoordinatedReleaseProject.csproj');
        const programPath = path.join(projectDirectory, 'Program.cs');
        fs.mkdirSync(projectDirectory, { recursive: true });

        try {
            writeFileWithRetry(projectPath, `
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <BUILD_FLAVOR>project-default</BUILD_FLAVOR>
                    <OutputPath Condition="'$(BUILD_FLAVOR)' == 'custom'">bin/custom/</OutputPath>
                    <OutputPath Condition="'$(RUNTIME_ONLY)' != ''">bin/wrong/</OutputPath>
                  </PropertyGroup>
                </Project>
            `);
            writeFileWithRetry(programPath, 'System.Console.WriteLine("coordinated");\n');
            execFileSync('dotnet', ['build', projectPath, '--configuration', 'Release', '--nologo', '--property:BUILD_FLAVOR=custom'], {
                cwd: projectDirectory,
                env: { ...process.env, BUILD_FLAVOR: 'custom' },
                stdio: 'pipe',
            });

            // If the extension ignores suppress_build and launches its own project build, this invalid
            // source makes the E2E command fail. TargetPath evaluation itself does not compile sources.
            writeFileWithRetry(programPath, 'this does not compile\n');
            const buildEnvironmentName = process.platform === 'win32' ? 'build_flavor' : 'BUILD_FLAVOR';
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project-with-external-build.v1',
                project_path: projectPath,
                build_configuration: 'Release',
                build_environment: { [buildEnvironmentName]: 'custom' },
                build_working_directory: projectDirectory,
                suppress_build: true,
            };
            const controlStatus = await executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig,
                env: [
                    { name: 'BUILD_FLAVOR', value: 'runtime' },
                    { name: 'RUNTIME_ONLY', value: 'runtime-value' },
                ],
                debug: false,
                isApphost: true,
            }, { timeoutMs: 180000 });
            const debugConfiguration = controlStatus.result as { program?: string };

            assert.ok(debugConfiguration.program);
            assert.match(debugConfiguration.program.replaceAll('\\', '/'), /\/bin\/custom\//);
        } finally {
            fs.rmSync(projectDirectory, { recursive: true, force: true });
        }
    });

    test('honors custom run properties without requiring project output or rebuilding', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const projectDirectory = path.join(getWorkspaceRoot(), 'CoordinatedCustomRunProject');
        const projectPath = path.join(projectDirectory, 'CoordinatedCustomRunProject.csproj');
        const programPath = path.join(projectDirectory, 'Program.cs');
        fs.mkdirSync(projectDirectory, { recursive: true });

        try {
            writeFileWithRetry(projectPath, `
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="CustomizeRunCommand" AfterTargets="ComputeRunArguments">
                    <PropertyGroup>
                      <RunCommand>custom-launcher</RunCommand>
                      <RunArguments>--from-msbuild</RunArguments>
                      <RunWorkingDirectory>relative-run-directory</RunWorkingDirectory>
                    </PropertyGroup>
                  </Target>
                </Project>
            `);
            // ComputeRunArguments can select an unrelated launcher without producing TargetPath. Invalid
            // source also makes the test fail if the extension attempts to replace the coordinated build.
            writeFileWithRetry(programPath, 'this does not compile\n');
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project-with-external-build.v1',
                project_path: projectPath,
                build_working_directory: projectDirectory,
                suppress_build: true,
            };
            const controlStatus = await executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig,
                args: ['--from-session'],
                debug: true,
                isApphost: true,
            }, { timeoutMs: 180000 });
            const debugConfiguration = controlStatus.result as {
                program?: string;
                cwd?: string;
                noDebug?: boolean;
            };

            assert.strictEqual(debugConfiguration.program, 'custom-launcher');
            assert.strictEqual(debugConfiguration.cwd, path.join(projectDirectory, 'relative-run-directory'));
            assert.strictEqual(debugConfiguration.noDebug, true);
            assert.strictEqual(
                fs.existsSync(path.join(projectDirectory, 'bin', 'Debug', 'net10.0', 'CoordinatedCustomRunProject.dll')),
                false);
        } finally {
            fs.rmSync(projectDirectory, { recursive: true, force: true });
        }
    });

    test('uses the requested file-app configuration instead of a source Configuration property', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const projectDirectory = path.join(getWorkspaceRoot(), 'CoordinatedFileApp');
        const projectPath = path.join(projectDirectory, 'app.cs');
        fs.mkdirSync(projectDirectory, { recursive: true });

        try {
            const source = [
                '// Licensed to the .NET Foundation under one or more agreements.',
                '// The .NET Foundation licenses this file to you under the MIT license.',
                '',
                '#:property Configuration=Release',
                'System.Console.WriteLine("coordinated file app");',
                ''
            ].join('\n');
            writeFileWithRetry(projectPath, source);
            execFileSync('dotnet', ['build', projectPath, '--configuration', 'Debug', '--nologo'], {
                cwd: projectDirectory,
                stdio: 'pipe',
            });

            // The launch-property query must use the output that the coordinated build already produced.
            // Keeping the project invalid catches accidental extension-owned build work on this path.
            writeFileWithRetry(projectPath, source.replace(
                'System.Console.WriteLine("coordinated file app");',
                'this does not compile'));
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                build_configuration: 'Debug',
                suppress_build: true,
            };
            const controlStatus = await executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig,
                debug: false,
                isApphost: true,
            }, { timeoutMs: 180000 });
            const debugConfiguration = controlStatus.result as { program?: string };

            assert.ok(debugConfiguration.program);
            assert.ok(fs.existsSync(debugConfiguration.program), `Expected configured file-app output to exist: ${debugConfiguration.program}`);
            const outputPathSegments = debugConfiguration.program.split(/[\\/]/).map(segment => segment.toLowerCase());
            assert.ok(
                outputPathSegments.includes('debug'),
                `Expected Debug output, got: ${debugConfiguration.program}`);
            assert.ok(
                !outputPathSegments.includes('release'),
                `Expected the source Configuration property to be overridden, got: ${debugConfiguration.program}`);
        } finally {
            fs.rmSync(projectDirectory, { recursive: true, force: true });
        }
    });
});

interface DotnetResourceLaunchProof {
    proof: string;
    configuration: AspireResourceExtendedDebugConfiguration;
    launchRequests: Array<{ type: string; noDebug: boolean }>;
    breakpointResponses: Array<{ success: boolean }>;
    breakpointHit?: { reason: string; sourcePath: string; line: number };
    exitCode: number;
    readiness: string;
    output: string[];
}

function createResourceProject(language: 'C#' | 'F#' | 'VB') {
    const extension = language === 'C#' ? 'cs' : language === 'F#' ? 'fs' : 'vb';
    const projectName = `CoordinatedResource_${extension}`;
    const directory = path.join(getWorkspaceRoot(), projectName);
    const projectPath = path.join(directory, `${projectName}.${extension}proj`);
    const sourcePath = path.join(directory, `Program.${extension}`);
    const buildSentinelPath = path.join(directory, `BuildSentinel.${extension}`);
    fs.mkdirSync(directory, { recursive: true });
    writeFileWithRetry(projectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Optimize>false</Optimize>
    <DebugType>portable</DebugType>
    <UseAppHost>false</UseAppHost>
    <BUILD_FLAVOR>project-default</BUILD_FLAVOR>
    <OutputPath Condition="'$(BUILD_FLAVOR)' == 'custom'">bin/custom/$(Configuration)/</OutputPath>
    <OutputPath Condition="'$(RUNTIME_ONLY)' != ''">bin/wrong/</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="BuildSentinel.${extension}" />
    <Compile Include="Program.${extension}" />
    <AssemblyMetadata Include="BuildFlavor" Value="$(BUILD_FLAVOR)" />
  </ItemGroup>
</Project>
`);
    const sources = {
        'C#': `using System;
using System.IO;
using System.Linq;
using System.Reflection;

var executingAssembly = Assembly.GetExecutingAssembly();
var configuration = executingAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>().Configuration;
var flavor = executingAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "BuildFlavor").Value;
var message = string.Join("|", configuration, flavor, Environment.GetEnvironmentVariable("E2E_PROFILE") ?? "<none>", Environment.GetEnvironmentVariable("BUILD_FLAVOR"), Environment.GetEnvironmentVariable("RUNTIME_ONLY"), string.Join(",", args), executingAssembly.Location);
Console.WriteLine("coordinated-resource-ready");
File.WriteAllText(Environment.GetEnvironmentVariable("E2E_READY_PATH"), message);
`,
        'F#': `open System
open System.IO
open System.Reflection

[<EntryPoint>]
let main args =
    let executingAssembly = Assembly.GetExecutingAssembly()
    let configuration = executingAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>().Configuration
    let flavor = (executingAssembly.GetCustomAttributes<AssemblyMetadataAttribute>() |> Seq.find (fun attribute -> attribute.Key = "BuildFlavor")).Value
    let profile = Environment.GetEnvironmentVariable("E2E_PROFILE") |> Option.ofObj |> Option.defaultValue "<none>"
    let message = String.Join("|", [| configuration; flavor; profile; Environment.GetEnvironmentVariable("BUILD_FLAVOR"); Environment.GetEnvironmentVariable("RUNTIME_ONLY"); String.Join(",", args); executingAssembly.Location |])
    printfn "coordinated-resource-ready"
    File.WriteAllText(Environment.GetEnvironmentVariable("E2E_READY_PATH"), message)
    0
`,
        'VB': `Imports System
Imports System.IO
Imports System.Linq
Imports System.Reflection

Module Program
    Sub Main(args As String())
        Dim executingAssembly = Assembly.GetExecutingAssembly()
        Dim configuration = executingAssembly.GetCustomAttribute(Of AssemblyConfigurationAttribute)().Configuration
        Dim flavor = executingAssembly.GetCustomAttributes(Of AssemblyMetadataAttribute)().Single(Function(attribute) attribute.Key = "BuildFlavor").Value
        Dim profile = If(Environment.GetEnvironmentVariable("E2E_PROFILE"), "<none>")
        Dim message = String.Join("|", New String() {configuration, flavor, profile, Environment.GetEnvironmentVariable("BUILD_FLAVOR"), Environment.GetEnvironmentVariable("RUNTIME_ONLY"), String.Join(",", args), executingAssembly.Location})
        Console.WriteLine("coordinated-resource-ready")
        File.WriteAllText(Environment.GetEnvironmentVariable("E2E_READY_PATH"), message)
    End Sub
End Module
`,
    };
    const source = sources[language];
    writeFileWithRetry(sourcePath, source);
    writeFileWithRetry(buildSentinelPath, language === 'C#'
        ? 'internal static class BuildSentinel { }\n'
        : language === 'F#'
            ? 'module BuildSentinel\nlet value = 0\n'
            : 'Friend Module BuildSentinel\nEnd Module\n');
    for (const profileDirectory of language === 'VB' ? ['Properties', 'My Project'] : ['Properties']) {
        const profilePath = path.join(directory, profileDirectory, 'launchSettings.json');
        fs.mkdirSync(path.dirname(profilePath), { recursive: true });
        const sourceName = profileDirectory === 'My Project' ? 'my-project' : 'properties';
        writeFileWithRetry(profilePath, JSON.stringify({
            profiles: {
                Primary: { commandName: 'Project', commandLineArgs: '--primary', environmentVariables: { E2E_PROFILE: `${sourceName}-primary` } },
                Named: { commandName: 'Project', commandLineArgs: '--named value', environmentVariables: { E2E_PROFILE: `${sourceName}-named` } },
            },
        }));
    }
    writeFileWithRetry(path.join(directory, `${projectName}.run.json`), JSON.stringify({
        profiles: { Ignored: { commandName: 'Project', environmentVariables: { E2E_PROFILE: 'run-json' } } },
    }));

    const breakpointLine = source.split('\n').findIndex(line => line.includes('coordinated-resource-ready'));
    assert.ok(breakpointLine >= 0);
    const targetPath = path.join(directory, 'bin', 'custom', 'Release', 'net11.0', `${projectName}.dll`);
    return { directory, projectPath, sourcePath, targetPath, buildSentinelPath, breakpointLine };
}
