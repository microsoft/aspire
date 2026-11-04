// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI publish flows with a TypeScript AppHost.
/// </summary>
public sealed class TypeScriptPublishTests(ITestOutputHelper output)
{
    private static readonly string s_jsPublishFixturesDir = Path.Combine(
        CliE2ETestHelpers.GetRepoRoot(),
        "tests", "Aspire.Cli.EndToEnd.Tests", "Fixtures", "JsPublish");

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithKubernetesPersistentVolumeConfigurationCustomizesClaims()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test requires locally built packages or current PR artifacts for the PVC customization exports.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.Kubernetes."]);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.RunCommandAsync("aspire add Aspire.Hosting.Kubernetes", counter, TimeSpan.FromMinutes(2));
        await auto.RunCommandAsync("aspire restore", counter, TimeSpan.FromMinutes(3));

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var appHost = """
            import assert from 'node:assert/strict';
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            const k8s = await builder.addKubernetesEnvironment("env");
            const data = await k8s.addPersistentVolume("data");
            await data.withStorageClass("fast");
            await data.withPersistentVolumeName("original-volume");
            await data.withConfiguration(async (claim) => {
                const metadata = await claim.metadata.get();
                await metadata.labels.set("stage", "first");
                const annotations = await metadata.annotations();
                await annotations.set("example.com/retention", "retain");
                const spec = await claim.spec.get();
                assert.equal(await spec.storageClassName.get(), "fast");
                assert.equal(await spec.volumeName.get(), "original-volume");
                await spec.storageClassName.set("");
                await spec.volumeName.set("replacement-volume");
                await spec.volumeMode.set("Filesystem");
                const accessModes = await spec.accessModes();
                assert.equal(await accessModes.get(0), "ReadWriteOnce");
                await accessModes.clear();
                await accessModes.add("ReadWriteMany");
                const requests = await (await spec.resources.get()).requests();
                assert.equal(await requests.get("storage"), "1Gi");
                await requests.set("storage", "20Gi");
            });
            await data.withConfiguration(async (claim) => {
                const metadata = await claim.metadata.get();
                assert.equal(await metadata.labels.get("stage"), "first");
                await metadata.labels.set("stage", "second");
                const spec = await claim.spec.get();
                assert.equal(await spec.storageClassName.get(), "");
                assert.equal(await spec.volumeName.get(), "replacement-volume");
            });

            const omitted = await k8s.addPersistentVolume("omitted");
            await omitted.withStorageClass("fast");
            await omitted.withConfiguration(async (claim) => {
                const spec = await claim.spec.get();
                assert.equal(await spec.volumeMode.get(), null);
                assert.equal(await spec.volumeName.get(), null);
                await spec.storageClassName.set(null);
            });

            const storageClass = await builder.addParameter("storageclass");
            const capacity = await builder.addParameter("capacity");
            const backupTier = await builder.addParameter("backuptier");
            const parameterized = await k8s.addPersistentVolume("parameterized");
            await parameterized.withStorageClassParam(storageClass);
            await parameterized.withCapacityParam(capacity);
            await parameterized.withVolumeAnnotationParam("backup.example.com/tier", backupTier);
            await parameterized.withConfiguration(async (claim) => {
                const metadata = await claim.metadata.get();
                await metadata.labels.set("example.com/retention", "retain");
                await (await claim.spec.get()).volumeMode.set("Filesystem");
            });

            for (const [name, volume] of [["data", data], ["omitted", omitted], ["parameterized", parameterized]] as const) {
                const service = await builder.addContainer(`${name}-service`, "nginx");
                await service.withKubernetesPersistentVolumeMount(volume, "/data");
            }

            await builder.build().run();
            """;
        await File.WriteAllTextAsync(appHostPath, appHost);
        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.RunCommandAsync("export Parameters__storageclass=fast Parameters__capacity=5Gi Parameters__backuptier=daily", counter);
        await auto.RunCommandAsync("aspire publish -o artifacts --non-interactive", counter, TimeSpan.FromMinutes(5));

