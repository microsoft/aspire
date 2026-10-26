// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

public sealed class NativeHostingWorkflowTests
{
    [Theory]
    [InlineData("src/Aspire.Hosting.Native.Server/Core/Runtime/RuntimeGeneration.cs")]
    [InlineData("src/Aspire.Hosting.Native.Core/Aspire.Hosting.Native.Core.csproj")]
    [InlineData("src/Aspire.Hosting.Native.Core/Runtime/RuntimeGeneration.cs")]
    [InlineData("src/Aspire.Hosting.Native.CodeGeneration/Program.cs")]
    [InlineData("src/Aspire.Hosting.Native.Client/NativeClient.cs")]
    [InlineData("src/Aspire.Hosting.Native.Client/Authoring/DistributedApplicationBuilder.cs")]
    [InlineData("src/Aspire.Hosting.Native.CodeGeneration/Resources/native-builder.mts")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/transport.ts")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/package.json")]
    [InlineData("src/Aspire.Cli/Templating/Templates/ts-starter/tsconfig.apphost.json")]
    [InlineData("src/Aspire.Cli/Projects/NativeAppHostServerProject.cs")]
    [InlineData("src/Aspire.Managed/AppHostServerV2CodeGeneration.cs")]
    [InlineData("src/Aspire.TerminalHost/Aspire.TerminalHost.csproj")]
    [InlineData("src/Aspire.Dashboard/Components/Pages/Resources.razor")]
    [InlineData("src/Aspire.Hosting/Dashboard/proto/dashboard_service.proto")]
    [InlineData("src/Shared/SocketPermissionHelper.cs")]
    [InlineData("src/Shared/DcpEnvironmentVariables.cs")]
    [InlineData("src/Aspire.Hosting.Native.Server/Transport/SocketConnectionListener.cs")]
    [InlineData("playground/NativeIntegrations/CSharp/Provider/NativeIntegrations.Provider.csproj")]
    [InlineData("playground/NativeIntegrations/CSharp/DockerCompose/DockerComposeEnvironment.cs")]
    [InlineData("playground/NativeIntegrations/CSharp/DockerComposeAppHost/Program.cs")]
    [InlineData("playground/NativeIntegrations/TypeScript/external-api.mts")]
    [InlineData("playground/NativeIntegrations/CSharp/Redis/RedisExtensions.cs")]
    [InlineData("playground/NativeIntegrations/CSharp/RedisProvider/NativeIntegrations.Redis.Provider.csproj")]
    [InlineData("playground/NativeIntegrations/CSharp/RedisValidation/NativeIntegrations.Redis.Validation.csproj")]
    [InlineData("playground/NativeIntegrations/TypeScript/Redis/redis.mts")]
    [InlineData("playground/NativeIntegrations/TypeScript/Redis/redis-protocol.mts")]
    [InlineData("playground/NativeIntegrations/TypeScript/Redis/redis-builder.test.mts")]
    [InlineData("playground/NativeIntegrations/TypeScript/DockerCompose/docker-compose.mts")]
    [InlineData("playground/NativeIntegrations/TypeScript/DockerCompose/AppHost/web/Dockerfile")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/Program.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/NativeRevisionSmoke.mts")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/NativeComposeSmoke.mts")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/NativeComposeComparison.mts")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/CliAppHost/apphost.mts")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/TestServices/DiagnosticCapture.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/NativeCSharpClientTests.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/NativeSymbolicWorkloadSocketTests.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/NativeIpcSecurityTests.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/DcpEnvironmentTests.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/SocketConnectionListenerTests.cs")]
    [InlineData("tests/Aspire.Hosting.Native.Core.Tests/AotSmoke/SymbolicRedisConsumer.mts")]
    [InlineData("tests/PolyglotAppHosts/Aspire.Hosting/TypeScript/package.json")]
    [InlineData("tests/PolyglotAppHosts/Aspire.Hosting.Docker/TypeScript/apphost.mts")]
    [InlineData("tests/Directory.Build.props")]
    [InlineData("tests/Shared/RepoTesting/RepoTesting.csproj")]
    [InlineData("eng/dcppack/Common.projitems")]
    [InlineData("eng/scripts/test-native-hosting.sh")]
    [InlineData("eng/common/tools.sh")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData(".editorconfig")]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("global.json")]
    [InlineData("NuGet.config")]
    [InlineData("Aspire.slnx")]
    [InlineData("restore.sh")]
    [InlineData("build.sh")]
    [InlineData(".github/workflows/native-hosting-validation.yml")]
    public void NativeHostingRuntimeAndBuildInputsTriggerIndependentLane(string path)
    {
        var events = Events();

        foreach (var eventName in new[] { "pull_request", "push" })
        {
            var paths = Paths(events, eventName);
            Assert.Contains(paths, pattern => TriggerMap.GlobMatches(pattern, path));
        }
    }

    [Theory]
    [InlineData("docs/ci/test-trigger-map.md")]
    [InlineData("extension/src/extension.ts")]
    [InlineData("tests/Infrastructure.Tests/TestTriggerMap/NativeHostingWorkflowTests.cs")]
    [InlineData("tests/Aspire.Hosting.Redis.Tests/RedisResourceTests.cs")]
    [InlineData("playground/NativeHosting/AtsServer/AtsSession.cs")]
    public void UnrelatedInputsDoNotTriggerNativeHostingLane(string path)
    {
        Assert.All(Paths(Events(), "pull_request"),
            pattern => Assert.False(TriggerMap.GlobMatches(pattern, path)));
    }

    [Fact]
    public void PushAndPullRequestFiltersStayAligned()
    {
        var events = Events();

        Assert.Equal(Paths(events, "pull_request"), Paths(events, "push"));
        Assert.True(events.Children.ContainsKey(new YamlScalarNode("workflow_dispatch")));
    }

    [Fact]
    public void IndependentLaneIsClassifiedWithoutInventingASelectorJob()
    {
        var map = TriggerMap.Load(Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml"));
        const string workflow = ".github/workflows/native-hosting-validation.yml";
        const string harness = "eng/scripts/test-native-hosting.sh";

        Assert.Contains(workflow, map.Ignore);
        Assert.Contains("playground/NativeIntegrations/TypeScript/**", map.Ignore);
        foreach (var path in new[] { workflow, harness })
        {
            var targets = map.PathRules
                .Where(rule => rule.Paths.Any(pattern => TriggerMap.GlobMatches(pattern, path)))
                .SelectMany(rule => rule.Targets)
                .Distinct()
                .ToArray();

            Assert.Equal(["test:Infrastructure.Tests"], targets);
        }
    }

    [Fact]
    public void NativeHostingJobRunsHarnessAndUploadsEvidenceOnFailure()
    {
        var root = Workflow();
        var permissions = (YamlMappingNode)root.Children[new YamlScalarNode("permissions")];
        Assert.Equal("read", permissions.Children[new YamlScalarNode("contents")].ToString());
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var job = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("native_hosting")]);
        Assert.Equal("ubuntu-latest", job.Children[new YamlScalarNode("runs-on")].ToString());
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        var steps = (YamlSequenceNode)job.Children[new YamlScalarNode("steps")];
        var nodeSetup = Assert.Single(steps.Cast<YamlMappingNode>(),
            step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses)
                && uses.ToString().StartsWith("actions/setup-node@", StringComparison.Ordinal));
        var nodeInputs = (YamlMappingNode)nodeSetup.Children[new YamlScalarNode("with")];
        Assert.Equal("false", nodeInputs.Children[new YamlScalarNode("package-manager-cache")].ToString());
        Assert.False(nodeInputs.Children.ContainsKey(new YamlScalarNode("cache")));
        var harness = Assert.Single(steps.Cast<YamlMappingNode>(),
            step => step.Children.ContainsKey(new YamlScalarNode("run")));
        Assert.Equal("bash eng/scripts/test-native-hosting.sh linux-x64",
            harness.Children[new YamlScalarNode("run")].ToString());
        Assert.False(harness.Children.ContainsKey(new YamlScalarNode("if")));
        Assert.False(harness.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        var evidence = Assert.Single(steps.Cast<YamlMappingNode>(),
            step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses)
                && uses.ToString().StartsWith("actions/upload-artifact@", StringComparison.Ordinal));
        Assert.Equal("always()", evidence.Children[new YamlScalarNode("if")].ToString());
    }

    [Fact]
    public void WindowsLaneExercisesThePublishedServerThroughTheRealCli()
    {
        var jobs = (YamlMappingNode)Workflow().Children[new YamlScalarNode("jobs")];
        var job = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("windows_rpc")]);
        Assert.Equal("windows-latest", job.Children[new YamlScalarNode("runs-on")].ToString());
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
        var steps = (YamlSequenceNode)job.Children[new YamlScalarNode("steps")];
        var validation = Assert.Single(steps.Cast<YamlMappingNode>(),
            step => step.Children.ContainsKey(new YamlScalarNode("run")));
        var script = validation.Children[new YamlScalarNode("run")].ToString();
        Assert.Contains("ASPIRE_NATIVE_PRODUCT_CLI", script);
        Assert.Contains("ASPIRE_NATIVE_CSHARP_TEST_SERVER", script);
        Assert.Contains("*.NativeProductCliTests", script);
        Assert.Contains("*.NativeCliProtocolTests", script);
        Assert.Contains("*.NativeAuxiliaryBackchannelTests", script);
        Assert.Contains("*.NativeSocketIsolationTests", script);
        Assert.Contains("*.DcpEnvironmentTests", script);
        Assert.Contains("*.SocketConnectionListenerTests", script);
        Assert.False(validation.Children.ContainsKey(new YamlScalarNode("continue-on-error")));
    }

    private static string[] Paths(YamlMappingNode events, string eventName)
    {
        var trigger = (YamlMappingNode)events.Children[new YamlScalarNode(eventName)];

        return ((YamlSequenceNode)trigger.Children[new YamlScalarNode("paths")])
            .Select(path => path.ToString()).ToArray();
    }

    private static YamlMappingNode Events() =>
        (YamlMappingNode)Workflow().Children[new YamlScalarNode("on")];

    private static YamlMappingNode Workflow()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "native-hosting-validation.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);

        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }
}
