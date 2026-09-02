import * as assert from 'assert';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { ProjectLaunchConfiguration } from '../dcp/types';
import { waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, writeFileWithRetry } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire coordinated build E2E', function () {
    this.timeout(240000);

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

    test('honors custom run properties without rebuilding the project', async () => {
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
            writeFileWithRetry(programPath, 'System.Console.WriteLine("coordinated");\n');
            execFileSync('dotnet', ['build', projectPath, '--nologo'], {
                cwd: projectDirectory,
                stdio: 'pipe',
            });

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
            writeFileWithRetry(projectPath, [
                '// Licensed to the .NET Foundation under one or more agreements.',
                '// The .NET Foundation licenses this file to you under the MIT license.',
                '',
                '#:property Configuration=Release',
                'System.Console.WriteLine("coordinated file app");',
                ''
            ].join('\n'));
            execFileSync('dotnet', ['build', projectPath, '--configuration', 'Debug', '--nologo'], {
                cwd: projectDirectory,
                stdio: 'pipe',
            });

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