        var artifactsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "artifacts");
        var claimPath = Assert.Single(Directory.GetFiles(artifactsPath, "data.yaml", SearchOption.AllDirectories));
        var chartPath = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(claimPath)))!;
        await Verify(await File.ReadAllTextAsync(claimPath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(chartPath, "templates", "omitted", "omitted.yaml")), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(chartPath, "templates", "parameterized", "parameterized.yaml")), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(chartPath, "values.yaml")), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(chartPath, "templates", "data-service", "statefulset.yaml")), "yaml");

        await File.WriteAllTextAsync(appHostPath, appHost.Replace("await builder.build().run();", """
            await data.withConfiguration(async () => {
                throw new Error("PVC callback failure");
            });
            await builder.build().run();
            """, StringComparison.Ordinal));
        await auto.RunCommandAsync("! aspire publish -o failed-artifacts --non-interactive > callback-failure.log 2>&1",
            counter, TimeSpan.FromMinutes(5));
        var failureLog = await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "callback-failure.log"));
        Assert.Contains("PVC callback failure", failureLog);
    }

    [Fact]
    public async Task PublishWithDockerComposeServiceCallbackSucceeds()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.EnablePolyglotSupportAsync(counter);

        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.KeyAsync(Hex1bKey.DownArrow);
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var newContent = """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addDockerComposeEnvironment("compose");

            const postgres = await builder.addPostgres("postgres")
                .publishAsDockerComposeService(async (_, svc) => {
                    await svc.name.set("postgres");
                });

            await postgres.addDatabase("db");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish -o artifacts --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(5));

        await auto.TypeAsync("ls -la artifacts/docker-compose.yaml");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("grep -F \"postgres:\" artifacts/docker-compose.yaml");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishJavaScriptPatternsGeneratesExpectedDockerComposeArtifacts()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            ["Aspire.Hosting.CodeGeneration.TypeScript.", "Aspire.Hosting.JavaScript.", "Aspire.Hosting.Docker."]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        CopyJavaScriptPublishFixtures(workspace);
        WriteJavaScriptPublishAppHost(workspace);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish -o artifacts --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(5));

        var artifactsPath = Path.Combine(workspace.WorkspaceRoot.FullName, "artifacts");
        var composeContent = await File.ReadAllTextAsync(Path.Combine(artifactsPath, "docker-compose.yaml"));

        Assert.Contains("staticsite:", composeContent);
        Assert.Contains("nodeserver:", composeContent);
        Assert.Contains("npmscript:", composeContent);
        Assert.Contains("nextjs:", composeContent);

        AssertDockerfileContains(
            artifactsPath,
            "staticsite",
            "COPY --from=build /app/dist /app/wwwroot",
            "ENTRYPOINT [\"dotnet\",\"/app/yarp.dll\"]");

        AssertDockerfileContains(
            artifactsPath,
            "nodeserver",
            "COPY --from=build /app/build /app/build",
            "ENV NODE_ENV=production",
            "USER node",
            "ENTRYPOINT [\"node\",\"build/server.js\"]");

        AssertDockerfileContains(
            artifactsPath,
            "npmscript",
            " AS prod-deps",
            "RUN --mount=type=cache,target=/root/.npm npm ci --omit=dev",
            "COPY --from=build /app /app",
            "COPY --from=prod-deps /app/node_modules ./node_modules",
            "ENV NODE_ENV=production",
            "ENTRYPOINT [\"sh\",\"-c\",\"exec npm run start -- --port $PORT\"]");

        AssertDockerfileContains(
            artifactsPath,
            "nextjs",
            "COPY --from=build --chown=node:node /app/public ./public",
            "COPY --from=build --chown=node:node /app/.next/standalone ./",
            "COPY --from=build --chown=node:node /app/.next/static ./.next/static",
            "USER node",
            "ENTRYPOINT [\"node\",\"server.js\"]");
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithoutOutputPathUsesAppHostDirectoryDefault()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test validates current TypeScript AppHost publish behavior. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.DotNet, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.EnablePolyglotSupportAsync(counter);

        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.KeyAsync(Hex1bKey.DownArrow);
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var newContent = """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addDockerComposeEnvironment("compose");

            const postgres = await builder.addPostgres("postgres")
                .publishAsDockerComposeService(async (_, svc) => {
                    await svc.name.set("postgres");
                });

            await postgres.addDatabase("db");

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(5));

        var dockerComposePath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-output", "docker-compose.yaml");
        Assert.True(File.Exists(dockerComposePath), $"Expected docker-compose output at {dockerComposePath}");

        var dockerComposeContent = await File.ReadAllTextAsync(dockerComposePath);
        Assert.Contains("postgres:", dockerComposeContent);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishWithConfigureEnvFileUpdatesEnvOutput()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test exercises unreleased TypeScript AppHost SDK surface. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.EnablePolyglotSupportAsync(counter);

        await auto.TypeAsync("aspire init --language typescript --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Docker");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        var newContent = """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            const compose = await builder.addDockerComposeEnvironment("compose");
            await compose.withDashboard({ enabled: false });

            const container = await builder.addContainer("my-container", "nginx:alpine");
            await container.withBindMount("/host/path/data", "/container/data");

            await compose.configureEnvFile(async (envVars) => {
                const bindMount = await envVars.get("MY_CONTAINER_BINDMOUNT_0");
                await bindMount.description.set("Customized bind mount source");
                await bindMount.defaultValue.set("./data");
            });

            await builder.build().run();
            """;

        File.WriteAllText(appHostPath, newContent);

        await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire publish -o artifacts --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(5));

        var envFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "artifacts", ".env");
        Assert.True(File.Exists(envFilePath), $"Expected env file at {envFilePath}");

        var envFileContent = await File.ReadAllTextAsync(envFilePath);
        Assert.Contains("# Customized bind mount source", envFileContent);
        Assert.DoesNotContain("# Bind mount source for my-container:/container/data", envFileContent);
    }

    private static void WriteJavaScriptPublishAppHost(TemporaryWorkspace workspace)
    {
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        File.WriteAllText(appHostPath, """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            const compose = await builder.addDockerComposeEnvironment('compose');
            await compose.withDashboard({ enabled: false });

            const api = await builder.addNodeApp('api', './api', 'server.js')
                .withHttpEndpoint({ port: 3001, env: 'PORT' });

            await builder.addViteApp('staticsite', './staticsite')
                .withHttpEndpoint({ name: 'http', targetPort: 5000 })
                .publishAsStaticWebsite({ apiPath: '/api', apiTarget: api });

            await builder.addViteApp('nodeserver', './nodeserver')
                .publishAsNodeServer('build/server.js', { outputPath: 'build' });

            await builder.addViteApp('npmscript', './npmscript')
                .publishAsPackageScript({ scriptName: 'start', runScriptArguments: '-- --port $PORT' });

            await builder.addNextJsApp('nextjs', './nextjs');

            await builder.build().run();
            """);
    }

    private static void CopyJavaScriptPublishFixtures(TemporaryWorkspace workspace)
    {
        foreach (var fixtureName in new[] { "api", "staticsite", "nodeserver", "npmscript", "nextjs" })
        {
            TestDirectoryHelpers.CopyDirectory(
                Path.Combine(s_jsPublishFixturesDir, fixtureName),
                Path.Combine(workspace.WorkspaceRoot.FullName, fixtureName));
        }
    }

    private static void AssertDockerfileContains(string artifactsPath, string resourceName, params string[] expectedFragments)
    {
        var dockerfilePath = Path.Combine(artifactsPath, $"{resourceName}.Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Expected Dockerfile for resource '{resourceName}' at {dockerfilePath}");

        var content = File.ReadAllText(dockerfilePath);

        foreach (var expectedFragment in expectedFragments)
        {
            Assert.Contains(expectedFragment, content);
        }
    }
}
