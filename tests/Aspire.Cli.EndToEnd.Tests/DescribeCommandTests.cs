// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using System.Text.Json;
using Aspire.Cli.Resources;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the aspire describe command.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class DescribeCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DescribeCommandShowsRunningResources()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireResourcesTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireResourcesTestApp/AspireResourcesTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Override the type label on apiservice. The first .WithHttpHealthCheck("/health"); in the
        // starter AppHost belongs to apiservice; the template's cache keeps the Redis class default
        // and webfrontend has no label, so it falls back to its technical type.
        var appHostDir = Path.Combine(workspace.WorkspaceRoot.FullName, "AspireResourcesTestApp", "AspireResourcesTestApp.AppHost");
        var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");
        var content = File.ReadAllText(appHostFilePath);
        const string originalPattern = ".WithHttpHealthCheck(\"/health\");";
        var index = content.IndexOf(originalPattern, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected to find '{originalPattern}' in {appHostFilePath}.");
        const string replacement = """
            .WithHttpHealthCheck("/health")
            .WithResourceTypeDisplayName("Orders API");
            """;
        File.WriteAllText(appHostFilePath, content[..index] + replacement + content[(index + originalPattern.Length)..]);

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait a bit for resources to stabilize
        await auto.TypeAsync("sleep 5");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Now verify aspire describe shows the running resources (human-readable table)
        await auto.TypeAsync("aspire describe");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Name", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync("webfrontend", timeout: TimeSpan.FromSeconds(5));
        await auto.WaitUntilTextAsync("apiservice", timeout: TimeSpan.FromSeconds(5));

        // The Type column shows the label on the same row as its resource:
        //   │ cache      │ Redis      │ Running │ Healthy │ tcp://localhost:6379 │
        // Match with .* so the checks don't depend on the table's border characters or column widths.
        foreach (var (resourceName, expectedType) in new[] { ("cache", "Redis"), ("apiservice", "Orders API"), ("webfrontend", "Project") })
        {
            var typeRow = new CellPatternSearcher().FindPattern($"{resourceName}.*{expectedType}");
            await auto.WaitUntilAsync(s => typeRow.Search(s).Count > 0, timeout: TimeSpan.FromSeconds(5), description: $"describe table showing {resourceName} with type {expectedType}");
        }

        await auto.WaitForSuccessPromptAsync(counter);

        // Test aspire describe --format json output - pipe to file to avoid terminal buffer issues
        await auto.TypeAsync("aspire describe --format json > resources.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the JSON file contains expected resources
        await auto.TypeAsync("cat resources.json | grep webfrontend");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("webfrontend", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // The label travels through the real backchannel while resourceType stays the technical discriminator.
        // The workspace is a bind mount, so the file written inside the container is readable from here.
        using var resourcesJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(appHostDir, "resources.json")));
        var resources = resourcesJson.RootElement.GetProperty("resources").EnumerateArray().ToList();
        AssertResourceTypes(resources, "cache", "Container", "Redis");
        AssertResourceTypes(resources, "apiservice", "Project", "Orders API");
        AssertResourceTypes(resources, "webfrontend", "Project", "Project");

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    private static void AssertResourceTypes(List<JsonElement> resources, string displayName, string expectedResourceType, string expectedTypeDisplayName)
    {
        var resource = Assert.Single(resources, r => r.GetProperty("displayName").GetString() == displayName);
        Assert.Equal(expectedResourceType, resource.GetProperty("resourceType").GetString());
        Assert.Equal(expectedTypeDisplayName, resource.GetProperty("resourceTypeDisplayName").GetString());
    }

    [Fact]
    public async Task DescribeCommandResolvesReplicaNames()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        // Pattern for describe output showing a specific replica
        var waitForApiserviceReplicaName = new CellPatternSearcher()
            .FindPattern("apiservice-[a-z0-9]+");

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new
        await auto.AspireNewAsync("AspireReplicaTestApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd AspireReplicaTestApp/AspireReplicaTestApp.AppHost");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Add .WithReplicas(2) to the apiservice resource in the AppHost
        var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, "AspireReplicaTestApp");
        var appHostDir = Path.Combine(projectDir, "AspireReplicaTestApp.AppHost");
        var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

        output.WriteLine($"Looking for AppHost.cs at: {appHostFilePath}");

        var content = File.ReadAllText(appHostFilePath);

        // Add .WithReplicas(2) to the first .WithHttpHealthCheck("/health"); occurrence (apiservice)
        // and clear the fixed target ports so DCP can assign unique ports to each replica.
        var originalPattern = ".WithHttpHealthCheck(\"/health\");";
        var replacement = """
            .WithHttpHealthCheck("/health")
            .WithReplicas(2)
            .WithEndpoint("http", e => e.TargetPort = null, createIfNotExists: false)
            .WithEndpoint("https", e => e.TargetPort = null, createIfNotExists: false);
            """;

        // Only replace the first occurrence (apiservice), not the second (webfrontend)
        var index = content.IndexOf(originalPattern);
        if (index >= 0)
        {
            content = content[..index] + replacement + content[(index + originalPattern.Length)..];
        }

        File.WriteAllText(appHostFilePath, content);

        output.WriteLine("Modified AppHost.cs to add .WithReplicas(2) to apiservice and clear fixed endpoint target ports");

        // Start the AppHost in the background using aspire start
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Wait for resources to stabilize
        await auto.TypeAsync("sleep 10");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 1: aspire describe with friendly name for a non-replicated resource (cache)
        // This should resolve via DisplayName since cache has only one instance
        await auto.TypeAsync("aspire describe cache --format json > cache-describe.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify cache resource was found in the output
        await auto.TypeAsync("cat cache-describe.json | grep cache");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("cache", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 2: Get all resources to find an apiservice replica name
        await auto.TypeAsync("aspire describe --format json > all-resources.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Extract a replica name from the JSON - apiservice replicas have names like apiservice-<suffix>
        await auto.TypeAsync("REPLICA_NAME=$(cat all-resources.json | grep -o '\"name\": *\"apiservice-[a-z0-9]*\"' | head -1 | sed 's/.*\"\\(apiservice-[a-z0-9]*\\)\"/\\1/')");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify we captured a replica name
        await auto.TypeAsync("echo \"Found replica: $REPLICA_NAME\"");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s => waitForApiserviceReplicaName.Search(s).Count > 0, timeout: TimeSpan.FromSeconds(10), description: "waiting for apiservice replica name");
        await auto.WaitForSuccessPromptAsync(counter);

        // Test 3: aspire describe with the replica name
        // This should resolve via exact Name match
        await auto.TypeAsync("aspire describe $REPLICA_NAME --format json > replica-describe.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the replica was found and output contains the replica name
        await auto.TypeAsync("cat replica-describe.json | grep apiservice");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s => waitForApiserviceReplicaName.Search(s).Count > 0, timeout: TimeSpan.FromSeconds(10), description: "waiting for apiservice replica in describe output");
        await auto.WaitForSuccessPromptAsync(counter);

        // Stop the AppHost using aspire stop
        await auto.TypeAsync("aspire stop");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DescribeLaunchGroupsDoesNotStartExcludedResourcesOrBuildProjects()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewAsync("LaunchGroupsTestApp", counter, template: AspireTemplate.EmptyAppHost);
        await auto.RunCommandAsync("cd LaunchGroupsTestApp", counter);
        await auto.RunCommandAsync("dotnet new console --output DeferredProbe --framework net10.0", counter);

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "LaunchGroupsTestApp");
        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var source = File.ReadAllText(appHostPath);
        const string builderDeclaration = "var builder = DistributedApplication.CreateBuilder(args);";
        var builderIndex = source.IndexOf(builderDeclaration, StringComparison.Ordinal);
        Assert.True(builderIndex >= 0, $"Expected to find '{builderDeclaration}' in {appHostPath}.");
        File.WriteAllText(appHostPath, source[..builderIndex] + """
            using Aspire.Hosting.ApplicationModel;

            #pragma warning disable ASPIRELAUNCHGROUPS001

            var builder = DistributedApplication.CreateBuilder(args);
            builder.Configuration["Aspire:LaunchGroups:0"] = "frontend";
            builder.Configuration["Aspire:LaunchGroups:1"] = "backend";
            builder.Configuration["Aspire:LaunchGroups:2"] = "empty";

            var active = builder.AddExecutable("active", "sh", builder.AppHostDirectory, "-c", "sleep 600")
                .WithLaunchGroups("frontend", "FRONTEND")
                .WithEnvironment("LAUNCH_GROUP_TEST_SECRET", "launch-group-secret-value");
            builder.AddResource(new ChildResource("child", active.Resource));
            builder.AddExecutable("manual", "sh", builder.AppHostDirectory, "-c", "sleep 600")
                .WithLaunchGroup("frontend")
                .WithExplicitStart();
            var deferred = builder.AddDotnetProject("deferred", Path.Combine(builder.AppHostDirectory, "DeferredProbe", "DeferredProbe.csproj"))
                .WithLaunchGroup("backend")
                .WithEnvironment(context =>
                {
                    File.WriteAllText(Path.Combine(builder.AppHostDirectory, "callback-probe.txt"), "called");
                    context.EnvironmentVariables["DEFERRED_PROBE"] = "called";
                });
            builder.AddResource(new ChildResource("selected-child", deferred.Resource))
                .WithLaunchGroup("frontend");
            builder.Build().Run();

            sealed class ChildResource(string name, IResource parent) : Resource(name), IResourceWithParent
            {
                public IResource Parent => parent;
            }
            """);

        var deferredProjectPath = Path.Combine(projectDirectory, "DeferredProbe", "DeferredProbe.csproj");
        var deferredProject = File.ReadAllText(deferredProjectPath);
        Assert.Contains("</Project>", deferredProject);
        File.WriteAllText(deferredProjectPath, deferredProject.Replace("</Project>", """
            <Target Name="RecordLaunchGroupBuild" BeforeTargets="CoreCompile">
              <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-probe.txt" Lines="built" Overwrite="true" />
            </Target>
            </Project>
            """, StringComparison.Ordinal));

        await auto.RunCommandAsync("export Aspire__LaunchGroup=frontend", counter);
        await auto.AspireStartAsync(counter, isolated: true);
        await auto.RunCommandAsync("aspire wait active --status up --timeout 120", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("aspire describe --format json > resources-before.json", counter);

        var buildProbePath = Path.Combine(projectDirectory, "DeferredProbe", "build-probe.txt");
        var callbackProbePath = Path.Combine(projectDirectory, "callback-probe.txt");
        Assert.False(File.Exists(buildProbePath));
        Assert.False(File.Exists(callbackProbePath));

        await auto.RunCommandAsync("aspire describe --launch-groups > groups-first.txt", counter);
        await auto.RunCommandAsync("aspire describe --launch-groups > groups-second.txt", counter);
        await auto.RunCommandAsync("aspire describe --launch-groups --format json > groups-first.json", counter);
        await auto.RunCommandAsync("aspire describe --launch-groups --format json > groups-second.json", counter);
        await auto.RunCommandAsync("aspire describe --format json > resources-after.json", counter);

        var text = File.ReadAllText(Path.Combine(projectDirectory, "groups-first.txt"));
        var json = File.ReadAllText(Path.Combine(projectDirectory, "groups-first.json"));
        Assert.Equal(text, File.ReadAllText(Path.Combine(projectDirectory, "groups-second.txt")));
        Assert.Equal(json, File.ReadAllText(Path.Combine(projectDirectory, "groups-second.json")));
        var selectedChildText = Assert.Single(text.Split('\n'), line => line.Contains("selected-child", StringComparison.Ordinal));
        Assert.Contains("Default startup", selectedChildText);
        Assert.DoesNotContain("Automatic", selectedChildText);
        Assert.Contains("frontend", text);
        Assert.Contains("backend", text);
        Assert.Contains("empty", text);
        Assert.DoesNotContain("launch-group-secret-value", text);
        Assert.DoesNotContain("launch-group-secret-value", json);

        using var groups = JsonDocument.Parse(json);
        Assert.Equal("frontend", groups.RootElement.GetProperty("selectedGroup").GetString());
        Assert.Equal(["backend", "empty", "frontend"], groups.RootElement.GetProperty("groups").EnumerateArray().Select(group => group.GetString()));
        var groupResources = groups.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        var child = Assert.Single(groupResources, resource => resource.GetProperty("name").GetString() == "child");
        Assert.Equal(["frontend"], child.GetProperty("groups").EnumerateArray().Select(group => group.GetString()));
        Assert.Equal("active", child.GetProperty("inheritedFrom").GetString());
        var selectedChild = Assert.Single(groupResources, resource => resource.GetProperty("name").GetString() == "selected-child");
        Assert.Equal(["frontend"], selectedChild.GetProperty("groups").EnumerateArray().Select(group => group.GetString()));
        Assert.False(selectedChild.TryGetProperty("inheritedFrom", out _));
        Assert.False(selectedChild.GetProperty("explicitStart").GetBoolean());
        Assert.Equal("None", selectedChild.GetProperty("exclusionReason").GetString());
        var manual = Assert.Single(groupResources, resource => resource.GetProperty("name").GetString() == "manual");
        Assert.True(manual.GetProperty("explicitStart").GetBoolean());
        Assert.Equal("None", manual.GetProperty("exclusionReason").GetString());
        var deferred = Assert.Single(groupResources, resource => resource.GetProperty("name").GetString() == "deferred");
        Assert.Equal("NotInSelectedGroup", deferred.GetProperty("exclusionReason").GetString());
        Assert.Equal("Deferred", deferred.GetProperty("buildType").GetString());
        var coordinatedBuild = Assert.Single(groupResources, resource => resource.GetProperty("name").GetString() == "__dotnet-project-build");
        Assert.Empty(coordinatedBuild.GetProperty("groups").EnumerateArray());
        Assert.False(coordinatedBuild.GetProperty("explicitStart").GetBoolean());
        Assert.Equal("ExemptFromSelection", coordinatedBuild.GetProperty("exclusionReason").GetString());
        Assert.Equal("Deferred", coordinatedBuild.GetProperty("buildType").GetString());

        foreach (var fileName in new[] { "resources-before.json", "resources-after.json" })
        {
            using var resourcesJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, fileName)));
            var resources = resourcesJson.RootElement.GetProperty("resources").EnumerateArray().ToArray();
            Assert.Equal("Running", Assert.Single(resources, resource => resource.GetProperty("displayName").GetString() == "active").GetProperty("state").GetString());
            Assert.Equal("NotStarted", Assert.Single(resources, resource => resource.GetProperty("displayName").GetString() == "manual").GetProperty("state").GetString());
            Assert.Equal("NotStarted", Assert.Single(resources, resource => resource.GetProperty("displayName").GetString() == "deferred").GetProperty("state").GetString());
        }
        Assert.False(File.Exists(buildProbePath));
        Assert.False(File.Exists(callbackProbePath));

        await auto.AspireStopAsync(counter);
    }
}
